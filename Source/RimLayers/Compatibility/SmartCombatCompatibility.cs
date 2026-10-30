using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Smart Combat (Immersive Warfare, packageId <c>lordfelix.SmartCombat</c>) keeps a private
/// <c>NativeArray&lt;ushort&gt; offsets</c> per-cell cost grid in <c>SmartCombat.OrdinaryRoadMap</c>,
/// allocated with <c>map.cellIndices.NumGridCells</c> only once a pawn starts an ordinary job while
/// its road preference is enabled. Because it is a lazily allocated native buffer, layered map
/// expansion used to refuse the whole transaction as soon as the player had walked around enough
/// for it to exist: <c>Unmapped native per-cell buffer:
/// SmartCombat.OrdinaryRoadMap.offsets</c>, so a stair could never add a floor.
///
/// <para>This adapter does not grow the grid. The cost grid is a pure derivative of terrain, roof
/// and the pawn's job, and Smart Combat already knows how to rebuild it from scratch: its own
/// <c>PrepareSafeBatch</c> disposes the grid whenever <c>offsets.Length != NumGridCells</c> and then
/// refills all cells, invalidating the native path grid through <c>RecycleGridJobData</c>. So the
/// adapter only disposes it inside the expansion transaction, which is exactly the state that
/// self-heal expects. Migrating the prefix instead would preserve stale costs for a map whose
/// appended rows were never evaluated, and <c>roadCount</c> (maintained alongside a companion
/// <c>bool[] roadCells</c>) would no longer match the cell grid.</para>
///
/// <para><c>DisposeGrid</c> is safe here: it needs no map, it is idempotent through
/// <c>NativeArray.IsCreated</c>, and map expansion completes every scheduled path job before any
/// buffer is touched, so no native view is still being read.</para>
/// </summary>
internal static class SmartCombatCompatibility
{
    internal const string PackageId = "lordfelix.SmartCombat";
    private const string TypeName = "SmartCombat.OrdinaryRoadMap";
    // Resolved lazily: a Mod constructor runs before other mods' assemblies are reachable through
    // TypeByName, so a static readonly lookup here would always see null and silently do nothing.
    private static Type? componentType;
    private static bool resolved;
    private static FieldInfo? offsets;
    private static MethodInfo? disposeGrid;
    private static MethodInfo? prepareBatch;
    internal static bool Active => Resolve() && offsets != null && disposeGrid != null;
    internal static Type? ComponentType => Resolve() ? componentType : null;

    /// <summary>
    /// <c>ModLister.GetActiveModWithIdentifier</c> matches <c>ModsConfig</c>'s raw string set through
    /// <c>ToLower()</c>, so a hand-edited <c>ModsConfig.xml</c> that keeps the pack's mixed-case
    /// <c>packageId</c> makes <c>ModMetaData.Active</c> false even though the assembly still loads. The
    /// running-mod scan does not care about case and sees exactly what was loaded.
    /// </summary>
    private static bool Loaded()
    {
        try
        {
            if (ModLister.GetActiveModWithIdentifier(PackageId, false) != null) return true;
            return LoadedModManager.RunningModsListForReading
                .Any(pack => pack.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Declares the in-transaction discard hook. Safe to call when Smart Combat is absent, and safe
    /// to call twice (registration is keyed by type).
    /// </summary>
    internal static void Install()
    {
        if (!Loaded()) return;
        if (!Resolve() || componentType == null) return;
        offsets = AccessTools.Field(componentType, "offsets");
        disposeGrid = AccessTools.Method(componentType, "DisposeGrid", Type.EmptyTypes);
        prepareBatch = AccessTools.Method(componentType, "PrepareSafeBatch");
        if (!Active)
        {
            // The field or the disposal moved. Keep the old safe refusal rather than dropping a
            // buffer we can no longer release.
            Log.Warning("[RimLayers] Smart Combat OrdinaryRoadMap layout changed; layered map expansion stays disabled on those maps until the adapter is updated.");
            return;
        }
        LayerCellBufferCompatibility.RegisterBeforeResize(componentType, DiscardGrid);
        Log.Message("[RimLayers] Smart Combat ordinary-road cost grid is released before layered map expansion.");
    }

    /// <summary>The road component Smart Combat attaches to every map, or null when it is absent.</summary>
    internal static MapComponent? ComponentOn(Map map) => Active && componentType != null ? map.GetComponent(componentType) : null;

    /// <summary>The live per-cell cost grid, boxed as the component's own <c>NativeArray&lt;ushort&gt;</c>.</summary>
    internal static object? GridOn(MapComponent? component) => Active && component != null ? offsets!.GetValue(component) : null;

    /// <summary>
    /// Runs Smart Combat's own lazy batch so diagnostics can allocate the grid the way a colonist
    /// walking to a job does, instead of writing the private fields by hand.
    /// </summary>
    internal static bool TryPrepareBatch(MapComponent? component, out bool changed)
    {
        changed = false;
        if (!Active || prepareBatch == null || component == null) return false;
        object?[] args = [null];
        prepareBatch.Invoke(component, args);
        changed = args[0] is true;
        return true;
    }

    private static bool Resolve()
    {
        if (resolved) return componentType != null;
        componentType = AccessTools.TypeByName(TypeName);
        resolved = componentType != null;
        return resolved;
    }

    /// <summary>
    /// Runs inside the expansion transaction, before the generic scan reads any field, so the
    /// unregistered native grid is already gone when it is classified. The next
    /// <c>PrepareSafeBatch</c> sees <c>!offsets.IsCreated</c> and refills the grid at the new size.
    /// </summary>
    private static void DiscardGrid(object component)
    {
        try
        {
            disposeGrid!.Invoke(component, null);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("[RimLayers] Could not release the Smart Combat ordinary-road cost grid before expansion.", e);
        }
    }
}

/// <summary>
/// Runs once after every active mod's assembly is loaded and all defs are resolved.
/// <see cref="Mod"/> constructors run before that point, so <c>AccessTools.TypeByName</c> cannot see
/// other mods' types from there; this is the earliest safe place to detect Smart Combat.
/// </summary>
[StaticConstructorOnStartup]
internal static class SmartCombatBootstrap
{
    static SmartCombatBootstrap()
    {
        try { SmartCombatCompatibility.Install(); }
        catch (Exception e) { Log.Error($"[RimLayers] Smart Combat compatibility setup failed: {e}"); }
    }
}
