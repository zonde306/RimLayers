using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Keeps hull foam repairs on the deck that was actually damaged.
/// </summary>
/// <remarks>
/// <c>SpaceShipCache.SlowTick</c> repairs one breached cell per distributor per tick, chosen by
/// <c>BuildingsToFoam.OrderBy(t =&gt; t.Item3).First()</c>. <c>Item3</c> is the breadth-first path
/// length back to the bridge, which on a layered ship is meaningless across decks: a breach on deck 3
/// next to the distributor can outrank one on deck 0, so a foam distributor on the ground deck seals a
/// hole three decks up while the hull next to it stays open.
/// <para>
/// The replacement keeps the native shape — one repair per distributor per tick, fuel gated the same
/// way — but ranks candidates by deck distance first and path length second. Deck distance comes from
/// the layout, so a distributor never repairs a deck it cannot reach through its own hull.
/// </para>
/// </remarks>
internal static class Sos2FoamPatches
{
    private static readonly FieldInfo? FoamTargets =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "BuildingsToFoam");

    private static readonly FieldInfo? Distributors =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "FoamDistributors");

    private static readonly FieldInfo? ShipIsWreck =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "IsWreck");

    private static readonly FieldInfo? ShipMap =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "map");

    private static readonly FieldInfo? ShipFaction =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "Faction");

    [HarmonyPatch]
    private static class SlowTickFoamPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "SlowTick");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            if (FoamTargets == null || Distributors == null || ShipIsWreck == null) return true;
            if (ShipIsWreck.GetValue(__instance) is true) return true;
            if (FoamTargets.GetValue(__instance) is not IList targets || targets.Count == 0) return true;
            if (Distributors.GetValue(__instance) is not IList distributors || distributors.Count == 0) return true;
            Map? map = ShipMap?.GetValue(__instance) as Map;
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out _, out _, out _)) return true;

            foreach (ThingComp distributor in distributors)
            {
                if (distributor?.parent is not Building { Spawned: true } building) continue;
                if (targets.Count == 0) break;
                if (building.TryGetComp<CompRefuelable>() is not { Fuel: > 0 } fuel) continue;
                if (building.TryGetComp<CompPowerTrader>() is not { PowerOn: true }) continue;
                if (PickNearest(targets, owner, building.Position) is not { } target) continue;

                Thing replacer = ThingMaker.MakeThing(target.Hull
                    ? ResourceBankDef("HullFoamWall")
                    : ResourceBankDef("ShipHullfoamTile"));
                if (ShipFaction?.GetValue(__instance) is Faction faction) replacer.SetFaction(faction);
                if (!GenPlace.TryPlaceThing(replacer, target.Cell, map, ThingPlaceMode.Direct)) continue;
                targets.Remove(target.Entry);
                fuel.ConsumeFuel(1);
            }
            return false;
        }

        /// <summary>
        /// Nearest breach for one distributor: same deck first, then the fewest decks away, then the
        /// native path length. Null when the entry list is empty.
        /// </summary>
        private static FoamCandidate? PickNearest(IList targets, LayerMapComponent owner, IntVec3 from)
        {
            if (!owner.TryDecode(from, out var origin)) return null;
            FoamCandidate? best = null;
            int bestDeck = int.MaxValue, bestPath = int.MaxValue;
            foreach (object entry in targets)
            {
                if (entry == null) continue;
                Type type = entry.GetType();
                if (type.GetProperty("Item2")?.GetValue(entry) is not IntVec3 cell) continue;
                bool hull = type.GetProperty("Item1")?.GetValue(entry) is true;
                int path = type.GetProperty("Item3")?.GetValue(entry) as int? ?? int.MaxValue;
                int decks = owner.TryDecode(cell, out var target) ? owner.Layout!.Distance(target.LayerId, origin.LayerId) : int.MaxValue;
                if (best != null && (decks > bestDeck || decks == bestDeck && path >= bestPath)) continue;
                best = new FoamCandidate(entry, cell, hull);
                bestDeck = decks;
                bestPath = path;
            }
            return best;
        }

        private static ThingDef ResourceBankDef(string name) =>
            DefDatabase<ThingDef>.GetNamedSilentFail(name)
            ?? throw new InvalidOperationException($"[RimLayers] SOS2 thing def not found: {name}");

        private readonly record struct FoamCandidate(object Entry, IntVec3 Cell, bool Hull);
    }
}
