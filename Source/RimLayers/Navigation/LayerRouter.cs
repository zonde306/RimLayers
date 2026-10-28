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
    // Counters for the automated performance regression: a same-floor query that proves the direct
    // leg optimal must price zero connector legs, and a cross-floor query must stay bounded.
    public static long Requests { get; private set; }
    public static long LegPathfinds { get; private set; }
    public static long DirectShortCircuits { get; private set; }
    public static void ResetStatistics() { Requests = 0; LegPathfinds = 0; DirectShortCircuits = 0; }

    public static bool HasMetadata(PawnPath path, out RouteMetadata tag) => metadata.TryGetValue(path, out tag!);

    public static PawnPath Find(LayerMapComponent owner, IntVec3 start, LocalTargetInfo target,
        TraverseParms parms, PathFinderCostTuning? tuning, PathEndMode endMode, PathRequest.IPathGridCustomizer? customizer)
    {
        if (Busy || !owner.TryDecode(start, out var startLogical) || !owner.TryDecode(target.Cell, out _))
            return PawnPath.NotFound;
        if (target.HasThing && target.Thing.MapHeld != owner.map) return PawnPath.NotFound;
        Busy = true;
        Requests++;
        var legs = new Dictionary<(int From, int To), PawnPath?>();
        PawnPath? direct = null;
        try
        {
            var connectors = owner.Connectors.MovementEdges(parms);
            // Vanilla treats the allowed area as a COST, not a wall: PathGridJob.CostForCell adds
            // Cost_OutsideAllowedArea (600) to every cell outside PathRequest.area instead of
            // refusing it. A connector crossing is one step in flat storage space that jumps a whole
            // layer band, so the native grid never prices it and a restricted pawn would leave its
            // area for free. Price the landing cell the way vanilla prices any cell.
            // Reachability deliberately stays unfiltered: vanilla Reachability.CanReach ignores
            // areas as well, and only the path job prices them.
            var allowed = PathUtility.GetAllowedArea(parms.pawn);
            int LandingPenalty(IntVec3 cell) =>
                allowed is { TrueCount: > 0 } && !allowed[cell] ? PathGridJob.Cost_OutsideAllowedArea : 0;
            // Goal remains a distinct virtual node, preserving the caller's target and Touch/InteractionCell mode.
            // Index by cell so node discovery is O(1); the previous Skip/Contains scan was O(E*N).
            var nodes = new List<IntVec3> { target.Cell, start };
            var nodeIndex = new Dictionary<IntVec3, int> { [target.Cell] = 0, [start] = 1 };
            void AddNode(IntVec3 cell)
            {
                if (nodeIndex.ContainsKey(cell)) return;
                nodeIndex.Add(cell, nodes.Count);
                nodes.Add(cell);
            }
            foreach (var edge in connectors)
            {
                AddNode(edge.From);
                AddNode(edge.To);
            }
            float CrossingCost(ConnectorEdge edge)
            {
                int penalty = LandingPenalty(edge.To);
                if (customizer == null) return edge.Ticks + penalty;
                var offsets = customizer.GetOffsetGrid();
                if (offsets.Length == 0) return edge.Ticks + penalty;
                if (offsets.Length != owner.map.cellIndices.NumGridCells)
                    throw new ArgumentException("Path customizer must cover the complete storage Map.");
                return edge.Ticks + penalty + offsets[owner.map.cellIndices.CellToIndex(edge.To)];
            }
            var crossing = new Dictionary<(int, int), ConnectorEdge>();
            foreach (var edge in connectors)
                {
                    var pair = (nodeIndex[edge.From], nodeIndex[edge.To]);
                    if(!crossing.TryGetValue(pair,out var previous)||edge.Ticks<previous.Ticks)crossing[pair]=edge;
                }

            // A direct same-floor leg is a complete route when it exists. Leaving the start floor and
            // coming back to the target floor always costs at least two connector crossings, so when
            // the direct leg is no more expensive than that floor we can prove it is optimal without
            // pricing a single connector leg. This is the dominant same-floor query.
            // The bound needs raw edge ticks, so it is only used without a customizer (whose offset
            // grid could be negative and would break the lower bound).
            owner.TryDecode(start, out var startLayer);
            owner.TryDecode(target.Cell, out var targetLayer);
            bool sameFloor = startLayer.LayerId == targetLayer.LayerId;
            if (sameFloor)
            {
                direct = Leg(owner, start, target, parms, tuning, endMode, customizer);
            }
            float bound = direct?.TotalCost ?? float.PositiveInfinity;
            bool directProven = false;
            if (direct != null && customizer == null && connectors.Count != 0)
            {
                float cheapest = float.PositiveInfinity;
                foreach (var edge in connectors)
                {
                    // The lower bound must include the same landing penalty the route pays, or a
                    // penalised crossing could look cheaper than it is and mis-prove optimality.
                    float price = edge.Ticks + LandingPenalty(edge.To);
                    if (price < cheapest) cheapest = price;
                }
                directProven = RouteBounds.DirectIsOptimal(bound, cheapest);
            }

            IEnumerable<RouteArc> Edges(int from)
            {
                if (from == 0) yield break;
                owner.TryDecode(nodes[from], out var here);
                for (int to = 0; to < nodes.Count; to++)
                {
                    if (to == from || to == 1) continue;
                    if (crossing.TryGetValue((from, to), out var viaConnector))
                    {
                        yield return new RouteArc(to, CrossingCost(viaConnector));
                        continue;
                    }
                    owner.TryDecode(nodes[to], out var there);
                    if (here.LayerId != there.LayerId) continue;
                    // Reuse the direct leg instead of pathfinding the same start->target again.
                    // `direct` is owned by the local field, never by `legs`, so it is disposed once.
                    if (from == 1 && to == 0 && direct != null)
                    {
                        yield return new RouteArc(0, direct.TotalCost);
                        continue;
                    }
                    if (!legs.TryGetValue((from, to), out var path))
                    {
                        path = Leg(owner, nodes[from], to == 0 ? target : nodes[to], parms, tuning,
                            to == 0 ? endMode : PathEndMode.OnCell, customizer);
                        legs[(from, to)] = path;
                    }
                    if (path != null) yield return new RouteArc(to, path.TotalCost);
                }
            }
            // directProven means no connector route can beat the direct leg, so the search is skipped
            // and the route is expressed as the single (start -> goal) leg. A bounded search that
            // finds nothing cheaper means exactly the same thing, so both fall back to the direct leg.
            // Either way the result goes through the assembly below: `direct` is a pooled native
            // PawnPath, so it can never be returned to the caller nor registered as a result.
            List<int>? route = directProven ? new List<int> { 1, 0 } :
                float.IsPositiveInfinity(bound) ? EndpointSearch.Find(1, 0, Edges) :
                bound <= 0f ? null : EndpointSearch.Find(1, 0, Edges, bound);
            if (directProven) DirectShortCircuits++;
            // Nothing cheaper than the direct leg: use it when we have one, otherwise there is no route.
            if (route == null)
            {
                if (direct == null) return PawnPath.NotFound;
                route = new List<int> { 1, 0 };
            }
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
                    // The direct leg is owned by the local field rather than by `legs`, so a route
                    // that uses (1,0) must resolve it explicitly; `legs` has no such entry.
                    var leg = key == (1, 0) && direct != null ? direct : legs[key]!;
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
            metadata.AddOrUpdate(joined, new RouteMetadata(owner.Connectors.Version, steps, parms));
            return joined;
        }
        finally
        {
            foreach (var path in legs.Values) path?.Dispose();
            direct?.Dispose();
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
        LegPathfinds++;
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
