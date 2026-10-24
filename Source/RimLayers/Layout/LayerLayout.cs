namespace RimLayers.Layout;

public readonly record struct LayerCell(int LayerId, int X, int Z);
public readonly record struct StorageCell(int X, int Y, int Z);

/// <summary>Stable storage bands and independently ordered physical elevations.</summary>
public sealed class LayerLayout
{
    public const int CurrentVersion = 2;
    public const int PrototypeLayers = 3;
    public const int MinElevation = -9;
    public const int MaxElevation = 9;
    private readonly int[] elevations;
    // A layout is immutable, so the elevation-derived tables are computed once. These used to be
    // LINQ projections rebuilt (with a sort and allocations) on every access, including per-cell
    // paths such as HasSurface, OpenLaunchColumn and SkyExposure.IsExposed.
    private readonly int[] ordered;
    private readonly int[][] above;
    private readonly int[][] below;
    private readonly IReadOnlyList<int> elevationView;
    private readonly IReadOnlyList<int> orderedView;
    private readonly int lowest;
    private readonly int highest;
    public int Width { get; }
    public int Height { get; }
    public int LayerCount => elevations.Length;
    public int Stride { get; }
    public int StorageHeight => checked(Stride * LayerCount);
    public IReadOnlyList<int> Elevations => elevationView;
    /// <summary>Layer ids in ascending physical elevation.</summary>
    public IReadOnlyList<int> OrderedLayers => orderedView;
    public int LowestElevation => lowest;
    public int HighestElevation => highest;

    // Existing callers create the same surface/upper layout as before.
    public LayerLayout(int width, int height, int layers, int stride)
        : this(width, height, Enumerable.Range(0, layers).ToArray(), stride) { }

    public LayerLayout(int width, int height, IReadOnlyList<int> levels, int stride)
    {
        if (width <= 0 || height <= 0 || levels.Count < 2 || levels.Count > MaxElevation - MinElevation + 1 ||
            levels[0] != 0 || levels.Any(e => e < MinElevation || e > MaxElevation) ||
            levels.Distinct().Count() != levels.Count || levels.Max() - levels.Min() + 1 != levels.Count ||
            stride != CalculateStride(height))
            throw new ArgumentOutOfRangeException(nameof(levels), "Invalid or unsupported layer layout.");
        _ = checked(width * checked(stride * levels.Count));
        Width = width; Height = height; Stride = stride; elevations = levels.ToArray();
        lowest = elevations.Min(); highest = elevations.Max();
        ordered = Enumerable.Range(0, elevations.Length).OrderBy(id => elevations[id]).ToArray();
        // Array.AsReadOnly allocates a wrapper per call; these views are stable for an immutable layout.
        elevationView = Array.AsReadOnly(elevations);
        orderedView = Array.AsReadOnly(ordered);
        // above[id] is the ascending-elevation suffix of `ordered`, below[id] the descending prefix.
        // These used to be LINQ projections rebuilt with a sort on every access, including per-cell
        // paths (HasSurface, OpenLaunchColumn, SkyExposure.IsExposed).
        above = new int[elevations.Length][];
        below = new int[elevations.Length][];
        for (int position = 0; position < ordered.Length; position++)
        {
            int id = ordered[position];
            above[id] = ordered.Skip(position + 1).ToArray();
            below[id] = ordered.Take(position).Reverse().ToArray();
        }
    }

    public int ElevationOf(int layerId) => elevations[layerId];
    public bool TryGetLayerAtElevation(int elevation, out int layerId)
    { layerId = Array.IndexOf(elevations, elevation); return layerId >= 0; }
    public int LayerAt(int elevation) => TryGetLayerAtElevation(elevation, out int id) ? id :
        throw new ArgumentOutOfRangeException(nameof(elevation), "Elevation is not allocated.");
    public bool TryGetAdjacentLayer(int layerId, int direction, out int result) =>
        TryGetLayerAtElevation(ElevationOf(layerId) + direction, out result);
    /// <summary>Layers strictly above the given layer, in ascending elevation.</summary>
    public IReadOnlyList<int> Above(int layerId) => above[layerId];
    /// <summary>Layers strictly below the given layer, in descending elevation (nearest first).</summary>
    public IReadOnlyList<int> Below(int layerId) => below[layerId];
    // Read-only ship previews may address upper bands that will be appended on confirmation.
    public int LayerOrFuture(int elevation) => TryGetLayerAtElevation(elevation,out int id)?id:LayerCount+elevation-HighestElevation-1;
    public int ElevationOrFuture(int id) => id<LayerCount?ElevationOf(id):HighestElevation+1+id-LayerCount;
    public int ProjectionLayer(double height) => LayerAt(Math.Clamp((int)Math.Floor(height/3),LowestElevation,HighestElevation));
    public int Distance(int a, int b) => Math.Abs(ElevationOf(a) - ElevationOf(b));
    public LayerLayout Append(int elevation) => new(Width, Height, elevations.Append(elevation).ToArray(), Stride);
    public bool Extends(LayerLayout other) => Width == other.Width && Height == other.Height &&
        Stride == other.Stride && elevations.Take(other.LayerCount).SequenceEqual(other.elevations);

    public static int CalculateStride(int height)
    {
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        return checked((checked(height + 17) / 17) * 17);
    }
    public bool Contains(LayerCell cell) =>
        (uint)cell.LayerId < (uint)LayerCount && (uint)cell.X < (uint)Width && (uint)cell.Z < (uint)Height;
    public StorageCell ToStorage(LayerCell cell)
    {
        if (!Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell));
        return new StorageCell(cell.X, 0, checked(cell.LayerId * Stride + cell.Z));
    }
    public bool TryDecode(StorageCell cell, out LayerCell result)
    {
        result = default;
        if (cell.Y != 0 || (uint)cell.X >= (uint)Width || cell.Z < 0 || cell.Z >= StorageHeight) return false;
        int layer = cell.Z / Stride, localZ = cell.Z % Stride;
        if (localZ >= Height) return false;
        result = new LayerCell(layer, cell.X, localZ);
        return true;
    }
    public bool MatchesStorage(int width, int height) => Width == width && StorageHeight == height;
}
