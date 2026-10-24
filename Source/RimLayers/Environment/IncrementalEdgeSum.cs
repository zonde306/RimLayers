namespace RimLayers.Environment;

/// <summary>
/// Maintains a weighted sum over key pairs that is itself a sum over independent columns.
///
/// Vertical heat conductance is column-local: any edge between two volumes comes from a single
/// (x, z) column pairing two adjacent floors. The whole edge set is therefore
/// <c>sum over columns</c>, which lets a change to one column update the totals without rescanning
/// every storage cell of every floor. A single column may contribute several pairs (one per floor
/// boundary it spans), so evaluation returns a list rather than one value.
///
/// Keys are generic so the arithmetic is testable without Verse. When room identity changes
/// (merge/split) the column-to-room mapping changes wholesale, so callers must request a full
/// rebuild.
/// </summary>
public sealed class IncrementalEdgeSum<TKey> where TKey : notnull
{
    private readonly IComparer<TKey> order;
    private readonly Dictionary<(int X, int Z), (TKey A, TKey B, double Weight)[]> columns = new();
    private readonly Dictionary<(TKey, TKey), double> totals = new();
    private readonly List<(TKey, TKey)> totalOrder = new();
    private readonly HashSet<(int X, int Z)> dirty = new();
    private bool fullRebuild = true;

    public IncrementalEdgeSum(IComparer<TKey> order) => this.order = order;

    public int ColumnCount => columns.Count;
    public int PairCount => totals.Count;
    public int DirtyCount => dirty.Count;
    public bool NeedsFullRebuild => fullRebuild;
    public long ColumnEvaluations { get; private set; }
    public long FullRebuilds { get; private set; }

    /// <summary>A structural change inside this column can change its contribution.</summary>
    public void MarkDirty(int x, int z)
    {
        if (fullRebuild) return;
        dirty.Add((x, z));
    }

    /// <summary>Key identity changed (merge/split), so every column must be re-evaluated.</summary>
    public void MarkFullRebuild()
    {
        fullRebuild = true;
        dirty.Clear();
    }

    /// <summary>
    /// Current pairs in first-seen order, which keeps the edge list deterministic and stable
    /// across incremental updates.
    /// </summary>
    public (TKey A, TKey B, double Weight)[] Edges()
    {
        var result = new (TKey, TKey, double)[totalOrder.Count];
        for (int i = 0; i < totalOrder.Count; i++)
        {
            var key = totalOrder[i];
            result[i] = (key.Item1, key.Item2, totals[key]);
        }
        return result;
    }

    private (TKey, TKey) Normalize(TKey a, TKey b) => order.Compare(a, b) <= 0 ? (a, b) : (b, a);

    private void Add(TKey a, TKey b, double weight)
    {
        // Same threshold as Subtract: a weight this small can never survive a later removal,
        // so admitting it would only create a pair that immediately vanishes.
        if (weight <= 1e-12) return;
        var key = Normalize(a, b);
        if (!totals.ContainsKey(key)) totalOrder.Add(key);
        totals[key] = totals.GetValueOrDefault(key) + weight;
    }

    private void Subtract(TKey a, TKey b, double weight)
    {
        var key = Normalize(a, b);
        if (!totals.TryGetValue(key, out double current)) return;
        double remaining = current - weight;
        // Drop exhausted pairs so the edge list cannot grow without bound as floors change.
        if (remaining <= 1e-12)
        {
            totals.Remove(key);
            totalOrder.Remove(key);
            return;
        }
        totals[key] = remaining;
    }

    /// <summary>
    /// Bring the totals up to date. <paramref name="allColumns"/> is only enumerated for a full
    /// rebuild; <paramref name="evaluate"/> returns every pair this column contributes.
    /// </summary>
    public void Update(IEnumerable<(int X, int Z)> allColumns,
        Func<(int X, int Z), (TKey A, TKey B, double Weight)[]> evaluate)
    {
        if (fullRebuild)
        {
            FullRebuilds++;
            fullRebuild = false;
            dirty.Clear();
            columns.Clear();
            totals.Clear();
            totalOrder.Clear();
            foreach (var column in allColumns)
            {
                ColumnEvaluations++;
                var contributions = evaluate(column);
                if (contributions.Length == 0) continue;
                columns[column] = contributions;
                foreach (var (a, b, weight) in contributions) Add(a, b, weight);
            }
            return;
        }
        if (dirty.Count == 0) return;
        foreach (var column in dirty)
        {
            ColumnEvaluations++;
            // Remove the previous contribution before re-evaluating this column.
            if (columns.Remove(column, out var previous))
                foreach (var (a, b, weight) in previous) Subtract(a, b, weight);
            var contributions = evaluate(column);
            if (contributions.Length == 0) continue;
            columns[column] = contributions;
            foreach (var (a, b, weight) in contributions) Add(a, b, weight);
        }
        dirty.Clear();
    }
}
