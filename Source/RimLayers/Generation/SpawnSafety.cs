using RimLayers.Recovery;
using Verse;

namespace RimLayers.Generation;

// Existing callers join the shared queue; there is no competing Pawn-only relocation path.
internal static class SpawnSafety
{
    internal static void Recover(Map map)=>EntityRecoveryService.Current?.QueueMap(map);
}
