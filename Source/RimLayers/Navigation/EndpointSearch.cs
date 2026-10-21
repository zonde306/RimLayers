namespace RimLayers.Navigation;

public readonly record struct RouteArc(int To, float Cost);

/// <summary>Complete non-negative-cost search. Nodes are connector endpoints, not floors.</summary>
public static class EndpointSearch
{
    public static List<int>? Find(int start, int goal, Func<int, IEnumerable<RouteArc>> edges)
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
