using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Makes hull foam repair the nearest breach on the whole ship, not the shallowest one.
/// </summary>
/// <remarks>
/// <c>SpaceShipCache.SlowTick</c> repairs one breached cell per distributor per tick, chosen by
/// <c>BuildingsToFoam.OrderBy(t =&gt; t.Item3).First()</c>. <c>Item3</c> is the breadth-first path
/// length back to the bridge, and on a multi-deck hull that number is no longer a distance: the path
/// walks the whole ship, so a hole three decks up beside the bridge can outrank the hole next to the
/// distributor. Foam then travels past a nearby breach to seal a distant one, and on a hull whose
/// repair budget runs out mid-flight that leaves the adjacent hole open.
/// <para>
/// The replacement keeps the native shape — one repair per distributor per tick, fuel gated the same
/// way — and ranks candidates by how many decks away the breach is, then by the native path length.
/// This is an ordering only. Foam deliberately still reaches every deck of the ship, the way
/// <c>ShipMapComp.VecHasLS</c> lets any active life support cover the whole hull: a hull with one
/// distributor on the ground deck must still be able to seal a top-deck breach, otherwise that hole
/// stays open forever. Verified by <c>sos2-foam-covers-the-whole-ship</c> and
/// <c>sos2-foam-spends-its-own-fuel-across-decks</c>.
/// </para>
/// </remarks>
internal static class Sos2FoamPatches
{
    private static readonly Type? ShipCacheType = AccessTools.TypeByName("SaveOurShip2.SpaceShipCache");

    private static readonly FieldInfo? FoamTargets =
        AccessTools.Field(ShipCacheType, "BuildingsToFoam");

    private static readonly FieldInfo? Distributors =
        AccessTools.Field(ShipCacheType, "FoamDistributors");

    // IsWreck (SpaceShipCache.cs:180) and Faction (:80) are expression-bodied properties, not fields.
    // Looking them up with AccessTools.Field returns null, which used to make this Prefix bail out and
    // silently hand control back to the native same-deck-blind ordering.
    private static readonly PropertyInfo? ShipIsWreck =
        AccessTools.Property(ShipCacheType, "IsWreck");

    private static readonly FieldInfo? ShipMap =
        AccessTools.Field(ShipCacheType, "map");

    private static readonly PropertyInfo? ShipFaction =
        AccessTools.Property(ShipCacheType, "Faction");

    [HarmonyPatch]
    private static class SlowTickFoamPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(ShipCacheType, "SlowTick");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            if (FoamTargets == null || Distributors == null || ShipIsWreck == null) return true;
            if (ShipIsWreck.GetValue(__instance) is true) return true;
            if (FoamTargets.GetValue(__instance) is not IEnumerable targets || CountOf(targets) == 0) return true;
            if (Distributors.GetValue(__instance) is not IEnumerable distributors || CountOf(distributors) == 0) return true;
            Map? map = ShipMap?.GetValue(__instance) as Map;
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out _, out _, out _)) return true;

            foreach (ThingComp distributor in distributors)
            {
                if (distributor?.parent is not Building { Spawned: true } building) continue;
                if (CountOf(targets) == 0) break;
                if (building.TryGetComp<CompRefuelable>() is not { Fuel: > 0 } fuel) continue;
                if (building.TryGetComp<CompPowerTrader>() is not { PowerOn: true }) continue;
                if (PickNearest(targets, owner, building.Position) is not { } target) continue;
                Thing replacer = ThingMaker.MakeThing(target.Hull
                    ? ResourceBankDef("HullFoamWall")
                    : ResourceBankDef("ShipHullfoamTile"));
                if (ShipFaction?.GetValue(__instance) is Faction faction) replacer.SetFaction(faction);
                if (!GenPlace.TryPlaceThing(replacer, target.Cell, map, ThingPlaceMode.Direct)) continue;
                if (FoamTargets.GetValue(__instance) is { } live) RemoveTarget(live, target.Entry);
                fuel.ConsumeFuel(1);
            }
            return false;
        }

        /// <summary>
        /// <c>BuildingsToFoam</c> is a <c>HashSet&lt;Tuple&lt;bool,IntVec3,int&gt;&gt;</c> and
        /// <c>HashSet&lt;T&gt;</c> implements neither non-generic <see cref="ICollection"/> nor
        /// <see cref="IList"/>, so the count and the removal both go through reflection.
        /// </summary>
        private static int CountOf(IEnumerable collection) =>
            collection.GetType().GetProperty("Count")?.GetValue(collection) as int? ?? -1;

        private static void RemoveTarget(object collection, object entry)
        {
            collection.GetType().GetMethod("Remove", new[] { entry.GetType() })?.Invoke(collection, new[] { entry });
        }

        /// <summary>
        /// Nearest breach for one distributor: fewest decks away first, then the native path length.
        /// Null when the entry list is empty. This only decides the order candidates are repaired in;
        /// a breach on any deck stays eligible, so a lone ground-deck distributor still covers the
        /// whole hull.
        /// </summary>
        private static FoamCandidate? PickNearest(IEnumerable targets, LayerMapComponent owner, IntVec3 from)
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
