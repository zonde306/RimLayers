namespace RimLayers.Structure;

/// <summary>
/// Integer arithmetic behind mirroring a footprint onto the adjacent layer. Kept free of map and
/// Verse types so it can be unit tested: a layer owns a band of storage rows, and a footprint must be
/// re-based by the difference of the two band origins. Adding a band offset to an already-offset row
/// (the shape the existing <c>LayerRect</c>/<c>At</c> pair makes easy to write by accident) puts the
/// copy in the wrong band, or outside the map entirely.
/// </summary>
public static class LayerPairMath
{
    /// <summary>Re-bases a row from one layer's band to another, preserving its in-layer row.</summary>
    public static int ShiftZ(int minZ, int fromBandMinZ, int toBandMinZ) => toBandMinZ + (minZ - fromBandMinZ);

    /// <summary>True for the two adjacent-layer directions the pairing buttons use.</summary>
    public static bool IsVertical(int direction) => direction is 1 or -1;

    /// <summary>
    /// Rotation whose <c>Width</c> follows <paramref name="width"/>: a rectangular footprint keeps its
    /// long side on the same axis, and a square one never rotates.
    /// </summary>
    public static bool LongSideHorizontal(int width, int height) => width >= height;
}
