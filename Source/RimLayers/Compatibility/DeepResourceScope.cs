using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Deep-crust resources belong to one logical layer. <see cref="DeepResourceGrid"/> is a flat
/// ushort pair keyed by storage cell, so every native helper that only knows <see cref="Map.Size"/>
/// (deep drill scan radius, scanner lump search, scanner overlay mesh) can otherwise reach across
/// bands. This class keeps placement, scanning and the overlay inside one band.
/// </summary>
public static class DeepResourceScope
{
    public const string UpperLayerReasonKey = "RL_DeepResourceGroundOnly";

    private static readonly AccessTools.FieldRef<DeepResourceGrid, Map> GridMapRef =
        AccessTools.FieldRefAccess<DeepResourceGrid, Map>("map");

    /// <summary>Deep drilling needs a real substrate, so only the ground and dug strata qualify.</summary>
    public static bool LayerAllowed(LayerMapComponent? owner, int layerId) =>
        owner?.Layout != null && owner.Elevation(layerId) <= 0;

    /// <summary>Placement-level reason, or null when the def is not a deep-resource building.</summary>
    public static AcceptanceReport? PlacementReport(BuildableDef? def, IntVec3 center, Map? map)
    {
        if (def is not ThingDef thing || !IsDeepResourceBuilding(thing) || map == null) return null;
        var owner = LayerMapComponent.For(map);
        if (owner == null || !owner.TryDecode(center, out var local)) return null;
        if (owner.Elevation(local.LayerId) > 0) return UpperLayerReasonKey.Translate();
        if (!map.Biome.hasBedrock) return "CannotUseScannerNoBedrock".Translate();
        return null;
    }

    /// <summary>Native deep-resource components that must keep to the ground and dug strata.</summary>
    public static bool IsDeepResourceBuilding(ThingDef def) =>
        def.CompDefFor<CompDeepDrill>() != null || def.CompDefFor<CompDeepScanner>() != null;

    public static bool IsScanner(Thing? thing) => thing?.def.CompDefFor<CompDeepScanner>() != null;

    public static bool IsDrill(Thing? thing) => thing?.def.CompDefFor<CompDeepDrill>() != null;

    /// <summary>
    /// The single band a scan may write into, contracted the same way the native search does.
    /// Upper floors and reserved isolation bands are excluded by construction.
    /// </summary>
    public static CellRect ScanArea(Map map, IntVec3 center)
    {
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return new CellRect(0, 0, map.Size.x, map.Size.z).ContractedBy(10);
        if (!owner.TryDecode(center, out var here)) return CellRect.Empty;
        return owner.LayerRect(here.LayerId).ContractedBy(10);
    }

    /// <summary>
    /// Native <c>DeepDrillUtility.GetNextResource</c>, with its 21-cell radial sample projected into the
    /// drill's own band. Native addresses storage cells directly, so a narrow isolation band is reachable.
    /// </summary>
    public static bool TryDrillResource(IntVec3 storage, Map map, out ThingDef? def, out int count, out IntVec3 cell)
    {
        def = null; count = 0; cell = storage;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null || !owner.TryDecode(storage, out var here))
            return DeepDrillUtility.GetNextResource(storage, map, out def, out count, out cell);
        var layout = owner.Layout;
        for (int i = 0; i < DeepDrillUtility.NumCellsToScan; i++)
        {
            IntVec3 offset = GenRadial.RadialPattern[i];
            int x = here.X + offset.x, z = here.Z + offset.z;
            if ((uint)x >= (uint)layout.Width || (uint)z >= (uint)layout.Height) continue;
            IntVec3 candidate = owner.ToStorage(new LayerCell(here.LayerId, x, z));
            ThingDef? found = map.deepResourceGrid.ThingDefAt(candidate);
            if (found == null) continue;
            def = found;
            count = map.deepResourceGrid.CountAt(candidate);
            cell = candidate;
            return true;
        }
        def = DeepDrillUtility.GetBaseResource(map, storage);
        count = int.MaxValue;
        cell = storage;
        return false;
    }

    /// <summary>Native 1000-try contract, but never leaves <paramref name="area"/>.</summary>
    public static bool TryFindCenter(CellRect area, Predicate<IntVec3> validator, out IntVec3 result)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            result = new IntVec3(Rand.RangeInclusive(area.minX, area.maxX), 0, Rand.RangeInclusive(area.minZ, area.maxZ));
            if (validator(result)) return true;
        }
        result = IntVec3.Invalid;
        return false;
    }

    /// <summary>
    /// Same lump, count, letter and validator as <c>CompDeepScanner.DoFind</c>; only the candidate
    /// rectangle is the scanner's own band.
    /// </summary>
    public static int FindLump(CompDeepScanner scanner, Pawn worker)
    {
        Map map = scanner.parent.Map;
        CellRect area = ScanArea(map, scanner.parent.Position);
        if (area.IsEmpty) return 0;
        if (!TryFindCenter(area, cell => CanScatterAt(cell, map), out IntVec3 center))
        {
            Log.Error("[RimLayers] Could not find a center cell for deep scanning lump generation in the scanner's own layer.");
            return 0;
        }
        ThingDef def = DefDatabase<ThingDef>.AllDefs.RandomElementByWeight(d => d.deepCommonality);
        int cells = Mathf.CeilToInt(def.deepLumpSizeRange.RandomInRange);
        int written = 0;
        foreach (IntVec3 cell in GridShapeMaker.IrregularLump(center, map, cells))
        {
            if (!CanScatterAt(cell, map) || cell.InNoBuildEdgeArea(map)) continue;
            map.deepResourceGrid.SetAt(cell, def, def.deepCountPerCell);
            written++;
        }
        string key = "LetterDeepScannerFoundLump";
        Find.LetterStack.ReceiveLetter("LetterLabelDeepScannerFoundLump".Translate() + ": " + def.LabelCap,
            key.Translate(def.label, worker.Named("FINDER")), LetterDefOf.PositiveEvent, new LookTargets(center, map));
        return written;
    }

    /// <summary>Native <c>CompDeepScanner.CanScatterAt</c> semantics, without its private state.</summary>
    private static bool CanScatterAt(IntVec3 pos, Map map)
    {
        TerrainDef terrain = map.terrainGrid.BaseTerrainAt(pos);
        if (terrain != null && terrain.IsWater && terrain.passability == Traversability.Impassable) return false;
        if (!pos.GetAffordances(map).Contains(ThingDefOf.DeepDrill.terrainAffordanceNeeded)) return false;
        return !map.deepResourceGrid.GetCellBool(CellIndicesUtility.CellToIndex(pos, map.Size.x));
    }

    /// <summary>
    /// The native overlay mesh spans every storage band, so underground ore would be painted on top
    /// of upper floors. Keep our own drawer whose getter ignores cells outside the viewed band.
    /// Indices stay native storage indices, so the mesh lands on the real band.
    /// </summary>
    private sealed class Overlay
    {
        internal CellBoolDrawer? Drawer;
        internal int Layer = int.MinValue;
    }

    private static readonly ConditionalWeakTable<DeepResourceGrid, Overlay> Overlays = new();

    internal static bool MarkOverlay(DeepResourceGrid grid)
    {
        Map map = GridMapRef(grid);
        if (map == null || map != Find.CurrentMap) return true;
        var state = Overlays.GetOrCreateValue(grid);
        if (!PrepareOverlay(grid, map, state)) return true;
        state.Drawer!.MarkForDraw();
        return false;
    }

    internal static bool UpdateOverlay(DeepResourceGrid grid)
    {
        Map map = GridMapRef(grid);
        if (map == null) return true;
        var state = Overlays.GetOrCreateValue(grid);
        if (!PrepareOverlay(grid, map, state)) return true;
        state.Drawer!.CellBoolDrawerUpdate();
        return false;
    }

    /// <summary>True when the native whole-map drawer must stay in charge.</summary>
    private static bool PrepareOverlay(DeepResourceGrid grid, Map map, Overlay state)
    {
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return false;
        state.Drawer ??= new CellBoolDrawer(
            index => InBand(grid, map, index),
            () => Color.white,
            index => ExtraColor(grid, map, index),
            map.Size.x, map.Size.z, 3640, 1f);
        if (state.Layer != owner.ViewedLayer)
        {
            state.Layer = owner.ViewedLayer;
            state.Drawer.SetDirty();
        }
        return true;
    }

    private static bool InBand(DeepResourceGrid grid, Map map, int index) =>
        BandStorage(map, index, out IntVec3 storage) && grid.GetCellBool(CellIndicesUtility.CellToIndex(storage, map.Size.x));

    private static Color ExtraColor(DeepResourceGrid grid, Map map, int index) =>
        BandStorage(map, index, out IntVec3 storage)
            ? grid.GetCellExtraColor(CellIndicesUtility.CellToIndex(storage, map.Size.x))
            : Color.white;

    /// <summary>Storage index to a real cell only when it belongs to the band currently being viewed.</summary>
    private static bool BandStorage(Map map, int index, out IntVec3 storage)
    {
        storage = CellIndicesUtility.IndexToCell(index, map.Size.x);
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return true;
        return InViewedBand(owner, storage);
    }

    private static bool InViewedBand(LayerMapComponent owner, IntVec3 storage)
    {
        var layout = owner.Layout!;
        int layer = storage.z / layout.Stride, localZ = storage.z % layout.Stride;
        return layer == owner.ViewedLayer && localZ < layout.Height;
    }

    /// <summary>Diagnostics mirror of the overlay getter: is this storage cell drawn on the given band?</summary>
    public static bool OverlayDraws(Map map, int layer, IntVec3 storage)
    {
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return true;
        var layout = owner.Layout;
        return storage.z / layout.Stride == layer && storage.z % layout.Stride < layout.Height;
    }

    /// <summary>True when our band-scoped drawer exists and matches the band currently being viewed.</summary>
    public static bool OverlayActive(Map map, int layer)
    {
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return false;
        if (!Overlays.TryGetValue(map.deepResourceGrid, out var state) || state.Drawer == null) return false;
        return state.Layer == layer && state.Layer == owner.ViewedLayer;
    }
}

/// <summary>The drill's 21-cell sample must not step into a reserved isolation band.</summary>
[HarmonyPatch]
internal static class DeepDrillSamplePatch
{
    static MethodBase TargetMethod() => AccessTools.Method(typeof(DeepDrillUtility), nameof(DeepDrillUtility.GetNextResource),
        new[] { typeof(IntVec3), typeof(Map), typeof(ThingDef).MakeByRefType(), typeof(int).MakeByRefType(), typeof(IntVec3).MakeByRefType() })
        ?? throw new InvalidOperationException("[RimLayers] DeepDrillUtility.GetNextResource signature changed.");

    static bool Prefix(IntVec3 p, Map map, ref ThingDef __2, ref int __3, ref IntVec3 __4, ref bool __result)
    {
        if (LayerMapComponent.For(map) == null) return true;
        __result = DeepResourceScope.TryDrillResource(p, map, out ThingDef? def, out int count, out IntVec3 cell);
        __2 = def!;
        __3 = count;
        __4 = cell;
        return false;
    }
}

/// <summary>Scanner writes only inside its own band.</summary>
[HarmonyPatch]
internal static class DeepScannerLayerPatch
{
    static MethodBase TargetMethod() => AccessTools.Method(typeof(CompDeepScanner), "DoFind", new[] { typeof(Pawn) });

    static bool Prefix(CompDeepScanner __instance, Pawn worker)
    {
        if (LayerMapComponent.For(__instance.parent.Map) == null) return true;
        DeepResourceScope.FindLump(__instance, worker);
        return false;
    }
}

/// <summary>
/// The native roof requirement is a sky-visibility test. Underground strata are always under a native
/// thick roof, which is exactly where a ground-penetrating scanner is meant to work now, so the
/// requirement only survives on the ground layer.
/// </summary>
[HarmonyPatch(typeof(CompScanner), nameof(CompScanner.CanUseNow), MethodType.Getter)]
internal static class ScannerRoofLayerPatch
{
    static void Postfix(CompScanner __instance, ref AcceptanceReport __result)
    {
        if (__result.Accepted) return;
        if (!string.Equals(__result.Reason, "CannotUseScannerRoofed".Translate(), StringComparison.Ordinal)) return;
        var map = __instance.parent.MapHeld;
        var owner = LayerMapComponent.For(map);
        if (owner == null || !owner.TryDecode(__instance.parent.PositionHeld, out var local)) return;
        if (owner.Elevation(local.LayerId) < 0) __result = true; // Dug strata are natively roofed on purpose.
    }
}

[HarmonyPatch(typeof(PlaceWorker_NotUnderRoof), nameof(PlaceWorker_NotUnderRoof.AllowsPlacing))]
internal static class ScannerPlacementLayerPatch
{
    static void Postfix(BuildableDef checkingDef, IntVec3 loc, Map map, ref AcceptanceReport __result)
    {
        if (__result.Accepted) return;
        if (checkingDef is not ThingDef def || def.CompDefFor<CompDeepScanner>() == null) return;
        var owner = LayerMapComponent.For(map);
        if (owner == null || !owner.TryDecode(loc, out var local)) return;
        if (owner.Elevation(local.LayerId) < 0) __result = true;
    }
}

/// <summary>The "cannot be used under a roof" alert must not fire for a legitimately roofed underground scanner.</summary>
[HarmonyPatch]
internal static class RoofedAlertLayerPatch
{
    static MethodBase TargetMethod() => AccessTools.PropertyGetter(typeof(Alert_CannotBeUsedRoofed), "UnusableBuildings");

    // Native recomputes this list on every read, so filtering here cannot go stale.
    static void Postfix(List<Thing> __result)
    {
        if (__result.Count == 0 || !__result.Any(t => LayerMapComponent.For(t.MapHeld) != null)) return;
        __result.RemoveAll(t =>
        {
            var owner = LayerMapComponent.For(t.MapHeld);
            return owner != null && owner.TryDecode(t.PositionHeld, out var local) && owner.Elevation(local.LayerId) < 0;
        });
    }
}

/// <summary>Route the overlay through the layer-scoped drawer instead of native's whole-map mesh.</summary>
[HarmonyPatch(typeof(DeepResourceGrid), nameof(DeepResourceGrid.MarkForDraw))]
internal static class DeepResourceOverlayMarkPatch
{
    static bool Prefix(DeepResourceGrid __instance) => DeepResourceScope.MarkOverlay(__instance);
}

[HarmonyPatch(typeof(DeepResourceGrid), nameof(DeepResourceGrid.DeepResourceGridUpdate))]
internal static class DeepResourceOverlayUpdatePatch
{
    static bool Prefix(DeepResourceGrid __instance)
    {
        bool native = DeepResourceScope.UpdateOverlay(__instance);
        if (native && DebugViewSettings.drawDeepResources) __instance.MarkForDraw();
        return native;
    }
}
