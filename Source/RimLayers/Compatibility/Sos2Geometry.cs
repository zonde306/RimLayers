namespace RimLayers.Compatibility;

/// <summary>
/// Pure storage-coordinate arithmetic shared by the SOS2 adapter. No Verse types: the whole file is
/// compiled directly into Tests/Sos2GeometryTests so the band maths can be checked without the game.
/// </summary>
/// <remarks>
/// SOS2 keeps its own ship model (`ShipMapComp.MapShipCells`, a flat <c>Dictionary&lt;IntVec3,…&gt;</c>).
/// It is NOT an Odyssey gravship and must never enter <c>RimLayers.Ships</c>. What it does share is the
/// coordinate hazard: it reads <c>Map.Size</c> as if it were the logical rectangle, and it walks ships
/// with same-layer cardinal adjacency only. Both are handled here.
/// </remarks>
public static class Sos2Geometry
{
    /// <summary>Layer id encoded in a storage z, or -1 when the z falls in the padding band.</summary>
    public static int LayerOf(int storageZ, int stride, int layerCount)
    {
        if (stride <= 0 || storageZ < 0) return -1;
        int layer = storageZ / stride;
        return layer < layerCount ? layer : -1;
    }

    /// <summary>Logical z inside its own band.</summary>
    public static int LocalZ(int storageZ, int stride) => stride <= 0 ? storageZ : storageZ % stride;

    /// <summary>
    /// What a caller must use in place of <c>Map.Size.z</c>. A layered map stores every band, so the
    /// logical north/south length is the layout height; an unmanaged map keeps its own size.
    /// </summary>
    public static int LogicalLength(int storageLength, int logicalHeight, int stride, int layerCount) =>
        stride > 0 && layerCount > 1 ? logicalHeight : storageLength;

    /// <summary>True when the cell is inside the rectangle this layer count actually stores.</summary>
    public static bool InStorage(int x, int z, int width, int stride, int layerCount) =>
        x >= 0 && z >= 0 && x < width && stride > 0 && z < stride * layerCount;

    /// <summary>
    /// Same-layer cardinal neighbours first, then the two vertical neighbours of the column. The
    /// vertical pair is what makes an upper deck part of the same ship; callers keep their own
    /// membership filters (MapShipCells / Area), so returning the raw geometry is safe.
    /// </summary>
    public static IEnumerable<(int X, int Y, int Z)> Neighbors(
        int x, int y, int z, int width, int height, int stride, int layerCount)
    {
        int layer = LayerOf(z, stride, layerCount);
        if (layer < 0) yield break;
        int localZ = LocalZ(z, stride);
        int bandStart = z - localZ;
        if (x > 0) yield return (x - 1, y, z);
        if (x + 1 < width) yield return (x + 1, y, z);
        if (localZ > 0) yield return (x, y, z - 1);
        if (localZ + 1 < height) yield return (x, y, z + 1);
        if (layer > 0) yield return (x, y, bandStart - stride + localZ);
        if (layer + 1 < layerCount) yield return (x, y, bandStart + stride + localZ);
    }

    /// <summary>
    /// Rotates/mirrors a ship cell inside the logical rectangle while keeping its band. The native
    /// version mirrors against <c>Map.Size</c>, which spans every band, so a multi-deck ship would be
    /// mapped into the storage gap.
    /// </summary>
    public static (int X, int Y, int Z) Mirror(int x, int y, int z, int width, int height, int stride, int layerCount, int rotb)
    {
        int layer = LayerOf(z, stride, layerCount);
        if (layer < 0) return (x, y, z);
        int localZ = LocalZ(z, stride);
        int bandStart = z - localZ;
        return rotb switch
        {
            2 => (width - x, y, bandStart + (height - localZ)),
            3 => (width - localZ, y, bandStart + x),
            _ => (x, y, z),
        };
    }

    /// <summary>
    /// Centre offset for dropping a ship of <paramref name="size"/> onto a map whose z extent is
    /// <paramref name="storageLength"/> but whose logical height is <paramref name="height"/>.
    /// </summary>
    public static (int X, int Y, int Z) CenterOnMap(
        int storageWidth, int storageLength, int height, int stride, int layerCount,
        int sizeX, int sizeZ, int minX, int minZ)
    {
        int logicalLength = LogicalLength(storageLength, height, stride, layerCount);
        return (storageWidth / 2 - sizeX / 2 - minX, 0, logicalLength / 2 - sizeZ / 2 - minZ);
    }

    /// <summary>
    /// True when both storage z values live in the same band. An unmanaged map, or a cell that lands
    /// in the padding row, answers true: the adapter has no opinion there and must not veto anything.
    /// </summary>
    public static bool SameLayer(int firstZ, int secondZ, int stride, int layerCount)
    {
        if (stride <= 0 || layerCount < 2) return true;
        int first = LayerOf(firstZ, stride, layerCount);
        int second = LayerOf(secondZ, stride, layerCount);
        if (first < 0 || second < 0) return true;
        return first == second;
    }

    /// <summary>
    /// What a caller must use in place of a whole <c>Map.Size</c>. Callers that hand a map size to
    /// <c>MapGenerator.GenerateMap</c> pass the storage rectangle, which on a layered map is every
    /// band plus the padding.
    /// </summary>
    public static (int X, int Y, int Z) LogicalSize(int x, int z, int logicalHeight, int stride, int layerCount) =>
        (x, 0, LogicalLength(z, logicalHeight, stride, layerCount));

    /// <summary>
    /// Which map edge an SOS2 shot comes from, as the <paramref name="side"/> index
    /// <see cref="EdgeCell"/> expects (0 north, 1 east, 2 south, 3 west). Mirrors
    /// <c>ShipMapComp.FindProjectileSpawnDirection</c> but compares against the band height instead of
    /// the storage length.
    /// </summary>
    public static int ProjectileSide(
        int x, int z, int width, int height, int stride, int layerCount, int heading, int engineRot)
    {
        if (heading == 1) return NormalizeRotation(engineRot);
        if (heading == -1) return NormalizeRotation(engineRot + 2);
        int localZ = LocalZ(z, stride);
        if (x < width / 2 && x < localZ && x < height - localZ) return 3;
        if (x > width / 2 && width - x < localZ && width - x < height - localZ) return 1;
        return localZ > height / 2 ? 0 : 2;
    }

    private static int NormalizeRotation(int rotation) => ((rotation % 4) + 4) % 4;

    /// <summary>
    /// The band-local edge cell a ship turret fires at. The native version clamps the upper bound to
    /// <c>Map.Size.z</c> (one past the last row) and lands in the storage gap on a layered map.
    /// </summary>
    public static (int X, int Y, int Z) EdgeCell(
        int x, int y, int z, int width, int height, int stride, int layerCount, int miss, int side)
    {
        int layer = LayerOf(z, stride, layerCount);
        // A caller already in the padding band has no deck to aim along. Return it unchanged rather
        // than relocating the shot onto band 0, which would hide the real defect.
        if (layer < 0) return (x, y, z);
        int localZ = LocalZ(z, stride);
        int bandStart = z - localZ;
        int nx = x, nz = localZ;
        switch (side)
        {
            case 0: nz = height - 1; nx = x + miss; break;
            case 1: nx = width - 1; nz = localZ + miss; break;
            case 2: nz = 0; nx = x + miss; break;
            default: nx = 0; nz = localZ + miss; break;
        }
        nx = nx < 0 ? 0 : nx >= width ? width - 1 : nx;
        nz = nz < 0 ? 0 : nz >= height ? height - 1 : nz;
        return (nx, y, bandStart + nz);
    }
}
