using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Optional SOS2 (Save Our Ship 2) bindings. RimLayers never compiles against
/// ShipsHaveInsides.dll; every SOS2 type is resolved by name and a missing type is a hard failure at
/// Install() time, exactly like the VGE adapter.
/// </summary>
/// <remarks>
/// <para>
/// The two frameworks model ships in ways that cannot be merged. An Odyssey gravship is a RimLayers
/// payload owned by a <c>Building_GravEngine</c> and enumerated by <c>RimLayers.Ships</c>. An SOS2
/// ship is a flat <c>Dictionary&lt;IntVec3, Tuple&lt;int,int&gt;&gt; MapShipCells</c> on its own
/// <c>ShipMapComp</c>, owned by a <c>Building_ShipBridge</c>. SOS2's engines are plain
/// <c>Building</c>s (Buildings_Ship.xml declares <c>&lt;thingClass&gt;Building&lt;/thingClass&gt;</c>),
/// so RimLayers' engine test can never claim one, and it must not: this adapter deliberately touches
/// nothing under <c>RimLayers.Ships</c>.
/// </para>
/// <para>
/// What SOS2 does share is the storage-coordinate hazard. Because RimLayers stores a layer at
/// <c>z = layer * Stride + localZ</c>, SOS2's assumptions that <c>Map.Size.z</c> is the logical
/// north/south length and that cardinal adjacency stays on one deck are wrong on a layered map. The
/// helpers in <see cref="Sos2Geometry"/> answer both questions, and the patches here route SOS2's
/// own algorithms through them.
/// </para>
/// </remarks>
internal static class Sos2Compatibility
{
    private const string AssemblyName = "ShipsHaveInsides";

    internal static bool Active { get; private set; }

    // Resolved once. Nothing here is a compile-time dependency.
    private static Type? shipMapComp;
    private static Type? shipCachePart;
    private static Type? shipCachePartProps;
    private static Type? shipBridge;
    private static Type? shipInterior;
    private static Type? shipHeatComp;
    private static MethodInfo? mapShipCellsGetter;
    private static MethodInfo? shipsOnMapGetter;
    private static MethodInfo? addToCache;
    private static FieldInfo? shipMapGrid;
    private static FieldInfo? heatGridDirty;
    private static FieldInfo? breathableZoneDirty;
    private static MethodInfo? shipIndexOnVec;

    internal static void Install(Harmony harmony)
    {
        if (!ModsConfig.IsActive("kentington.saveourship2")) return;
        var assembly = AccessTools.TypeByName("SaveOurShip2.ShipMapComp")?.Assembly;
        if (assembly == null || assembly.GetName().Name != AssemblyName)
            throw new InvalidOperationException("[RimLayers] Save Our Ship 2 is active but ShipsHaveInsides.dll was not found.");

        shipMapComp = Require(assembly, "SaveOurShip2.ShipMapComp");
        shipCachePart = Require(assembly, "SaveOurShip2.CompShipCachePart");
        shipCachePartProps = Require(assembly, "SaveOurShip2.CompProps_ShipCachePart");
        shipBridge = Require(assembly, "SaveOurShip2.Building_ShipBridge");
        shipInterior = Require(assembly, "SaveOurShip2.ShipInteriorMod2");
        shipHeatComp = Require(assembly, "SaveOurShip2.CompShipHeat");

        // MapShipCells is a lazy-initialising PROPERTY, not a field: AccessTools.Field returns null
        // for it and every ship test would silently degrade to false. It is also declared on
        // ShipMapComp, so MethodDelegate<Func<MapComponent,...>> cannot bind it (the delegate would
        // need the concrete type the adapter deliberately has no reference to) - invoke it instead.
        mapShipCellsGetter = AccessTools.PropertyGetter(shipMapComp, "MapShipCells");
        shipMapGrid = AccessTools.Field(shipMapComp, "grid");
        heatGridDirty = AccessTools.Field(shipMapComp, "heatGridDirty");
        breathableZoneDirty = AccessTools.Field(shipMapComp, "breathableZoneDirty");

        // Deck predicate: an SOS2 hull plating / floor is a walkable deck for the layer above it.
        // AnyPart covers plating, hull walls and hardpoints; the MapShipCells membership test keeps
        // this to cells SOS2 itself considers part of a ship.
        StructuralSupport.RegisterDeckSource((owner, cell) => IsShipDeck(owner, cell));
        Sos2HullPlacementPatches.Install(harmony);
        // SOS2 roofs its hull with RoofShip (Defs/Misc/RoofDefs/Roofs.xml), which is not one of the
        // two native support roofs.
        StructuralSupport.RegisterSupportRoof(roof => roof != null && roof.defName == "RoofShip");
        LiveMapExpansion.Committed += OnExpanded;
        // A hull's decks are storage bands, so a map it enters must be at least as tall as the hull.
        LiveMapExpansion.RegisterRequirement(RequiredElevation);

        Active = true;
        // Every patch class is applied in isolation. This runs from the Mod constructor, where a
        // rethrown transpiler failure aborts the whole mod and the game looks inert; the class-processor
        // loop that normally isolates failures has not run yet. A reshaped SOS2 costs one patch here
        // instead of every later RimLayers patch class.
        Patch(harmony, typeof(Sos2AdjacencyPatches));
        Patch(harmony, typeof(Sos2GeometryPatches));
        Patch(harmony, typeof(Sos2HeatPatches));
        Patch(harmony, typeof(Sos2FoamPatches));
        Patch(harmony, typeof(Sos2SubsystemPatches));
        Patch(harmony, typeof(Sos2PlacementPatches));
        Patch(harmony, typeof(Sos2DeckRegistryPatches));
        Patch(harmony, typeof(Sos2DeckMovePatch));
        Patch(harmony, typeof(Sos2RepowerPatches));
        Patch(harmony, typeof(Sos2MoveGhostPatches));
        Patch(harmony, typeof(Sos2SketchFloorPatch));
        Patch(harmony, typeof(Sos2LaunchFocusPatch));
        Patch(harmony, typeof(Sos2TerrainPatches));
        Log.Message("[RimLayers] Save Our Ship 2 adapter installed (multi-deck hulls, banded geometry, heat nets across decks).");
    }

    /// <summary>
    /// Patch classes this adapter already installed. <c>RimLayersMod</c> walks every
    /// <c>[HarmonyPatch]</c> type in the assembly, so without this list each SOS2 class would be
    /// processed twice and every prefix/postfix would run twice per call.
    /// </summary>
    private static readonly HashSet<Type> installed = new();

    /// <summary>True when <paramref name="type"/> was already patched by this adapter.</summary>
    internal static bool AlreadyInstalled(Type type) => installed.Contains(type);

    private static void Patch(Harmony harmony, Type type)
    {
        if (!installed.Add(type)) return;
        try
        {
            harmony.CreateClassProcessor(type).Patch();
        }
        catch (Exception exception)
        {
            Log.Error("[RimLayers] SOS2 patch class " + type.Name + " failed and was skipped; the rest of the adapter keeps working.");
            Log.Error(exception.ToString());
        }
    }

    /// <summary>
    /// The <c>AllowsPlacing</c> method of one of SOS2's ship placement workers, or null when the
    /// worker type is gone. Returned as <c>MethodBase</c> because a <c>PlaceWorker</c> subclass method
    /// is what Harmony must patch; the adapter has no compile-time reference to it.
    /// </summary>
    internal static System.Reflection.MethodBase? PlacementWorkerMethod(string workerName)
    {
        Type? assemblyOwner = shipMapComp;
        if (assemblyOwner == null) return null;
        Type? worker = assemblyOwner.Assembly.GetType("SaveOurShip2." + workerName, throwOnError: false);
        return worker == null ? null : AccessTools.Method(worker, "AllowsPlacing");
    }

    private static Type Require(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: false) ??
        throw new InvalidOperationException($"[RimLayers] SOS2 type not found: {name}");

    /// <summary>True when the map is an SOS2 space map, where empty cells are vacuum rather than air.</summary>
    /// <remarks>
    /// Read from the biome instead of calling SOS2's <c>Map.IsSpace()</c>: that extension indexes
    /// <c>ShipGameComp.Utility</c>, which is null until SOS2's own game component has spawned, and this
    /// predicate runs inside placement validation on freshly generated maps.
    /// </remarks>
    internal static bool IsSpaceMap(Map? map) =>
        map != null && map.Biome != null && map.Biome.defName == "OuterSpaceBiome";

    /// <summary>
    /// True when <paramref name="buildable"/> is an SOS2 hull part that SOS2 itself would allow here.
    /// </summary>
    /// <remarks>
    /// SOS2 has no "needs a floor" rule for its own parts: <c>PlaceWorker_ShipPlating</c> asks for an
    /// affordance, no blocking building, and nothing else, and it returns early for Odyssey space
    /// terrain. RimLayers used to answer first and reject with <c>RL_NoSurface</c>, because a cell with
    /// no deck yet is bare <c>RL_OpenAir</c> and has no surface. In space nobody is standing on anything
    /// - the vacuum carries the metal - so a hull part anywhere on an owned vessel is legitimate.
    /// <para>
    /// Deliberately narrow: only while SOS2 is loaded, only for defs that carry SOS2's ship-part comp,
    /// only on a space map, and only when the cell is already part of the vessel (or the deck below it
    /// is). Ordinary buildings, every other mod, and every ground map keep the normal rules.
    /// </para>
    /// </remarks>
    internal static bool AllowsHullPartInVacuum(LayerMapComponent owner, BuildableDef buildable, IntVec3 center, Rot4 rotation)
    {
        if (!Active || shipCachePart == null || !IsSpaceMap(owner.map)) return false;
        if (buildable is not ThingDef def || !def.HasComp(shipCachePart)) return false;
        foreach (IntVec3 cell in GenAdj.OccupiedRect(center, rotation, buildable.Size))
            if (!owner.TryDecode(cell, out LayerCell logical) || logical.LayerId == 0) return false;
        // In vacuum SOS2 itself is the authority. PlaceWorker_ShipPlating returns true outright on empty
        // Odyssey space, and elsewhere it only demands Heavy affordance - which RL_OpenAir now offers.
        // Demanding more than that here (a ship claim on this very cell, or deck already underneath)
        // rejects the part players use to EXTEND a vessel outward: a new hardpoint hangs on bare space
        // by design. SOS2 still vetoes blocks, fog and rock roof inside its own place worker.
        return true;
    }

    /// <summary>True when the cell holds an SOS2 ship part and belongs to some SOS2 ship.</summary>
    internal static bool IsShipDeck(LayerMapComponent owner, IntVec3 cell)
    {
        if (!Active || shipCachePart == null || !owner.TryDecode(cell, out _)) return false;
        // A hull tile on a lower band answers for itself. On the third deck and above the cell below is
        // another RimLayers deck, which carries no SOS2 ship-part comp, so the deck itself has to count
        // as hull - otherwise the player can lay two decks and never a third.
        if (StructuralSupport.IsSos2DeckTerrain(owner.map.terrainGrid.FoundationAt(cell)) ||
            StructuralSupport.IsSos2DeckTerrain(owner.map.terrainGrid.TerrainAt(cell)))
            return MapClaims(owner.map, cell) || HasRegisteredDeckBelow(owner, cell);
        if (!MapClaims(owner.map, cell)) return false;
        foreach (Thing thing in cell.GetThingList(owner.map))
            if (thing.def.HasComp(shipCachePart)) return true;
        return false;
    }

    /// <summary>True when some cell below this one is registered by the deck registry.</summary>
    private static bool HasRegisteredDeckBelow(LayerMapComponent owner, IntVec3 cell)
    {
        IntVec3 under = owner.Adjacent(cell, -1);
        return under.IsValid &&
               (MapClaims(owner.map, under) ||
                StructuralSupport.IsSos2DeckTerrain(owner.map.terrainGrid.FoundationAt(under)) ||
                StructuralSupport.IsSos2DeckTerrain(owner.map.terrainGrid.TerrainAt(under)));
    }

    /// <summary>True when the thing carries SOS2's heat comp, which is what a heat net is built from.</summary>
    /// <remarks>
    /// Checked against the live comp instances, not <c>def.HasComp</c>: SOS2 declares the heat comps as
    /// subclasses (<c>CompShipHeatSource</c>, <c>CompShipHeatSink</c>, ...) and a def-level lookup that
    /// compares the exact class would only ever match a bare <c>CompShipHeat</c>.
    /// </remarks>
    internal static bool IsHeatPart(Thing? thing)
    {
        if (thing == null || shipHeatComp == null) return false;
        return thing is ThingWithComps owner && owner.AllComps.Any(comp => shipHeatComp.IsInstanceOfType(comp));
    }

    internal static bool MapClaims(Map map, IntVec3 cell) => ShipCellsOf(map)?.Contains(cell) ?? false;

    /// <summary>
    /// True for a roof SOS2 treats as sealing a ship interior, which is what a deck may sit under.
    /// </summary>
    /// <remarks>
    /// Deliberately SOS2's own predicate instead of a defName list. <c>CompShipCachePart.SetRoof</c> writes
    /// <c>RoofConstructed</c> over a cell that already carries <c>RoofShip</c>: its branch is
    /// <c>!IsRoofDefAirtight(oldRoof) ? RoofShip : RoofConstructed</c>, and <c>IsRoofDefAirtight</c> accepts
    /// <c>RoofShip</c>, so every respawn (a load is enough) downgrades a hull's ship ceiling to the plain
    /// constructed roof. SOS2 does not care, because both are airtight to it, and third-party mods can add
    /// their own airtight roofs through <c>compatibleAirtightRoofs</c>. Matching on <c>RoofShip</c> alone
    /// therefore locks the deck out of any hull that has been reloaded once.
    /// </remarks>
    internal static bool IsShipCeiling(RoofDef? roof)
    {
        if (roof == null) return false;
        if (airtightRoof == null)
        {
            MethodInfo? method = AccessTools.Method(shipInterior, "IsRoofDefAirtight", new[] { typeof(RoofDef) });
            airtightRoof = method == null
                ? (Func<RoofDef, bool>)(def => def.defName == "RoofShip" || def == RoofDefOf.RoofConstructed)
                : (Func<RoofDef, bool>)(def => method.Invoke(null, new object[] { def }) is true);
        }
        try
        {
            return airtightRoof(roof);
        }
        catch (Exception exception)
        {
            Log.Warning("[RimLayers] SOS2 IsRoofDefAirtight failed; falling back to def name. " + exception.Message);
            return roof.defName == "RoofShip" || roof == RoofDefOf.RoofConstructed;
        }
    }

    private static Func<RoofDef, bool>? airtightRoof;

    /// <summary>
    /// SOS2's own <c>ShipMapComp.ShipIndexOnVec</c>, i.e. which ship owns a cell, or -1.
    /// </summary>
    /// <remarks>
    /// Reflected rather than reimplemented on purpose: SOS2 validates the index against
    /// <c>ShipsOnMap</c> and returns -1 for a cell whose ship was removed, and a second copy of that
    /// rule here would drift.
    /// </remarks>
    internal static int ShipIndexAt(Map? map, IntVec3 cell)
    {
        MapComponent? comp = ShipCompOf(map);
        if (comp == null) return -1;
        MethodInfo? method = shipIndexOnVec ??= AccessTools.Method(shipMapComp, "ShipIndexOnVec");
        if (method == null) return -1;
        try
        {
            return method.Invoke(comp, new object[] { cell }) is int index ? index : -1;
        }
        catch (Exception exception)
        {
            Log.Warning("[RimLayers] SOS2 ShipIndexOnVec failed; treating the cell as unclaimed. " + exception.Message);
            return -1;
        }
    }

    internal static MapComponent? ShipCompOf(Map? map) =>
        shipMapComp == null || map == null ? null : map.GetComponent(shipMapComp);

    /// <summary>The live <c>MapShipCells</c> dictionary, or null when SOS2 is absent.</summary>
    internal static IDictionary? ShipCellsOf(Map? map) => ShipCellsOf(ShipCompOf(map));

    internal static IDictionary? ShipCellsOf(MapComponent? comp) =>
        comp == null || mapShipCellsGetter == null ? null : mapShipCellsGetter.Invoke(comp, null) as IDictionary;

    internal static bool HasShipCells(MapComponent? comp) => comp != null && ShipCellsOf(comp) is { Count: > 0 };

    /// <summary>
    /// Highest elevation this map's ships occupy, or 0 when no ship is registered on it.
    /// </summary>
    /// <remarks>
    /// A hull stores one band per deck and writes its cells into <c>MapShipCells</c> and the home area
    /// one at a time. A destination with fewer bands than the hull has decks makes the first
    /// out-of-range cell throw inside <c>MoveShip</c>, leaving the hull half transferred - what the
    /// player sees as a ship stuck in the air and then collapsing. Reporting the need here lets
    /// <c>LiveMapExpansion.EnsureRequirements</c> grow the map through the normal expansion
    /// transaction before the move starts.
    /// </remarks>
    internal static int RequiredElevation(Map? map)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { } layout) return 0;
        if (ShipCellsOf(map) is not { } cells) return 0;
        int required = 0;
        foreach (object? key in cells.Keys)
        {
            if (key is not IntVec3 cell || !owner.TryDecode(cell, out LayerCell logical)) continue;
            int elevation = layout.ElevationOf(logical.LayerId);
            if (elevation > required) required = elevation;
        }
        return required;
    }

    /// <summary>The live <c>ShipsOnMap</c> dictionary, or null when SOS2 is absent.</summary>
    internal static IDictionary? ShipsOnMapOf(Map? map)
    {
        MapComponent? comp = ShipCompOf(map);
        if (comp == null) return null;
        MethodInfo? getter = shipsOnMapGetter ??= AccessTools.PropertyGetter(shipMapComp, "ShipsOnMap");
        return getter?.Invoke(comp, null) as IDictionary;
    }

    /// <summary>One <c>SpaceShipCache</c> by index, or null when that ship is gone.</summary>
    internal static object? ShipOf(IDictionary ships, int index) =>
        index < 0 || !ships.Contains(index) ? null : ships[index];

    /// <summary>The <c>Area</c> set of one ship, or null when the ship or the member is missing.</summary>
    internal static ICollection<IntVec3>? ShipAreaOf(IDictionary ships, int index)
    {
        object? ship = ShipOf(ships, index);
        return ship == null ? null : AccessTools.Field(ship.GetType(), "Area")?.GetValue(ship) as ICollection<IntVec3>;
    }

    /// <summary>True when the building carries SOS2's ship-part comp, i.e. SOS2 can cache it.</summary>
    internal static bool HasShipCachePart(Building building) =>
        shipCachePart != null && building.AllComps.Any(comp => shipCachePart.IsInstanceOfType(comp));

    /// <summary>Adds a building to a ship's cache through SOS2's own entry point.</summary>
    /// <remarks>
    /// <c>SpaceShipCache.AddToCache</c> is idempotent (<c>Buildings.Add</c> guards the body) and is the
    /// same method the adjacency walk calls, so a part it already adopted is left untouched.
    /// </remarks>
    internal static void AddBuildingToShip(object ship, Building building)
    {
        MethodInfo? add = addToCache ??= AccessTools.Method(ship.GetType(), "AddToCache", new[] { typeof(Building) });
        if (add == null) return;
        try
        {
            add.Invoke(ship, new object[] { building });
        }
        catch (Exception exception)
        {
            Log.Warning("[RimLayers] SOS2 AddToCache failed for " + building.def.defName + ". " + exception.Message);
        }
    }

    /// <summary>
    /// Rebuilds SOS2's per-cell heat grid after the map grew a band. The expansion copies the ground
    /// rows and leaves the appended bands at the array default (0); SOS2 reads <c>grid[i] == -1</c> as
    /// "not yet assigned to a heat net", so a zeroed band looks owned by net 0 and its pipes are
    /// never rebuilt.
    /// </summary>
    private static void OnExpanded(Map map, Layout.LayerLayout layout)
    {
        if (!Active || shipMapGrid == null) return;
        MapComponent? comp = ShipCompOf(map);
        if (comp == null) return;
        int cells = map.cellIndices.NumGridCells;
        if (shipMapGrid.GetValue(comp) is int[] grid && grid.Length == cells)
        {
            for (int i = 0; i < grid.Length; i++) grid[i] = -1;
        }
        else
        {
            // The generic scan normally grows the array for us, but it only recognises a buffer whose
            // length still matches the pre-expansion count. A mismatch here means SOS2 allocated its
            // grid at some other size, so own the allocation outright rather than leave a short array
            // that would index out of bounds on the first heat update.
            var rebuilt = new int[cells];
            for (int i = 0; i < rebuilt.Length; i++) rebuilt[i] = -1;
            shipMapGrid.SetValue(comp, rebuilt);
        }
        heatGridDirty?.SetValue(comp, true);
        breathableZoneDirty?.SetValue(comp, true);
    }

    internal static Type? ShipMapCompType => shipMapComp;
    internal static Type? ShipCachePartType => shipCachePart;    internal static Type? ShipBridgeType => shipBridge;
    internal static Type? ShipInteriorType => shipInterior;

    /// <summary>True when both cells sit on the same deck; always true on an unmanaged map.</summary>
    internal static bool SameLayer(Map? map, IntVec3 a, IntVec3 b)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return true;
        return Sos2Geometry.SameLayer(a.z, b.z, owner.Layout.Stride, owner.Layout.LayerCount);
    }

    /// <summary>
    /// Replaces a <c>Map.Size</c> that SOS2 passes to the map generator. Every caller hands over the
    /// storage rectangle, which on a layered map is every band plus the padding.
    /// </summary>
    internal static IntVec3 LogicalSize(IntVec3 storageSize)
    {
        LayerMapComponent? owner = LayerMapComponent.For(SourceMapOf(storageSize));
        if (owner?.Layout is not { } found) return storageSize;
        (int x, int y, int z) = Sos2Geometry.LogicalSize(
            storageSize.x, storageSize.z, found.Height, found.Stride, found.LayerCount);
        return new IntVec3(x, y, z);
    }

    /// <summary>
    /// The managed map whose storage rectangle is exactly <paramref name="storageSize"/>. A width-only
    /// match is the last resort: two maps of the same width but different band counts exist as soon as
    /// a ship leaves for its own space map.
    /// </summary>
    private static Map? SourceMapOf(IntVec3 storageSize)
    {
        Map? exact = Find.Maps.FirstOrDefault(m =>
            LayerMapComponent.For(m)?.Layout is { } layout &&
            layout.Width == storageSize.x && layout.StorageHeight == storageSize.z);
        return exact ?? Find.Maps.FirstOrDefault(m =>
            LayerMapComponent.For(m)?.Layout is { } layout && layout.Width == storageSize.x);
    }

    /// <summary>
    /// Set by the SOS2 map-size adapter just before it asks for a space map, consumed by
    /// <c>LayerGeneration</c>. A space map has to store the same bands as the ship it receives;
    /// generated as a flat map it is a logical rectangle, and every cell of a second deck then lands
    /// outside the map during the move.
    /// </summary>
    [ThreadStatic] private static LayerLayout? pendingSpaceLayout;

    /// <summary>Announces the band structure the next generated map must have.</summary>
    internal static void ExpectSpaceLayout(IntVec3 storageSize)
    {
        pendingSpaceLayout = LayerMapComponent.For(SourceMapOf(storageSize))?.Layout;
    }

    /// <summary>Reads and clears the pending band structure; a stale request must never leak.</summary>
    internal static LayerLayout? TakeSpaceLayout()
    {
        LayerLayout? layout = pendingSpaceLayout;
        pendingSpaceLayout = null;
        return layout;
    }

    /// <summary>Which edge an SOS2 shot comes from, in <see cref="Sos2Geometry.EdgeCell"/>'s indexing.</summary>
    internal static int ProjectileSide(Map map, IntVec3 targetCell)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { } layout) return 0;
        int heading = 0, engineRot = 0;
        MapComponent? comp = ShipCompOf(map);
        if (comp != null)
        {
            heading = AccessTools.Field(shipMapComp, "Heading")?.GetValue(comp) as int? ?? 0;
            engineRot = AccessTools.Field(shipMapComp, "EngineRot")?.GetValue(comp) as int? ?? 0;
        }
        return Sos2Geometry.ProjectileSide(
            targetCell.x, targetCell.z, layout.Width, layout.Height, layout.Stride, layout.LayerCount,
            heading, engineRot);
    }
}
