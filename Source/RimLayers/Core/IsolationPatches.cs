using HarmonyLib;
using RimLayers.Diagnostics;
using RimWorld;
using Verse;

namespace RimLayers.Core;

[HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt))]
internal static class PlacementBoundaryPatch
{
    static bool Prefix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, ref AcceptanceReport __result)
    {
        var component = LayerMapComponent.For(map);
        if (component == null) return true;
        // Behind a UI panel, or over a floor the player is not looking at, UI.MouseCell() answers
        // IntVec3.Invalid. That is "no cell", not "a cell with nothing under it": validating it would
        // report RL_NoSurface for a click that never aimed at the map at all.
        if (!center.IsValid) return true;
        // SOS2's ship-move ghost designates empty space on purpose: the thing it places is a sketch of
        // where the hull will arrive, and its own PlaceWorker_MoveShip decides what is legal there.
        // Requiring a floor under it rejects every cell of a destination that is by definition empty.
        if (entDef.defName == "ShipMoveBlueprint") return true;
        var report = component.Structure.Validate(entDef, center, rot, planning: true);
        if (report.Accepted) return true;
        // The placement ghost revalidates every frame; only a real click is worth a window.
        if (RejectionReporter.PlayerClickInProgress)
            RejectionReporter.Reject(RejectionAction.Build, report.Reason, true, entDef.defName, entDef.LabelCap,
                map, center, RejectionReporter.ElevationAt(map, center));
        __result = report;
        return false;
    }
}

[HarmonyPatch(typeof(GenGrid), nameof(GenGrid.CanBeSeenOverFast))]
internal static class IsolationSightPatch
{
    static bool Prefix(IntVec3 c, Map map, ref bool __result)
    {
        var component = LayerMapComponent.For(map);
        if (component == null || component.TryDecode(c, out _)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(GasGrid), nameof(GasGrid.GasCanMoveTo))]
internal static class IsolationGasPatch
{
    static bool Prefix(IntVec3 cell, Map ___map, ref bool __result)
    {
        var component = LayerMapComponent.For(___map);
        if (component == null || component.TryDecode(cell, out _)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ExitMapGrid), "Rebuild")]
internal static class GroundExitPatch
{
    static bool Prefix(Map ___map, ref BoolGrid ___exitMapGrid, ref bool ___dirty, CellBoolDrawer ___drawerInt)
    {
        var component = LayerMapComponent.For(___map);
        if (component?.Layout == null) return true;
        ___dirty = false;
        ___exitMapGrid ??= new BoolGrid(___map);
        ___exitMapGrid.Clear();
        CellRect ground = component.LayerRect(0);
        foreach (IntVec3 cell in ground)
        {
            if (cell.x > 1 && cell.x < ground.maxX - 1 && cell.z > 1 && cell.z < ground.maxZ - 1) continue;
            if (!cell.CanBeSeenOver(___map)) continue;
            int count = GenRadial.NumCellsInRadius(2f);
            for (int i = 0; i < count; i++)
            {
                IntVec3 edge = cell + GenRadial.RadialPattern[i];
                if (!ground.Contains(edge)) continue;
                bool onEdge = edge.x == 0 || edge.z == 0 || edge.x == ground.maxX || edge.z == ground.maxZ;
                if (onEdge && edge.CanBeSeenOverFast(___map) && GenSight.LineOfSight(cell, edge, ___map))
                {
                    ___exitMapGrid[cell] = true;
                    break;
                }
            }
        }
        ___drawerInt?.SetDirty();
        return false;
    }
}

[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
internal static class TerrainChangePatch
{
    static void Postfix(IntVec3 c, TerrainDef newTerr, Map ___map)
    {
        var owner = LayerMapComponent.For(___map);
        // A foundation def makes SetTerrain hand off to SetFoundation and return, so this write is
        // already reported by ShipFoundationChange. Reporting it twice would run every structural
        // update - path cost, roof invalidation, collapse booking - two times per cell.
        if (newTerr.isFoundation) return;
        owner?.Structure.TerrainChanged(c, newTerr);
        owner?.Invalidate(c, LayerChangeKind.Structure | LayerChangeKind.Movement | LayerChangeKind.Air | LayerChangeKind.Heat | LayerChangeKind.Light);
    }
}

[HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
internal static class RoofChangePatch
{
    static void Postfix(IntVec3 c, Map ___map)
    {
        var owner = LayerMapComponent.For(___map);
        owner?.Structure.RoofChanged(c);
        owner?.Invalidate(c, LayerChangeKind.Structure | LayerChangeKind.Heat | LayerChangeKind.Light);
    }
}

[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.LightBlockerRemoved))]
internal static class IsolationLightPatch
{
    static bool Prefix(IntVec3 cell, Map ___map)
    {
        var component = LayerMapComponent.For(___map);
        return component == null || component.TryDecode(cell, out _);
    }
}
