using RimLayers.Environment;

// IncrementalEdgeSum must produce exactly what a from-scratch full scan produces, after any
// sequence of dirty-column updates. This is the invariant the HeatLinks optimisation depends on:
// an incremental rebuild may never change the edge set, only the cost of computing it.

int checks = 0;
void Assert(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

// Deterministic key ordering for the tests (int identity order).
var comparer = Comparer<int>.Default;

// A column contributes a list of pairs; this model mirrors the production EvaluateColumn shape.
List<(int A, int B, double Weight)> Evaluate(Dictionary<(int X, int Z), List<(int, int, double)>> world, (int X, int Z) column)
    => world.TryGetValue(column, out var pairs) ? pairs : new();

// Reference implementation: fold every column from scratch.
Dictionary<(int, int), double> Reference(Dictionary<(int X, int Z), List<(int, int, double)>> world)
{
    var totals = new Dictionary<(int, int), double>();
    foreach (var column in world.Keys.OrderBy(c => c.X).ThenBy(c => c.Z))
        foreach (var (a, b, w) in world[column])
        {
            if (w <= 0) continue;
            var key = a <= b ? (a, b) : (b, a);
            totals[key] = totals.GetValueOrDefault(key) + w;
        }
    return totals;
}

Dictionary<(int, int), double> Actual(IncrementalEdgeSum<int> sum) =>
    sum.Edges().Where(e => e.Weight > 1e-12).ToDictionary(e => (e.A, e.B), e => e.Weight);

void AssertSameAsReference(IncrementalEdgeSum<int> sum, Dictionary<(int X, int Z), List<(int, int, double)>> world, string context)
{
    var expected = Reference(world);
    var actual = Actual(sum);
    Assert(expected.Count == actual.Count, $"{context}: pair count {actual.Count} != {expected.Count}");
    foreach (var (key, weight) in expected)
    {
        Assert(actual.TryGetValue(key, out double got), $"{context}: missing pair {key}");
        Assert(Math.Abs(got - weight) < 1e-9, $"{context}: pair {key} weight {got} != {weight}");
    }
}

// --- 1. Full rebuild matches a reference scan, including several pairs per column. ---
{
    var world = new Dictionary<(int, int), List<(int, int, double)>>
    {
        [(0, 0)] = new() { (10, 11, 0.5), (11, 12, 0.25) },
        [(1, 0)] = new() { (10, 11, 1.5) },
        [(2, 0)] = new() { (12, 13, 2.0) },
        [(3, 3)] = new() { (10, 12, 0.125) },
    };
    var sum = new IncrementalEdgeSum<int>(comparer);
    sum.Update(world.Keys, c => Evaluate(world, c).ToArray());
    AssertSameAsReference(sum, world, "initial full rebuild");
    Assert(sum.FullRebuilds == 1 && sum.ColumnEvaluations == 4, "full rebuild evaluated every column once");
    // Pair weight is the sum over columns: (10,11) = 0.5 + 1.5.
    var edges = Actual(sum);
    Assert(Math.Abs(edges[(10, 11)] - 2.0) < 1e-9, "multi-column pair must accumulate");
    // Ordering is first-seen and deterministic.
    Assert(sum.Edges().Select(e => (e.A, e.B)).SequenceEqual(new[] { (10, 11), (11, 12), (12, 13), (10, 12) }),
        "edge order must be first-seen");
}

// --- 2. Dirty updates match the reference after each mutation. ---
{
    var world = new Dictionary<(int, int), List<(int, int, double)>>
    {
        [(0, 0)] = new() { (0, 1, 1.0) },
        [(1, 0)] = new() { (0, 1, 2.0) },
        [(2, 0)] = new() { (1, 2, 3.0) },
    };
    var allColumns = world.Keys.ToArray();
    var sum = new IncrementalEdgeSum<int>(comparer);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    AssertSameAsReference(sum, world, "dirty-update baseline");

    // Weight change in one column.
    world[(0, 0)] = new() { (0, 1, 5.0) };
    sum.MarkDirty(0, 0);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    AssertSameAsReference(sum, world, "weight change");

    // Column loses all contributions (its pair must be dropped, not left at zero).
    world[(2, 0)] = new();
    sum.MarkDirty(2, 0);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    AssertSameAsReference(sum, world, "column contribution removed");
    Assert(!Actual(sum).ContainsKey((1, 2)), "exhausted pair must be dropped");

    // Column gains a different pair.
    world[(2, 0)] = new() { (0, 2, 7.0) };
    sum.MarkDirty(2, 0);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    AssertSameAsReference(sum, world, "column pair replaced");

    // Removing one of two contributors must leave the other's weight.
    world[(0, 0)] = new();
    sum.MarkDirty(0, 0);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    AssertSameAsReference(sum, world, "one of two contributors removed");
    Assert(Math.Abs(Actual(sum)[(0, 1)] - 2.0) < 1e-9, "surviving contributor weight preserved");
}

// --- 3. Incremental must be much cheaper than a full scan: only dirty columns re-evaluated. ---
{
    var world = new Dictionary<(int, int), List<(int, int, double)>>();
    for (int x = 0; x < 40; x++) world[(x, 0)] = new() { (x % 3, (x % 3) + 1, 1.0) };
    var allColumns = world.Keys.ToArray();
    var sum = new IncrementalEdgeSum<int>(comparer);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    long afterFull = sum.ColumnEvaluations;
    Assert(afterFull == 40, $"full rebuild should evaluate 40 columns, got {afterFull}");

    world[(7, 0)] = new() { (0, 2, 4.0) };
    sum.MarkDirty(7, 0);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    Assert(sum.ColumnEvaluations - afterFull == 1, "one dirty column must evaluate exactly once");
    AssertSameAsReference(sum, world, "single dirty column");

    // A no-op update must not evaluate anything at all.
    long before = sum.ColumnEvaluations;
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    Assert(sum.ColumnEvaluations == before, "clean update must not evaluate columns");
}

// --- 4. MarkFullRebuild forces a complete recomputation. ---
{
    var world = new Dictionary<(int, int), List<(int, int, double)>> { [(0, 0)] = new() { (0, 1, 1.0) } };
    var allColumns = world.Keys.ToArray();
    var sum = new IncrementalEdgeSum<int>(comparer);
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    world[(0, 0)] = new() { (0, 1, 9.0) };
    sum.MarkFullRebuild();
    Assert(sum.NeedsFullRebuild, "full rebuild must be requested");
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    AssertSameAsReference(sum, world, "full rebuild after request");
    Assert(sum.FullRebuilds == 2, "full rebuild counted");
    // A pending full rebuild makes MarkDirty a no-op, so no stale dirty entries can survive it.
    sum.MarkFullRebuild();
    sum.MarkDirty(0, 0);
    Assert(sum.DirtyCount == 0, "MarkDirty is a no-op while a full rebuild is pending");
    sum.Update(allColumns, c => Evaluate(world, c).ToArray());
    Assert(sum.DirtyCount == 0, "full rebuild clears dirty marks");
}

// --- 5. Randomized equivalence: any mutation sequence stays identical to a full scan. ---
{
    var random = new Random(20261024);
    for (int trial = 0; trial < 400; trial++)
    {
        int columns = random.Next(1, 25);
        var keys = Enumerable.Range(0, columns).Select(i => (X: i, Z: i % 4)).ToArray();
        var world = new Dictionary<(int, int), List<(int, int, double)>>();
        foreach (var key in keys) world[key] = RandomPairs(random);
        var allColumns = keys.ToArray();
        var sum = new IncrementalEdgeSum<int>(comparer);
        sum.Update(allColumns, c => Evaluate(world, c).ToArray());
        for (int step = 0; step < 20; step++)
        {
            // Mutate a few random columns, then update only those.
            int mutations = random.Next(1, 5);
            var touched = new HashSet<(int X, int Z)>();
            for (int m = 0; m < mutations; m++)
            {
                var key = keys[random.Next(keys.Length)];
                world[key] = RandomPairs(random);
                touched.Add(key);
            }
            foreach (var key in touched) sum.MarkDirty(key.X, key.Z);
            sum.Update(allColumns, c => Evaluate(world, c).ToArray());
            AssertSameAsReference(sum, world, $"randomized trial {trial} step {step}");
            // The incremental path must only ever evaluate the dirty columns.
            Assert(sum.ColumnEvaluations <= sum.FullRebuilds * columns + (long)(step + 1) * 4,
                $"randomized trial {trial} step {step}: evaluated more columns than were dirtied");
        }
    }
}

// --- 6. Degenerate weights are ignored, and zero/negative never create pairs. ---
{
    var world = new Dictionary<(int, int), List<(int, int, double)>>
    {
        [(0, 0)] = new() { (0, 1, 0.0) },
        [(1, 0)] = new() { (0, 1, -3.0) },
        [(2, 0)] = new() { (0, 1, 1e-18) },
    };
    var sum = new IncrementalEdgeSum<int>(comparer);
    sum.Update(world.Keys, c => Evaluate(world, c).ToArray());
    // 1e-18 is below the drop threshold, so no pair should remain.
    Assert(sum.PairCount == 0, "non-positive and negligible weights must not create pairs");
    Assert(sum.Edges().Length == 0, "edge list empty");
}

static List<(int, int, double)> RandomPairs(Random random)
{
    int count = random.Next(0, 4);
    var result = new List<(int, int, double)>(count);
    for (int i = 0; i < count; i++)
    {
        int a = random.Next(0, 6), b = random.Next(0, 6);
        if (a == b) continue;
        // Weights on a coarse grid keep the reference comparison exact.
        result.Add((a, b, random.Next(1, 9) * 0.25));
    }
    return result;
}

Console.WriteLine($"PASS: {checks} incremental-edge-sum checks (reference equivalence, dirty tracking, randomized).");
