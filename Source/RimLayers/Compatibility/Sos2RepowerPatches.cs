using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Re-powers every deck of a ship after it moves, not only the deck its bridge sits on.
/// </summary>
/// <remarks>
/// SOS2 restores power after a ship move in <c>SpaceShipCache.ForceRePowerOnTick</c>: it reconnects
/// the bridge's net and then switches every <c>CompPowerTrader</c> in that one net back on. That is
/// exactly right for a flat ship, where the whole hull is a single net. With RimLayers the power
/// graph is per deck — <see cref="Environment.PowerLinks.Neighbors"/> only crosses decks through an
/// explicit riser — so an engine or a reactor on the deck below is not in the bridge's net and is
/// never switched back on.
///
/// The observed result is not a dead engine: the propulsion net is intact (<c>PowerNet != null</c>)
/// and the flick switch is on, but <c>CompPowerTrader.PowerOn</c> stays false, so
/// <c>CompEngineTrail.CanFire</c> returns false, <c>SpaceShipCache.CanMove</c> is false,
/// <c>ShipMapComp</c> calls <c>MapFullStop()</c> — and because <c>LaunchShip</c> starts the flight at
/// <c>altitudeLand</c> with <c>Heading = 1</c>, the very next <c>TransitSlowTick</c> satisfies
/// <c>Altitude &lt;= altitudeLand &amp;&amp; Heading &lt; 1</c> and lands the ship back on the map it
/// just left. From the player's side the ship "flies up for a second and comes back".
///
/// So after a native re-power on a multi-deck ship, every powered part standing on one of that
/// ship's own decks is switched on as well. Parts off the ship are untouched, and a single-deck ship
/// keeps the native behaviour exactly.
/// </remarks>
[HarmonyPatch]
internal static class Sos2RepowerPatches
{
    /// <summary>
    /// How many parts this adapter switched back on, and how many times the native re-power ran on a
    /// multi-deck ship. Exposed so the in-game test can assert the patch actually did the work instead
    /// of inferring it from a ship that happened to survive.
    /// </summary>
    internal static int Restored;

    /// <summary>Native re-power passes seen on a multi-deck ship, whether or not anything was restored.</summary>
    internal static int Passes;

    private static MethodBase? TargetMethod()
    {
        return AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "ForceRePowerOnTick");
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        if (!Sos2Compatibility.Active || __instance == null) return;
        if (__instance.GetType().FullName != "SaveOurShip2.SpaceShipCache") return;
        Map? map = AccessTools.Property(__instance.GetType(), "Map")?.GetValue(__instance) as Map;
        if (map == null) return;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout == null || owner.Layout.LayerCount < 2) return;

        HashSet<IntVec3>? area = AccessTools.Field(__instance.GetType(), "Area")?.GetValue(__instance) as HashSet<IntVec3>;
        if (area == null || area.Count == 0) return;

        Passes++;

        int restored = 0;
        foreach (IntVec3 cell in area)
        {
            foreach (Thing thing in cell.GetThingList(map))
            {
                if (thing is not Building building || building.Destroyed || !building.Spawned) continue;
                CompPowerTrader? power = building.TryGetComp<CompPowerTrader>();
                // PowerOn already true means the native pass reached it; !WantsToBeOn means the player
                // switched it off on purpose, and switching it back on here would override that.
                if (power == null || power.PowerOn || power.PowerNet == null) continue;
                if (!FlickUtility.WantsToBeOn(building) || building.IsBrokenDown()) continue;
                power.PowerOn = true;
                if (power.PowerOn) restored++;
            }
        }

        if (restored > 0)
        {
            Restored += restored;
            Log.Message("[RimLayers] Re-powered " + restored + " part(s) on the secondary decks of a "
                + owner.Layout.LayerCount + "-layer SOS2 ship on " + map + ".");
        }
    }
}
