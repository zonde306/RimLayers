using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Keeps SOS2's own placement workers inside one deck band.
/// </summary>
/// <remarks>
/// <c>PlaceWorker_ShipHull</c> and <c>PlaceWorker_ShipPlating</c> validate the cells of
/// <c>GenAdj.OccupiedRect</c> and then ask <c>ShipMapComp.ShipIndexOnVec</c> / <c>ShipCellsOf</c> about
/// them. On a layered map a 3x3 or 5x5 part can straddle a band boundary: the cells in the second band
/// are storage coordinates of a different deck, so a footprint that visually sits on deck 0 would be
/// validated against deck 1's cells and, worse, could be spawned half on each deck. SOS2's cache then
/// records <c>cellsUnder</c> spanning two bands and its breadth-first walks see a part that belongs to
/// two decks at once.
/// <para>
/// Rejecting a straddling footprint is the honest answer: the player is told to move the part instead
/// of getting a ship whose part is half on the deck below. Non-straddling footprints are left to SOS2
/// unchanged, so hardpoints, geysers, blueprints and faction checks all keep their original behaviour.
/// </para>
/// </remarks>
internal static class Sos2PlacementPatches
{
    /// <summary>
    /// Shared guard for both ship placement workers: the whole footprint must decode to one deck.
    /// </summary>
    internal static AcceptanceReport FitsOneDeck(BuildableDef def, IntVec3 loc, Rot4 rot, Map map)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return true;
        // A footprint that does not decode at all is outside the map; SOS2 and the vanilla placement
        // code already report that, so do not steal the message.
        bool first = true;
        int layer = 0;
        foreach (IntVec3 cell in GenAdj.OccupiedRect(loc, rot, def.Size))
        {
            if (!owner.TryDecode(cell, out LayerCell logical)) return true;
            if (first)
            {
                layer = logical.LayerId;
                first = false;
                continue;
            }
            if (logical.LayerId != layer) return "RL_PlacementCrossesDecks".Translate();
        }
        return true;
    }

    [HarmonyPatch]
    private static class ShipHullPlacementPatch
    {
        private static System.Reflection.MethodBase? TargetMethod() =>
            Sos2Compatibility.PlacementWorkerMethod("PlaceWorker_ShipHull");

        [HarmonyPrefix]
        private static bool Prefix(BuildableDef def, IntVec3 loc, Rot4 rot, Map map, ref AcceptanceReport __result)
        {
            AcceptanceReport fits = FitsOneDeck(def, loc, rot, map);
            if (fits.Accepted) return true;
            __result = fits;
            return false;
        }
    }

    [HarmonyPatch]
    private static class ShipPlatingPlacementPatch
    {
        private static System.Reflection.MethodBase? TargetMethod() =>
            Sos2Compatibility.PlacementWorkerMethod("PlaceWorker_ShipPlating");

        [HarmonyPrefix]
        private static bool Prefix(BuildableDef def, IntVec3 loc, Rot4 rot, Map map, ref AcceptanceReport __result)
        {
            AcceptanceReport fits = FitsOneDeck(def, loc, rot, map);
            if (fits.Accepted) return true;
            __result = fits;
            return false;
        }
    }
}
