using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Re-attaches the deck above a hull every time SOS2 rebuilds its ship caches.
/// </summary>
/// <remarks>
/// SOS2 serialises nothing about <c>MapShipCells</c>: it re-derives the whole table from the bridges
/// and their hull parts. That derivation only knows about buildings, so after every rebuild the upper
/// decks are unclaimed again and anything standing on them loses its ship - the load-time rebuild is
/// what turns a working upper deck into the <c>ShipsOnMap[-1]</c> crash on inspection.
/// <para>
/// All three rebuild entry points are hooked, not just <c>FinalizeInit</c>: ship movement and the
/// scenario loader call <c>RecacheMap</c> directly, and a deck that silently stops being claimed after
/// a launch would look exactly like the bug this class exists to prevent.
/// </para>
/// </remarks>
internal static class Sos2DeckRegistryPatches
{
    [HarmonyPatch]
    private static class RecacheFromRootsPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipMapCompType, "RecacheFromRoots");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPostfix]
        private static void Postfix(MapComponent __instance) => Sos2DeckRegistry.SyncAll(__instance?.map);
    }

    [HarmonyPatch]
    private static class RecacheMapPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipMapCompType, "RecacheMap");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPostfix]
        private static void Postfix(MapComponent __instance) => Sos2DeckRegistry.SyncAll(__instance?.map);
    }
}
