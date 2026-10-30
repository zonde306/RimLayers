using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Lets SOS2's heat network reach a deck above the reactor.
/// </summary>
/// <remarks>
/// <c>ShipMapComp.AccumulateToNetNew</c> is a cardinal flood fill: it registers a pipe, stamps its own
/// footprint into <c>grid</c>, then looks for more pipes on <c>GenAdj.CellsAdjacentCardinal</c>. On a
/// layered map a conduit running up through a deck hatch is never adjacent, so the upper decks end up
/// on their own net with no sink — reactors overheat and shields never charge.
/// <para>
/// This reuses <see cref="Sos2AdjacencyPatches.ReplaceAdjacency"/> instead of repeating the IL walk, so
/// the anchor-count assertion that guards every other SOS2 walk guards this one too.
/// </para>
/// </remarks>
internal static class Sos2HeatPatches
{
    [HarmonyPatch]
    private static class AccumulateToNetNewPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipMapCompType, "AccumulateToNetNew");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        /// <summary>
        /// The rewritten walk asks <see cref="Sos2CellGraph"/> which map answers the band arithmetic,
        /// and the original call site passes no map. Without this the vertical neighbours are dropped
        /// and every deck silently keeps its own net — which is exactly the failure this patch exists
        /// to prevent, so it must not be able to fail quietly.
        /// <para>
        /// A heat net must not cross into a different ship. Two hulls parked on neighbouring decks
        /// share no conduit, but a stacked pair of cells can be claimed by two different ships, and
        /// merging them would let a reactor on one hull charge a sink on the other. SOS2 groups a net
        /// per map and never per ship, so the ship identity is checked here instead.
        /// </para>
        /// </summary>
        private static void Prefix(MapComponent __instance) =>
            Sos2CellGraph.Enter(__instance?.map, SameShip);

        /// <summary>True when both cells belong to the same SOS2 ship, or either one is unclaimed.</summary>
        private static bool SameShip(IntVec3 from, IntVec3 to)
        {
            Map? map = Sos2CellGraph.Current;
            if (map == null) return true;
            return Sos2Compatibility.ShipIndexAt(map, from) == Sos2Compatibility.ShipIndexAt(map, to);
        }

        [HarmonyPostfix]
        [HarmonyFinalizer]
        private static void Finalizer() => Sos2CellGraph.Leave();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Sos2AdjacencyPatches.ReplaceAdjacency(instructions, "ShipMapComp.AccumulateToNetNew", 1);
    }
}
