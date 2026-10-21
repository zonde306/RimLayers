using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Core;
using Verse;
using Verse.AI;

namespace RimLayers.Navigation;

public sealed record RouteMetadata(long Version, Dictionary<(IntVec3 From, IntVec3 To), ConnectorEdge> Steps, TraverseParms TraverseParms);
public static class LayerRouter
{
    [ThreadStatic] public static bool Busy;
    [ThreadStatic] private static (Map Map, IntVec3 Start, LocalTargetInfo Target)? activeLeg;
    public static bool IsLegRequest(PathRequest request, Map map) => activeLeg is { } leg && leg.Map == map &&
        leg.Start == request.Start && leg.Target == request.Target;
    private static readonly ConditionalWeakTable<PawnPath, RouteMetadata> metadata = new();
    private static readonly AccessTools.FieldRef<PawnPath, int> Index = AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");
    private static readonly AccessTools.FieldRef<PawnPath, float> Cost = AccessTools.FieldRefAccess<PawnPath, float>("totalCostInt");
    public const int StairTicks = 90;

    public static bool HasMetadata(PawnPath path, out RouteMetadata tag) => metadata.TryGetValue(path, out tag!);

    public static PawnPath Find(LayerMapComponent owner, IntVec3 start, LocalTargetInfo target,
        TraverseParms parms, PathFinderCostTuning? tuning, PathEndMode endMode, PathRequest.IPathGridCustomizer? customizer)
    {
        if (Busy || !owner.TryDecode(start, out var startLogical) || !owner.TryDecode(target.Cell, out _))
            return PawnPath.NotFound;
        if (target.HasThing && target.Thing.MapHeld != owner.map) return PawnPath.NotFound;
        Busy = true;
        var legs = new Dictionary<(int From, int To), PawnPath?>();
        try
        {
            var connectors = owner.Connectors.MovementEdges(parms);
            // Goal remains a distinct virtual node, preserving the caller's target and Touch/InteractionCell mode.
            var nodes = new List<IntVec3> { target.Cell, start };
            foreach (var edge in connectors)
            {
                if (!nodes.Skip(2).Contains(edge.From)) nodes.Add(edge.From);
                if (!nodes.Skip(2).Contains(edge.To)) nodes.Add(edge.To);
            }
            float CrossingCost(ConnectorEdge edge)
            {
                if (customizer == null) return edge.Ticks;
                var offsets = customizer.GetOffsetGrid();
                if (offsets.Length == 0) return edge.Ticks;
                if (offsets.Length != owner.map.cellIndices.NumGridCells)
                    throw new ArgumentException("Path customizer must cover the complete storage Map.");
                return edge.Ticks + offsets[owner.map.cellIndices.CellToIndex(edge.To)];
            }
            var crossing = new Dictionary<(int, int), ConnectorEdge>();
            foreach (var edge in connectors)
                {
                    var pair=(nodes.FindIndex(2,c=>c==edge.From),nodes.FindIndex(2,c=>c==edge.To));
                    if(!crossing.TryGetValue(pair,out var previous)||edge.Ticks<previous.Ticks)crossing[pair]=edge;
                }

            IEnumerable<RouteArc> Edges(int from)
            {
                if (from == 0) yield break;
                owner.TryDecode(nodes[from], out var here);
                for (int to = 0; to < nodes.Count; to++)
                {
                    if (to == from || to == 1) continue;
                    if (crossing.ContainsKey((from, to)))
                    {
                        yield return new RouteArc(to, CrossingCost(crossing[(from,to)]));
                        continue;
                    }
                    owner.TryDecode(nodes[to], out var there);
                    if (here.LayerId != there.LayerId) continue;
                    var path = Leg(owner, nodes[from], to == 0 ? target : nodes[to], parms, tuning,
                        to == 0 ? endMode : PathEndMode.OnCell, customizer);
                    legs[(from, to)] = path;
                    if (path != null) yield return new RouteArc(to, path.TotalCost);
                }
            }
            var route = EndpointSearch.Find(1, 0, Edges);
            if (route == null) return PawnPath.NotFound;
            var walking = new List<IntVec3>();
            var steps = new Dictionary<(IntVec3, IntVec3), ConnectorEdge>();
            float cost = 0;
            for (int i = 1; i < route.Count; i++)
            {
                var key = (route[i - 1], route[i]);
                if (crossing.TryGetValue(key, out var stair))
                {
                    if (walking.Count == 0 || walking[^1] != stair.From) walking.Add(stair.From);
                    walking.Add(stair.To);
                    steps[(stair.From, stair.To)] = stair;
                    cost += CrossingCost(stair);
                }
                else
                {
                    var leg = legs[key]!;
                    for (int n = leg.NodesReversed.Count - 1; n >= 0; n--)
                    {
                        IntVec3 cell = leg.NodesReversed[n];
                        if (walking.Count == 0 || walking[^1] != cell) walking.Add(cell);
                    }
                    cost += leg.TotalCost;
                }
            }
            var joined = new PawnPath();
            for (int i = walking.Count - 1; i >= 0; i--) joined.AddNode(walking[i]);
            Index(joined) = walking.Count - 1;
            Cost(joined) = cost;
            metadata.Add(joined, new RouteMetadata(owner.Connectors.Version, steps, parms));
            return joined;
        }
        finally
        {
            foreach (var path in legs.Values) path?.Dispose();
            Busy = false;
        }
    }

    private static PawnPath? Leg(LayerMapComponent owner, IntVec3 from, LocalTargetInfo to,
        TraverseParms parms, PathFinderCostTuning? tuning, PathEndMode endMode, PathRequest.IPathGridCustomizer? customizer)
    {
        if (endMode == PathEndMode.OnCell && from == to.Cell)
        {
            var one = new PawnPath();
            one.AddNode(from); Index(one) = 0; Cost(one) = 0;
            return one;
        }
        PawnPath result;
        var previous = activeLeg;
        activeLeg = (owner.map, from, to);
        try { result = owner.map.pathFinder.FindPathNow(from, to, parms, tuning, endMode, customizer); }
        finally { activeLeg = previous; }
        if (result == null || !result.Found)
        {
            result?.Dispose(); return null;
        }
        owner.TryDecode(from, out var source);
        if (result.NodesReversed.Any(cell => !owner.TryDecode(cell, out var dest) || source.LayerId != dest.LayerId || !owner.Structure.HasSurface(cell)))
        {
            result.Dispose();
            return null;
        }
        return result;
    }
}
