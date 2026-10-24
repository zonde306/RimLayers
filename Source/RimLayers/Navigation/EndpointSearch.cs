namespace RimLayers.Navigation;

public readonly record struct RouteArc(int To, float Cost);

/// <summary>Complete non-negative-cost search. Nodes are connector endpoints, not floors.</summary>
public static class EndpointSearch
{
    public static List<int>? Find(int start, int goal, Func<int, IEnumerable<RouteArc>> edges) =>
        Find(start, goal, edges, float.PositiveInfinity);

    /// <summary>
    /// Same complete search, but stops once the cheapest open node already costs at least
    /// <paramref name="upperBound"/>. Every remaining path would be no cheaper, so when a caller
    /// already holds a complete route of that cost the pruned search cannot change the optimum.
    /// Passing a non-finite bound is exactly the unpruned search.
    /// </summary>
    public static List<int>? Find(int start, int goal, Func<int, IEnumerable<RouteArc>> edges, float upperBound)
    {
        var distances = new Dictionary<int, float> { [start] = 0 };
        var previous = new Dictionary<int, int>();
        var queue = new PriorityQueue<(int Node, float Cost), float>();
        queue.Enqueue((start, 0), 0);
        while (queue.TryDequeue(out var entry, out _))
        {
            if (entry.Cost != distances[entry.Node]) continue;
            if (entry.Node == goal)
            {
                var route = new List<int> { goal };
                while (route[^1] != start) route.Add(previous[route[^1]]);
                route.Reverse();
                return route;
            }
            // Strictly greater-or-equal: a tie cannot improve on the caller's bound either, and
            // returning null simply lets the caller keep the route it already has.
            if (entry.Cost >= upperBound) return null;
            foreach (var arc in edges(entry.Node))
            {
                if (!float.IsFinite(arc.Cost) || arc.Cost < 0)
                    throw new ArgumentOutOfRangeException(nameof(edges), "Route costs must be finite and nonnegative.");
                float next = entry.Cost + arc.Cost;
                if (!float.IsFinite(next)) throw new OverflowException("Route cost overflow.");
                if (distances.TryGetValue(arc.To, out float old) && next >= old) continue;
                distances[arc.To] = next;
                previous[arc.To] = entry.Node;
                queue.Enqueue((arc.To, next), next);
            }
        }
        return null;
    }
}
