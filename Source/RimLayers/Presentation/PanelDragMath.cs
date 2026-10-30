namespace RimLayers.Presentation;

/// <summary>
/// Pure geometry for the draggable floor panel, kept free of Unity types so the clamping rules can
/// be tested without the game assembly. The panel keeps its native upper-right anchor and stores a
/// user offset from it; the offset is re-clamped every frame, so a stale offset from a larger screen
/// can never strand the panel outside the current one.
/// </summary>
public static class PanelDragMath
{
    /// <summary>Smallest gap kept between the panel and any screen edge.</summary>
    public const float Margin = 4f;
    /// <summary>Height of the drag strip drawn directly above the panel.</summary>
    public const float HandleHeight = 14f;

    /// <summary>
    /// One axis: the panel keeps both ends on screen, and <paramref name="topInset"/> additionally
    /// reserves room for the drag strip drawn above it.
    /// </summary>
    public static float ClampAxis(float position, float size, float screenSize, float topInset = 0)
    {
        float min = Margin + Math.Max(0, topInset);
        // A panel taller than the screen has no valid range; the near margin wins instead of the
        // inverted maximum Math.Clamp would reject.
        float max = Math.Max(min, screenSize - size - Margin);
        return float.IsFinite(position) ? Math.Clamp(position, min, max) : min;
    }

    public static (float X, float Y) Clamp(float x, float y, float width, float height,
        float screenWidth, float screenHeight) =>
        (ClampAxis(x, width, screenWidth), ClampAxis(y, height, screenHeight, HandleHeight));

    /// <summary>Stored offset that reproduces a clamped position, so dragging cannot accumulate drift.</summary>
    public static (float X, float Y) OffsetFor(float x, float y, float defaultX, float defaultY) =>
        (x - defaultX, y - defaultY);
}
