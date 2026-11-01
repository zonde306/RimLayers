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

        /// <summary>
        /// True when a deck boundary between two stacked cells carries heat.
        /// </summary>
        /// <remarks>
        /// A vertical edge is only allowed when BOTH cells hold heat hardware, i.e. the player ran a
        /// conduit, a heatsink or a reactor up to that exact column. The earlier rule walked through
        /// every stacked pair unconditionally, which let a pipe on one deck silently join a sink on
        /// another and made "thermal conduit" stop meaning anything on a layered map. Matching the
        /// same-layer rule (also hardware, also adjacency) keeps both directions honest, and the
        /// player-facing answer is RimLayers' own <c>RL_HeatRiser</c>.
        /// </remarks>
        private static bool SameShip(IntVec3 from, IntVec3 to)
        {
            Map? map = Sos2CellGraph.Current;
            if (map == null) return true;
            if (!CarriesHeat(map, from) || !CarriesHeat(map, to)) return false;
            int a = Sos2Compatibility.ShipIndexAt(map, from), b = Sos2Compatibility.ShipIndexAt(map, to);
            // -1 means "no ship claims this cell". A pad fixture, and any cell built before the ship
            // cache caught up, reads as -1 on both sides; refusing that pair because -1 != 0 would drop
            // every vertical edge on the deck a player is actually standing on. Only two DIFFERENT
            // claimed hulls are a real cross-ship join.
            return a < 0 || b < 0 || a == b;
        }

        /// <summary>True when the cell holds a building with SOS2's heat comp.</summary>
        private static bool CarriesHeat(Map map, IntVec3 cell)
        {
            foreach (Thing thing in cell.GetThingList(map))
                if (Sos2Compatibility.IsHeatPart(thing)) return true;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyFinalizer]
        private static void Finalizer() => Sos2CellGraph.Leave();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Sos2AdjacencyPatches.ReplaceAdjacency(instructions, "ShipMapComp.AccumulateToNetNew", 1);
    }
}
