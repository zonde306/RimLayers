using RimLayers.Environment;
using RimLayers.Navigation;

namespace RimLayers.Diagnostics;

/// <summary>
/// Read-only snapshot of the counters added by the performance work. These exist so the automated
/// regression can assert that the optimisations actually engage (for example that a same-floor route
/// prices no connector legs) instead of only checking that nothing crashes.
/// </summary>
public static class PerfCounters
{
    public readonly record struct Snapshot(
        long ForCalls,
        long ForScans,
        long RouteRequests,
        long RouteLegPathfinds,
        long RouteDirectShortCircuits,
        long HeatExchanges,
        long HeatRebuilds,
        long HeatColumnEvaluations,
        long SkyRoomInvalidations);

    public static Snapshot Capture(Core.LayerMapComponent? owner) => new(
        Core.LayerMapComponent.ForCalls,
        Core.LayerMapComponent.ForScans,
        LayerRouter.Requests,
        LayerRouter.LegPathfinds,
        LayerRouter.DirectShortCircuits,
        owner?.Heat.Exchanges ?? 0,
        owner?.Heat.Rebuilds ?? 0,
        owner?.Heat.ColumnEvaluations ?? 0,
        owner?.Sky.RoomInvalidations ?? 0);

    /// <summary>Reset the process-wide counters. Per-map counters keep their own lifetime.</summary>
    public static void ResetProcessCounters()
    {
        Core.LayerMapComponent.ResetForStatistics();
        LayerRouter.ResetStatistics();
    }

    /// <summary>
    /// Fraction of For() lookups served without scanning Map.components. The optimisation is
    /// effective when this is close to 1 on a busy map.
    /// </summary>
    public static double ForCacheHitRatio()
    {
        long calls = Core.LayerMapComponent.ForCalls;
        return calls == 0 ? 1d : 1d - (double)Core.LayerMapComponent.ForScans / calls;
    }
}
