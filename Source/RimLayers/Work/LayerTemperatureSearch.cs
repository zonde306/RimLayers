using HarmonyLib;
using RimLayers.Core;
using RimLayers.Navigation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Work;

/// <summary>Add connector edges only when the native local-region search found no refuge.
/// The protected pawn owns temperature/allowed-area policy; TraverseParms owns movement.
/// No regions, jobs, or persistent state are replaced.</summary>
[HarmonyPatch(typeof(JobGiver_SeekSafeTemperature), nameof(JobGiver_SeekSafeTemperature.ClosestRegionWithinTemperatureRange))]
internal static class SafeTemperatureSearchPatch
{
    static void Postfix(IntVec3 root, Map map, Pawn pawn, FloatRange tempRange,
        TraverseParms traverseParms, RegionType traversableRegionTypes, ref Region? __result)
    {
        if (__result != null || LayerMapComponent.For(map) is not {} owner ||
            !owner.Connectors.HasAny || !owner.TryDecode(root, out _)) return;
        __result = LayerTemperatureSearch.Find(owner, root, pawn, tempRange, traverseParms, traversableRegionTypes);
    }
}

internal static class LayerTemperatureSearch
{
    internal static Region? Find(LayerMapComponent owner, IntVec3 root, Pawn protectedPawn,
        FloatRange temperatures, TraverseParms movement, RegionType types)
    {
        Region? start = root.GetRegion(owner.map, types);
        if (start == null) return null;
        // Directed edges already include forbidden connectors, gate/roamer policy and lift stops.
        var vertical = new Dictionary<Region, List<Region>>();
        foreach (var edge in owner.Connectors.MovementEdges(movement))
        {
            Region? from = edge.From.GetRegion(owner.map, types);
            Region? to = edge.To.GetRegion(owner.map, types);
            if (from == null || to == null) continue;
            if (!vertical.TryGetValue(from, out var destinations))
                vertical.Add(from, destinations = new List<Region>());
            if (!destinations.Contains(to)) destinations.Add(to);
        }
        var seen = new HashSet<Region> { start };
        var pending = new Queue<Region>();
        var candidates = new List<(Region Region, IntVec3 Cell)>();
        pending.Enqueue(start);
        void Visit(Region next)
        {
            if (!next.valid || (next.type & types) == 0 || seen.Contains(next) ||
                !next.Allows(movement, isDestination: false)) return;
            seen.Add(next);
            pending.Enqueue(next);
        }
        int processed = 0;
        while (pending.TryDequeue(out var region) && processed++ < 9999)
        {
            if (!region.IsDoorway && region.Room is {} room && temperatures.Includes(room.Temperature) &&
                JobGiver_SeekSafeTemperature.TryGetAllowedCellInRegion(region, protectedPawn, out var cell))
                candidates.Add((region, cell));
            foreach (Region next in region.Neighbors) Visit(next);
            if (vertical.TryGetValue(region, out var destinations))
                foreach (Region next in destinations) Visit(next);
        }
        Region? best = null;
        float bestCost = float.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            // A graph connection alone is insufficient: validate the actual walk, including
            // surface support and the real carrier's door/fence/danger constraints.
            using var path = owner.map.pathFinder.FindPathNow(root, candidate.Cell, movement, null, PathEndMode.OnCell);
            if (path.Found && path.TotalCost < bestCost)
            {
                best = candidate.Region;
                bestCost = path.TotalCost;
            }
        }
        return best;
    }
}


 
// SafePlaceForBaby immediately converts its preferred Region into a drop cell.
// Native SpotToStandDuringJobInRegion has a 4..15 storage-cell proximity limit;
// that local convenience radius must not reject a refuge on another floor.
[HarmonyPatch(typeof(ChildcareUtility), nameof(ChildcareUtility.SafePlaceForBaby))]
internal static class BabyTemperatureScope
{
    [ThreadStatic] internal static Pawn? Baby;
    static void Prefix(Pawn baby, out Pawn? __state) { __state = Baby; Baby = baby; }
    static Exception? Finalizer(Exception? __exception, Pawn? __state)
    { Baby = __state; return __exception; }
}

[HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.SpotToStandDuringJobInRegion))]
internal static class BabyTemperatureDropCellPatch
{
    static void Prefix(Region region, Pawn pawn, ref float maxDistance, ref Predicate<IntVec3>? extraValidator)
    {
        if (BabyTemperatureScope.Baby is not {} baby || region == null ||
            LayerMapComponent.For(pawn.Map) is not {} owner ||
            !owner.TryDecode(pawn.Position, out var from) ||
            !owner.TryDecode(region.AnyCell, out var to) || from.LayerId == to.LayerId) return;
        maxDistance = float.PositiveInfinity;
        var original = extraValidator;
        extraValidator = cell => (original == null || original(cell)) &&
            cell.InAllowedArea(baby) && owner.Structure.HasSurface(cell) &&
            pawn.CanReach(cell, PathEndMode.OnCell, Danger.Some);
    }
}

