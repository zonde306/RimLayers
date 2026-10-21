using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Navigation;

/// <summary>Walking world boundaries are the ground rectangle, never storage bands.
/// No Pawn/Map relocation and no replacement for native exit/caravan/quest handling.</summary>
internal static class WorldBoundary
{
    internal static IntVec3 Size(Map map) => LayerMapComponent.For(map)?.Layout is { } layout
        ? new IntVec3(layout.Width, 1, layout.Height) : map.Size;
    internal static CellRect Rect(Map map) => LayerMapComponent.For(map)?.LayerRect(0) ?? CellRect.WholeMap(map);
    internal static bool OnEdge(IntVec3 cell, Map map)
    {
        if (LayerMapComponent.For(map) == null) return cell.OnEdge(map);
        var rect = Rect(map);
        return rect.Contains(cell) && (cell.x == rect.minX || cell.x == rect.maxX || cell.z == rect.minZ || cell.z == rect.maxZ);
    }
    internal static bool OnDirectionalEdge(IntVec3 cell, Map map, Rot4 dir)
    {
        if (LayerMapComponent.For(map) == null) return cell.OnEdge(map, dir);
        var rect = Rect(map);
        return rect.Contains(cell) && (dir == Rot4.North && cell.z == rect.maxZ || dir == Rot4.South && cell.z == rect.minZ ||
            dir == Rot4.East && cell.x == rect.maxX || dir == Rot4.West && cell.x == rect.minX);
    }
    internal static IntVec3 GroundProjection(IntVec3 cell, Map map) =>
        LayerMapComponent.For(map) is { } owner && owner.TryDecode(cell, out var logical)
            ? new IntVec3(logical.X, 0, logical.Z) : cell;
    internal static IntVec3 RandomCell(Map map)
    {
        if (LayerMapComponent.For(map) == null) return CellFinder.RandomCell(map);
        var size = Size(map);
        return new IntVec3(Rand.Range(0, size.x), 0, Rand.Range(0, size.z));
    }
    internal static bool RandomNear(IntVec3 root, Map map, int squareRadius,
        Predicate<IntVec3>? validator, out IntVec3 result, int maxTries)
    {
        if (LayerMapComponent.For(map) == null)
            return CellFinder.TryFindRandomCellNear(root, map, squareRadius, validator, out result, maxTries);
        var ground = Rect(map);
        return CellFinder.TryFindRandomCellNear(GroundProjection(root, map), map, squareRadius,
            cell => ground.Contains(cell) && (validator == null || validator(cell)), out result, maxTries);
    }
    internal static IEnumerable<CodeInstruction> Geometry(IEnumerable<CodeInstruction> instructions)
    {
        var size = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Size));
        var rect = AccessTools.Method(typeof(CellRect), nameof(CellRect.WholeMap));
        var random = AccessTools.Method(typeof(CellFinder), nameof(CellFinder.RandomCell), new[] { typeof(Map) });
        var near = AccessTools.Method(typeof(CellFinder), nameof(CellFinder.TryFindRandomCellNear));
        var edge = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.OnEdge), new[] { typeof(IntVec3), typeof(Map) });
        var directional = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.OnEdge), new[] { typeof(IntVec3), typeof(Map), typeof(Rot4) });
        foreach (var code in instructions)
        {
            if (code.Calls(size)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(WorldBoundary), nameof(Size)); }
            else if (code.Calls(rect)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(WorldBoundary), nameof(Rect)); }
            else if (code.Calls(random)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(WorldBoundary), nameof(RandomCell)); }
            else if (code.Calls(near)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(WorldBoundary), nameof(RandomNear)); }
            else if (code.Calls(edge)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(WorldBoundary), nameof(OnEdge)); }
            else if (code.Calls(directional)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(WorldBoundary), nameof(OnDirectionalEdge)); }
            yield return code;
        }
    }
}

[HarmonyPatch]
internal static class WorldEdgeGeometryPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in typeof(CellFinder).GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (method.Name is nameof(CellFinder.RandomEdgeCell) or nameof(CellFinder.TryFindRandomEdgeCellWith)) yield return method;
        yield return AccessTools.Method(typeof(RCellFinder), nameof(RCellFinder.TryFindBestExitSpot));
        yield return AccessTools.Method(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomExitSpot));
        yield return AccessTools.Method(typeof(JobDriver_Goto), "TryExitMap");
        yield return AccessTools.Method(typeof(JobDriver_Flee), "ExitMap");
        // Patch the actual native delegates, not Toil construction: loaded Drivers
        // create these before layout restoration, but execute them after restoration.
        foreach (var type in new[] { typeof(JobDriver_Goto), typeof(JobDriver_Flee), typeof(JobDriver_TakeAndExitMap), typeof(CellFinder) })
            foreach (var method in DelegateMethods(type)) yield return method;
    }
    private static IEnumerable<MethodInfo> DelegateMethods(Type type)
    {
        foreach (var method in AccessTools.GetDeclaredMethods(type))
            if (method.Name.StartsWith("<MakeNewToils>", StringComparison.Ordinal) ||
                method.Name.StartsWith("<TryFindRandomEdgeCellWith>", StringComparison.Ordinal)) yield return method;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var method in DelegateMethods(nested)) yield return method;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => WorldBoundary.Geometry(instructions);
}

[HarmonyPatch(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellNearWith))]
internal static class WorldEdgeNearPatch
{
    static bool Prefix(IntVec3 near, float radius, Map map, Predicate<IntVec3> validator, ref IntVec3 spot, ref bool __result)
    {
        if (LayerMapComponent.For(map) == null) return true;
        near = WorldBoundary.GroundProjection(near, map);
        var ground = WorldBoundary.Rect(map);
        var rect = CellRect.CenteredOn(near, UnityEngine.Mathf.CeilToInt(radius)).ClipInsideRect(ground);
        if (rect.ExpandedBy(1).Area == rect.ExpandedBy(1).ClipInsideRect(ground).Area)
        { spot = IntVec3.Invalid; __result = false; return false; }
        var center = near;
        Predicate<IntVec3> valid = cell => cell.InHorDistOf(center, radius) && WorldBoundary.OnEdge(cell, map) && validator(cell);
        __result = ground.EdgeCellsCount < rect.Area
            ? CellFinder.TryFindRandomEdgeCellWith(valid, map, CellFinder.EdgeRoadChance_Ignore, out spot)
            : CellFinder.TryFindRandomCellInsideWith(rect, valid, out spot);
        return false;
    }
}

[HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReachMapEdge))]
internal static class WorldEdgeReachPatch
{
    static bool Prefix(Map ___map, IntVec3 c, TraverseParms traverseParms, ref bool __result)
    {
        var owner = LayerMapComponent.For(___map);
        // Generation temporarily uses native ground-sized regions. Preserve native validation/logging.
        if (owner == null || MapGenerator.mapBeingGenerated == ___map || LayerRouter.Busy ||
            traverseParms.pawn is { } p && (!p.Spawned || p.Map != ___map)) return true;
        __result = false;
        if (!owner.TryDecode(c, out _) || c.GetRegion(___map) is not { } start) return false;
        var ends = owner.LayerRect(0).EdgeCells.Select(cell => cell.GetRegion(___map)).Where(r => r != null).ToHashSet();
        var stairs = new Dictionary<Region, List<Region>>();
        foreach (var edge in owner.Connectors.MovementEdges(traverseParms))
        {
            var from = edge.From.GetRegion(___map); var to = edge.To.GetRegion(___map);
            if (from == null || to == null) continue;
            if (!stairs.TryGetValue(from, out var list)) stairs[from] = list = new();
            list.Add(to);
        }
        var visited = new HashSet<Region> { start };
        var queue = new Queue<Region>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var region))
        {
            if (ends.Contains(region)) { __result = true; break; }
            IEnumerable<Region> next = region.Neighbors;
            if (stairs.TryGetValue(region, out var links)) next = next.Concat(links);
            foreach (var neighbor in next)
                if (!visited.Contains(neighbor) && neighbor.Allows(traverseParms, isDestination: false))
                { visited.Add(neighbor); queue.Enqueue(neighbor); }
        }
        return false;
    }
}

