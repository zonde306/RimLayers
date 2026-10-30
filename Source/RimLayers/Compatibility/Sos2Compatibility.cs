using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
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
    private static FieldInfo? shipMapGrid;
    private static FieldInfo? heatGridDirty;
    private static FieldInfo? breathableZoneDirty;

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
        // SOS2 roofs its hull with RoofShip (Defs/Misc/RoofDefs/Roofs.xml), which is not one of the
        // two native support roofs.
        StructuralSupport.RegisterSupportRoof(roof => roof != null && roof.defName == "RoofShip");
        LiveMapExpansion.Committed += OnExpanded;

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
        Log.Message("[RimLayers] Save Our Ship 2 adapter installed (multi-deck hulls, banded geometry, heat nets across decks).");
    }

    private static void Patch(Harmony harmony, Type type)
    {
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

    private static Type Require(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: false) ??
        throw new InvalidOperationException($"[RimLayers] SOS2 type not found: {name}");

    /// <summary>True when the cell holds an SOS2 ship part and belongs to some SOS2 ship.</summary>
    internal static bool IsShipDeck(LayerMapComponent owner, IntVec3 cell)
    {
        if (!Active || shipCachePart == null || !owner.TryDecode(cell, out _)) return false;
        if (!MapClaims(owner.map, cell)) return false;
        foreach (Thing thing in cell.GetThingList(owner.map))
            if (thing.def.HasComp(shipCachePart)) return true;
        return false;
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

    internal static MapComponent? ShipCompOf(Map? map) =>
        shipMapComp == null || map == null ? null : map.GetComponent(shipMapComp);

    /// <summary>The live <c>MapShipCells</c> dictionary, or null when SOS2 is absent.</summary>
    internal static IDictionary? ShipCellsOf(Map? map) => ShipCellsOf(ShipCompOf(map));

    internal static IDictionary? ShipCellsOf(MapComponent? comp) =>
        comp == null || mapShipCellsGetter == null ? null : mapShipCellsGetter.Invoke(comp, null) as IDictionary;

    internal static bool HasShipCells(MapComponent? comp) => comp != null && ShipCellsOf(comp) is { Count: > 0 };

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
    internal static Type? ShipCachePartType => shipCachePart;
    internal static Type? ShipBridgeType => shipBridge;
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
        Map? map = Find.Maps.FirstOrDefault(m =>
        {
            LayerMapComponent? owner = LayerMapComponent.For(m);
            return owner?.Layout is { } layout && layout.Stride > 0 && layout.Width == storageSize.x;
        });
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { } found) return storageSize;
        (int x, int y, int z) = Sos2Geometry.LogicalSize(
            storageSize.x, storageSize.z, found.Height, found.Stride, found.LayerCount);
        return new IntVec3(x, y, z);
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
