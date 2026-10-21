namespace RimLayers.Presentation;

/// <summary>Screen-space transforms; independent of storage bands and simulation ticks.</summary>
internal static class LayerTransitionMath
{
    internal const float Duration = .320f;
    internal static float Ease(float elapsed)
    {
        float t = Math.Clamp(elapsed / Duration, 0, 1);
        return t * t * (3 - 2 * t);
    }
    internal static int Direction(int sourceElevation, int targetElevation) =>
        Math.Sign(targetElevation - sourceElevation);
    internal static TransitionSample Sample(float elapsed, int direction)
    {
        float t = Ease(elapsed);
        return new(1 - .04f * direction * t, .01f * direction * t,
            1 + .04f * direction * (1 - t), -.01f * direction * (1 - t), 1 - t);
    }
    // UVs are bottom-up; screen-space translation is positive downward.
    internal static (float X, float Y, float Width, float Height) UV(float scale, float down)
    {
        float size = 1 / scale;
        return ((1 - size) / 2, (1 - size) / 2 + down / scale, size, size);
    }
}
internal readonly record struct TransitionSample(float OldScale, float OldDown,
    float NewScale, float NewDown, float OldAlpha);
