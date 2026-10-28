namespace RimLayers.Areas;

/// <summary>
/// Layer/surface rules shared by the vanilla area and zone gates.
/// Deliberately free of Verse and RimWorld types so Tests/AreaLayerTests can compile this
/// file directly and cover the geometry without loading the game assembly.
/// </summary>
public static class AreaLayerRules
{
    /// <summary>Why a cell was refused, so the designator can show the right message.</summary>
    public enum Gate
    {
        Allow,
        OffLayer,
        NoSurface
    }

    /// <summary>
    /// A painted cell must belong to the layer the player is looking at and must carry a real
    /// floor. Untranslated storage cells (the stride isolation band) and every other layer fail
    /// the first test; open air on the viewed layer fails the second.
    /// </summary>
    public static Gate Evaluate(bool decoded, int cellLayer, int viewedLayer, bool hasSurface)
    {
        if (!decoded || cellLayer != viewedLayer) return Gate.OffLayer;
        return hasSurface ? Gate.Allow : Gate.NoSurface;
    }

    /// <summary>
    /// Automatic home-area and roof spread (vanilla grows a fixed rect around a building, a zone
    /// cell or a room) stays on the anchor's own layer and never leaves the floor. Without this a
    /// building near the end of a layer band seeds home area onto the neighbouring layer.
    /// </summary>
    public static bool AcceptsSpread(int cellLayer, int anchorLayer, bool hasSurface)
        => hasSurface && cellLayer == anchorLayer;

    /// <summary>
    /// Value a cell should take when an area is inverted. Vanilla flips the whole backing grid,
    /// which would turn on every other layer and every isolation band, so only cells that are part
    /// of the buildable universe participate.
    /// </summary>
    public static bool InvertValue(bool current, bool hasSurface) => hasSurface && !current;
}
