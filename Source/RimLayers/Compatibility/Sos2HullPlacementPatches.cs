using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Lets SOS2's own buildings stand on an upper deck that RimLayers laid.
/// </summary>
/// <remarks>
/// <c>PlaceWorker_OnShipHull.AllowsPlacing</c> decides "is this on a hull?" from
/// <c>location.GetThingList(map)</c> alone: it wants a <c>CompShipCachePart</c> whose props say
/// plating (or a hardpoint, for turrets). An upper deck has no hull tile in it - the hull is one
/// layer down and the deck is terrain - so every bridge, engine, shield and turret was refused with
/// "must be placed on a ship hull" the moment a second storey existed.
/// <para>
/// The relax is deliberately narrow: it applies only when <paramref name="location"/> is itself a
/// RimLayers deck terrain and the cell directly below is a cell SOS2 already counts as ship. Stacking
/// decks cannot therefore let arbitrary buildings land on any roof, and a deck over open ground still
/// fails the native check.
/// </para>
/// </remarks>
internal static class Sos2HullPlacementPatches
{
    internal static void Install(Harmony harmony)
    {
        Type? worker = AccessTools.TypeByName("SaveOurShip2.PlaceWorker_OnShipHull");
        if (worker == null)
        {
            Log.Warning("[RimLayers] SOS2 PlaceWorker_OnShipHull not found; SOS2 buildings stay limited to the hull deck.");
            return;
        }
        MethodInfo? target = AccessTools.Method(worker, "AllowsPlacing", new[]
        {
            typeof(BuildableDef), typeof(IntVec3), typeof(Rot4), typeof(Map), typeof(Thing), typeof(Thing)
        });
        if (target == null)
        {
            Log.Warning("[RimLayers] SOS2 PlaceWorker_OnShipHull.AllowsPlacing changed shape; SOS2 buildings stay limited to the hull deck.");
            return;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(Sos2HullPlacementPatches), nameof(AllowsPlacingPostfix)));
    }

    /// <summary>
    /// Only ever upgrades a rejection to an acceptance, and only for a cell this mod recognises as a
    /// deck over an SOS2 hull.
    /// </summary>
    private static void AllowsPlacingPostfix(BuildableDef def, IntVec3 loc, Map map, ref AcceptanceReport __result)
    {
        if (__result.Accepted) return;
        if (map == null || !loc.InBounds(map)) return;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout == null || !owner.TryDecode(loc, out LayerCell logical) || logical.LayerId == 0) return;
        if (!RimLayers.Structure.StructuralSupport.IsDeckTerrain(map.terrainGrid.FoundationAt(loc)) &&
            !RimLayers.Structure.StructuralSupport.IsDeckTerrain(map.terrainGrid.TerrainAt(loc))) return;
        IntVec3 hull = owner.Adjacent(loc, -1);
        if (!hull.IsValid || !Sos2Compatibility.IsShipDeck(owner, hull)) return;
        // A salvage bay on the hull below already forbids anything else there, and SOS2's worker
        // reports that case as a bare "false" rather than a translated reason; keep its veto.
        if (!HullIsClearOfRestrictedBay(def, hull, map)) return;
        __result = AcceptanceReport.WasAccepted;
    }

    /// <summary>
    /// Re-runs SOS2's own restricted-bay test against the hull cell, because the native worker never
    /// looked at it (it only inspects the deck cell, which has no buildings at all).
    /// </summary>
    private static bool HullIsClearOfRestrictedBay(BuildableDef def, IntVec3 hull, Map map)
    {
        Type? bay = AccessTools.TypeByName("SaveOurShip2.CompShipBaySalvage");
        if (bay == null) return true;
        foreach (Thing thing in hull.GetThingList(map))
        {
            if (thing is Building building && building.AllComps.Any(comp => bay.IsInstanceOfType(comp))) return false;
        }
        return true;
    }
}
