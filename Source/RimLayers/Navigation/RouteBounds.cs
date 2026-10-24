namespace RimLayers.Navigation;

/// <summary>
/// Pure decision logic for the cross-floor router, kept free of Verse types so the automated
/// tests can exercise the optimality argument directly.
/// </summary>
public static class RouteBounds
{
    /// <summary>
    /// True when a complete same-floor route of <paramref name="directCost"/> is provably optimal.
    ///
    /// Leaving the start floor and returning to the target floor crosses at least two connectors.
    /// Every connector costs at least <paramref name="cheapestCrossing"/> ticks, and all leg costs
    /// are non-negative, so every connector route costs at least 2 * cheapestCrossing. When the
    /// direct leg is no more expensive than that, no connector route can beat it and pricing the
    /// connector legs cannot change the result.
    ///
    /// A zero or non-finite cheapest crossing makes the argument vacuous, so the caller must fall
    /// back to the complete search in that case.
    /// </summary>
    public static bool DirectIsOptimal(float directCost, float cheapestCrossing)
    {
        if (!float.IsFinite(directCost) || !float.IsFinite(cheapestCrossing)) return false;
        if (cheapestCrossing <= 0f) return false;
        return directCost <= 2f * cheapestCrossing;
    }
}
