using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Cross-band adjacency for the Dubs-family pipe networks (Dubs Bad Hygiene, Dubs Central Heating,
/// Rimefeller, Dubs Rimatomics).
///
/// <para>All four ship the same topology code: <c>RebuildPipeGrid(int)</c> runs
/// <c>map.floodFiller.FloodFill(root, passCheck, processor, int.MaxValue, false, null)</c>, and the
/// pass check does the bookkeeping itself (it writes <c>GridID</c>, <c>pipeNetRef</c>,
/// <c>PipeGrid[mode, index]</c> and <c>PipedThings</c>). <see cref="FloodFiller"/> only walks
/// <see cref="GenAdj.CardinalDirectionsAround"/>, i.e. four same-band neighbours, so two bands of one
/// colony can never share a net.</para>
///
/// <para><b>Why <c>extraRoots</c> cannot be used.</b> <see cref="FloodFiller"/> enqueues extra roots
/// <em>without</em> calling the pass check (it only does <c>visited.Add</c>, <c>traversalDistance = 0</c>
/// and <c>openSet.Enqueue</c>), and passing any extra root also skips the root's own early-return
/// check. A cell pushed that way is never recorded, so the caller's next
/// <c>RebuildPipeGrid</c> still sees <c>GridID == -1</c> for it and opens a second net that steals
/// the band.</para>
///
/// <para><b>Why a pass-check wrapper cannot work either.</b> The flood only calls <c>passCheck</c> on a
/// <em>plane neighbour</em> of a dequeued cell, so the opposite band's cell is never offered to it; a
/// wrapper that "also tries the cell above" never gets the opportunity.</para>
///
/// <para><b>What this does instead.</b> It replaces the flood with an equivalent breadth-first walk
/// that calls the caller's own pass check in exactly the same places (so every mod keeps its own
/// bookkeeping), and additionally lets a dequeued cell expand into the band above/below when — and
/// only when — that cell carries a <see cref="CompLayerPort"/>. The replacement runs only inside a
/// pipe rebuild on a map that actually has a port; every other flood in the game uses the native
/// implementation unchanged.</para>
/// </summary>
internal static class LayerPipePorts
{
    // Nested expansions are normal here (RegenPipeGrids -> RebuildPipeGrid -> FloodFill), so the gate
    // is a counter, not a boolean. Port place/remove can also force an immediate rebuild.
    [ThreadStatic] private static int depth;
    [ThreadStatic] private static int[]? visitedStamp;
    [ThreadStatic] private static int[]? distance;
    [ThreadStatic] private static int stamp;
    [ThreadStatic] private static Queue<IntVec3>? open;
    [ThreadStatic] private static bool replacing;

    private static AccessTools.FieldRef<FloodFiller, Map>? fillerMap;
    private static MethodInfo? flood;

    private sealed class Binding
    {
        internal readonly Type Comp;
        internal readonly List<MethodInfo> Entries = new();
        /// <summary>The same entry points without arguments, i.e. the ones safe to invoke directly.</summary>
        internal readonly List<MethodInfo> Rebuilds = new();
        private readonly MethodInfo dirtyAll;
        private readonly FieldInfo? fullRebuild;
        internal Binding(Type comp)
        {
            Comp = comp;
            dirtyAll = AccessTools.Method(comp, "DirtyAllPipeGrids")
                ?? throw new MissingMethodException(comp.FullName, "DirtyAllPipeGrids");
            // Every one of the four funnels its rebuilds through these; the outer one covers the
            // incremental path as well, which must also see the bridge or it would re-split a net
            // that crosses a band boundary.
            foreach (string name in new[] { "RegenPipeGrids", "RegenGrids", "RebuildPipeGrid" })
                if (AccessTools.Method(comp, name, name == "RebuildPipeGrid" ? new[] { typeof(int) } : Type.EmptyTypes) is { } entry)
                {
                    Entries.Add(entry);
                    if (name != "RebuildPipeGrid") Rebuilds.Add(entry);
                }
            if (Entries.Count == 0)
                throw new MissingMethodException(comp.FullName, "RebuildPipeGrid(int)");
            // Dubs Bad Hygiene, Dubs Central Heating and Rimefeller expose FullRebuild; Rimatomics has
            // no incremental path at all, so the flag simply does not exist there.
            fullRebuild = AccessTools.Field(comp, "FullRebuild");
        }
        /// <summary>
        /// Only the full rebuild can cross a band boundary: the incremental path re-seeds from
        /// <c>changedCells x (self + four plane neighbours)</c> and can never discover a vertical edge.
        /// </summary>
        internal void ForceFullRebuild(MapComponent component)
        {
            if (fullRebuild?.GetValue(component) is bool[] flags)
                for (int i = 0; i < flags.Length; i++) flags[i] = true;
            dirtyAll.Invoke(component, null);
        }

        /// <summary>
        /// Dirtied is not the same as rebuilt: Bad Hygiene, Central Heating and Rimefeller only
        /// regenerate from <c>FinalizeInit</c>, <c>MapGenerated</c> or a multiplayer pipe change, and
        /// <c>MapComponentTick</c> only ticks existing nets. After a live expansion the grid has to be
        /// rebuilt now, so the network is not left pointing at indices that no longer exist.
        /// </summary>
        internal void RebuildNow(MapComponent component)
        {
            ForceFullRebuild(component);
            if (Rebuilds.Count == 0) return;
            Rebuilds[0].Invoke(component, null);
        }
    }

    private static readonly List<Binding> bindings = new();

    /// <summary>Spawned ports per map, keyed by <see cref="Map.uniqueID"/>. No port means native flooding is untouched.</summary>
    private static readonly Dictionary<int, int> portCount = new();

    private static bool installed;

    internal static bool Active => installed;

    /// <summary>
    /// Binds whichever of the four pipe map components are present and installs the flood hook.
    /// Safe to call repeatedly; returns false when nothing was installed.
    /// </summary>
    internal static bool Install(Harmony harmony)
    {
        if (installed) return true;
        if (bindings.Count == 0)
        {
            foreach (var (packageId, typeName) in new[]
                     {
                         ("dubwise.dubsbadhygiene", "DubsBadHygiene.HygienePipeMapComp"),
                         ("dubwise.dubscentralheating", "DubsCentralHeating.HygienePipeMapComp"),
                         ("dubwise.rimefeller", "Rimefeller.UniversalPipeMapComp"),
                         ("dubwise.rimatomics", "Rimatomics.UniversalPipeMapComp")
                     })
            {
                if (ModLister.GetActiveModWithIdentifier(packageId, false) == null) continue;
                if (AccessTools.TypeByName(typeName) is not { } comp) continue;
                try { bindings.Add(new Binding(comp)); }
                catch (Exception exception)
                {
                    Log.Warning($"[RimLayers] {typeName} pipe API changed; its networks stay same-band.\n{exception}");
                }
            }
        }
        if (bindings.Count == 0) return false;
        if (installed) return true;
        fillerMap = AccessTools.FieldRefAccess<FloodFiller, Map>("map");
        flood = AccessTools.Method(typeof(FloodFiller), nameof(FloodFiller.FloodFill),
            new[] { typeof(IntVec3), typeof(Predicate<IntVec3>), typeof(Func<IntVec3, int, bool>), typeof(int), typeof(bool), typeof(IEnumerable<IntVec3>) });
        if (flood == null || fillerMap == null)
        {
            Log.Warning("[RimLayers] FloodFiller.FloodFill changed; modded pipes stay same-band.");
            bindings.Clear();
            return false;
        }
        harmony.Patch(flood, prefix: new HarmonyMethod(typeof(LayerPipePorts), nameof(FloodPrefix)));
        // The gate has to be open while the owning mod rebuilds its own networks; the flood prefix
        // above only ever runs from inside one of these rebuild entries.
        foreach (var binding in bindings)
        {
            foreach (var entry in binding.Entries)
            {
                try
                {
                    harmony.Patch(entry,
                        prefix: new HarmonyMethod(typeof(LayerPipePorts), nameof(RebuildPrefix)),
                        finalizer: new HarmonyMethod(typeof(LayerPipePorts), nameof(RebuildFinalizer)));
                }
                catch (Exception exception)
                {
                    Log.Warning($"[RimLayers] Could not hook {binding.Comp.Name}.{entry.Name}; that path stays same-band.\n{exception}");
                }
            }
        }
        installed = true;
        Log.Message($"[RimLayers] Cross-band pipe ports enabled for {bindings.Count} pipe network(s).");
        return true;
    }

    internal static void Register(Thing port)
    {
        if (port.Map is not { } map) return;
        int id = map.uniqueID;
        portCount.TryGetValue(id, out int count);
        portCount[id] = count + 1;
        Refresh(map);
    }

    internal static void Unregister(Thing port)
    {
        if (port.Map is not { } map) return;
        int id = map.uniqueID;
        if (portCount.TryGetValue(id, out int count))
        {
            if (count <= 1) portCount.Remove(id);
            else portCount[id] = count - 1;
        }
        Refresh(map);
    }

    internal static void MapRemoved(Map map) => portCount.Remove(map.uniqueID);

    internal static bool HasPorts(Map map) => portCount.TryGetValue(map.uniqueID, out int count) && count > 0;

    /// <summary>
    /// The layers directly above and below a port that also hold a pipe of the same network. Advisory
    /// only: it mirrors what the flood bridge will discover, so a port can say whether it is connected.
    /// </summary>
    internal static List<string> LinkedLayers(Thing port)
    {
        var result = new List<string>();
        if (port?.Map is not { } map || !port.Spawned) return result;
        if (LayerMapComponent.For(map) is not { } owner || !owner.TryDecode(port.Position, out var cell)) return result;
        if (!CarriesPort(map, port.Position)) return result;
        for (int direction = -1; direction <= 1; direction += 2)
        {
            IntVec3 peer = owner.Adjacent(port.Position, direction);
            if (!peer.IsValid || !peer.InBounds(map) || !owner.TryDecode(peer, out var other)) continue;
            if (CarriesPort(map, peer)) result.Add(owner.LevelName(other.LayerId));
        }
        return result;
    }

    /// <summary>Forces a full rebuild of every bound network on the map.</summary>
    internal static void Refresh(Map map)
    {
        if (map == null || map.Disposed || map.components == null) return;
        Enter();
        try
        {
            foreach (var component in map.components)
                foreach (var binding in bindings)
                    if (binding.Comp.IsInstanceOfType(component))
                    {
                        try { binding.ForceFullRebuild(component); }
                        catch (Exception exception) { Log.Warning($"[RimLayers] Could not dirty {binding.Comp.Name}: {exception.Message}"); }
                    }
        }
        finally { Exit(); }
    }

    /// <summary>
    /// A live expansion replaced every per-cell buffer with a longer one, so each bound network holds
    /// net ids and <c>PipeGrid</c> rows whose length no longer matches the map: <c>Array.CreateInstance</c>
    /// zero-fills the appended cells and the mods read 0 as a real net id (only -1 means "no pipe").
    /// Rebuilding now is what turns those rows back into a valid grid.
    /// </summary>
    internal static void ExpansionCompleted(Map map)
    {
        if (map == null || map.Disposed || map.components == null) return;
        int cells = map.cellIndices.NumGridCells;
        Enter();
        try
        {
            foreach (var component in map.components)
                foreach (var binding in bindings)
                {
                    if (!binding.Comp.IsInstanceOfType(component)) continue;
                    try
                    {
                        // Only force the eager path when the buffer actually had to grow. On a freshly
                        // generated map the components were constructed at full size and their own
                        // FinalizeInit rebuild is still ahead of us.
                        if (AccessTools.Field(binding.Comp, "PipeGrid")?.GetValue(component) is Array grid
                            && grid.Rank == 2 && grid.GetLength(1) != cells) binding.RebuildNow(component);
                        else binding.ForceFullRebuild(component);
                    }
                    catch (Exception exception)
                    {
                        Log.Warning($"[RimLayers] Could not rebuild {binding.Comp.Name} after the expansion: {exception.Message}");
                    }
                }
        }
        finally { Exit(); }
    }

    /// <summary>Enters the gate so nested rebuilds all see the vertical bridge.</summary>
    internal static void Enter() => depth++;

    internal static void Exit() { if (depth > 0) depth--; }

    private static void RebuildPrefix() => Enter();

    // A finalizer runs exactly once whether or not the rebuild threw, so depth cannot leak.
    private static void RebuildFinalizer() => Exit();

    private static bool FloodPrefix(IntVec3 root, Predicate<IntVec3> passCheck, Func<IntVec3, int, bool> processor,
        int maxCellsToProcess, bool rememberParents, IEnumerable<IntVec3> extraRoots, FloodFiller __instance)
    {
        if (depth == 0 || replacing) return true;
        // The bound rebuilds always pass false/null here; reproducing those two native paths would be
        // a second copy of native behaviour for no gain.
        if (rememberParents || extraRoots != null) return true;
        if (fillerMap == null || passCheck == null || processor == null) return true;
        Map map;
        try { map = fillerMap(__instance); }
        catch (Exception) { return true; }
        if (map == null || map.Disposed || LayerMapComponent.For(map) is not { } owner || !HasPorts(map)) return true;
        Replace(map, owner, root, passCheck, processor, maxCellsToProcess);
        return false;
    }

    /// <summary>
    /// Native-equivalent breadth-first walk (same pass-check placement, same maxCellsToProcess
    /// accounting, same overflow guard) with one addition: a dequeued cell carrying a port may also
    /// expand into the band directly above and below it.
    /// </summary>
    private static void Replace(Map map, LayerMapComponent owner, IntVec3 root, Predicate<IntVec3> passCheck,
        Func<IntVec3, int, bool> processor, int maxCellsToProcess)
    {
        CellIndices indices = map.cellIndices;
        int cells = indices.NumGridCells;
        if (visitedStamp == null || visitedStamp.Length != cells)
        {
            visitedStamp = new int[cells];
            distance = new int[cells];
            stamp = 0;
        }
        if (++stamp == int.MaxValue)
        {
            Array.Clear(visitedStamp, 0, cells);
            stamp = 1;
        }
        open ??= new Queue<IntVec3>();
        Queue<IntVec3> queue = open;
        queue.Clear();
        replacing = true;
        try
        {
            if (root.IsValid && !passCheck(root)) return;
            IntVec3[] around = GenAdj.CardinalDirectionsAround;
            int area = map.Area;
            int processed = 0;
            if (root.IsValid) Enqueue(root, 0);
            while (queue.Count > 0)
            {
                IntVec3 cell = queue.Dequeue();
                int travelled = distance![indices.CellToIndex(cell)];
                if (processor(cell, travelled)) break;
                if (++processed == maxCellsToProcess) break;
                for (int k = 0; k < around.Length; k++)
                    TryEnqueue(cell + around[k], travelled + 1);
                // A deliberate port is the only way across a band boundary, so an unrelated network
                // can never be welded to a passing one.
                if (!CarriesPort(map, cell)) continue;
                for (int direction = -1; direction <= 1; direction += 2)
                    TryEnqueue(owner.Adjacent(cell, direction), travelled + 1);
            }
            if (queue.Count > area)
                Log.Error("Overflow on flood fill (>" + area + " cells). Make sure we're not flooding over the same area after we check it.");

            void TryEnqueue(IntVec3 next, int nextDistance)
            {
                if (!next.IsValid || !next.InBounds(map)) return;
                int index = indices.CellToIndex(next);
                if (visitedStamp![index] == stamp) return;
                if (!passCheck(next)) return;
                Enqueue(next, nextDistance);
            }

            void Enqueue(IntVec3 cell, int cellDistance)
            {
                int index = indices.CellToIndex(cell);
                visitedStamp![index] = stamp;
                distance![index] = cellDistance;
                queue.Enqueue(cell);
            }
        }
        finally
        {
            replacing = false;
            queue.Clear();
        }
    }

    private static bool CarriesPort(Map map, IntVec3 cell)
    {
        var things = map.thingGrid.ThingsListAt(cell);
        for (int i = 0; i < things.Count; i++)
            if (things[i] is ThingWithComps { Spawned: true, Destroyed: false } comps && comps.GetComp<CompLayerPort>() != null)
                return true;
        return false;
    }
}

/// <summary>
/// Runs once after every active mod's assembly is loaded and all defs are resolved.
/// <see cref="Mod"/> constructors run before that point, so <c>AccessTools.TypeByName</c> cannot see
/// other mods' types from there; this is the earliest safe place to bind their pipe components.
/// </summary>
[StaticConstructorOnStartup]
internal static class LayerPipePortsBootstrap
{
    static LayerPipePortsBootstrap()
    {
        try { LayerPipePorts.Install(new Harmony(RimLayersMod.HarmonyId + ".pipeports")); }
        catch (Exception e) { Log.Error($"[RimLayers] Pipe port compatibility setup failed: {e}"); }
    }
}

/// <summary>Clears the per-map port count when a map goes away.</summary>
[HarmonyPatch(typeof(Map), nameof(Map.Dispose))]
internal static class LayerPipePortsMapDisposePatch
{
    private static void Postfix(Map __instance) => LayerPipePorts.MapRemoved(__instance);
}

/// <summary>
/// Marks a building as a deliberate cross-band connector. The def must still be a real pipe of the
/// owning mod's own medium (Dubs, Rimefeller and Rimatomics all refuse to register two pipe comps in
/// one cell, and Dubs destroys a same-medium pipe that overlaps), so this comp only opts the building
/// into the vertical projection; the mod itself keeps registering and merging it natively.
/// </summary>
public sealed class CompProperties_LayerPort : CompProperties
{
    public CompProperties_LayerPort() => compClass = typeof(CompLayerPort);
}

public sealed class CompLayerPort : ThingComp
{
    public override void PostSpawnSetup(bool respawningAfterLoad)
    {
        base.PostSpawnSetup(respawningAfterLoad);
        LayerPipePorts.Register(parent);
    }

    public override void PostDeSpawn(Map map, DestroyMode mode)
    {
        LayerPipePorts.Unregister(parent);
        base.PostDeSpawn(map, mode);
    }

    public override string CompInspectStringExtra()
    {
        if (LayerMapComponent.For(parent.Map) is null) return "RL_RequiresLayerMap".Translate();
        var layers = LayerPipePorts.LinkedLayers(parent);
        return layers.Count == 0
            ? "RL_PipeNoPeer".Translate()
            : "RL_PipePeers".Translate(string.Join(", ", layers));
    }
}
