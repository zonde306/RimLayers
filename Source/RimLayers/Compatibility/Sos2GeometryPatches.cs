using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Rewrites the three SOS2 places that read <c>Map.Size</c> as if it were the logical rectangle.
/// On a layered map <c>Map.Size.z</c> spans every band plus the padding, so the native versions
/// relocate ships and shots into the storage gap.
/// </summary>
internal static class Sos2GeometryPatches
{
    private static readonly FieldInfo? ShipArea =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "Area");

    /// <summary>
    /// <c>Building_ShipTurret.MapEdgeCell</c> aims every shot at the map's storage edge. Replaced with
    /// the band-local edge of the turret's own deck.
    /// </summary>
    [HarmonyPatch]
    private static class TurretEdgeCellPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.Building_ShipTurret"), "MapEdgeCell", new[] { typeof(int) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance, int miss, ref LocalTargetInfo __result)
        {
            if (__instance is not Building turret) return true;
            Map map = turret.Map;
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            IntVec3 pos = turret.Position;
            if (miss > 0) miss = Rand.RangeInclusive(-miss, miss);

            // SOS2 fires along the engine axis, reversed while retreating.
            int engineRot = EngineRotation(__instance);
            int heading = Heading(__instance);
            int side = (engineRot, heading) switch
            {
                (0, not -1) => 0,
                (2, -1) => 0,
                (1, not -1) => 1,
                (3, -1) => 1,
                (2, not -1) => 2,
                (0, -1) => 2,
                _ => 3,
            };
            (int x, int y, int z) = Sos2Geometry.EdgeCell(
                pos.x, 0, pos.z, owner.Layout!.Width, height, stride, layers, miss, side);
            __result = new LocalTargetInfo(new IntVec3(x, y, z));
            return false;
        }

        private static int EngineRotation(object turret)
        {
            object? comp = AccessTools.Field(turret.GetType(), "mapComp")?.GetValue(turret);
            return comp == null ? 0 : AccessTools.Field(comp.GetType(), "EngineRot")?.GetValue(comp) as int? ?? 0;
        }

        private static int Heading(object turret)
        {
            object? comp = AccessTools.Field(turret.GetType(), "mapComp")?.GetValue(turret);
            return comp == null ? 0 : AccessTools.Field(comp.GetType(), "Heading")?.GetValue(comp) as int? ?? 0;
        }
    }

    /// <summary>
    /// <c>ShipInteriorMod2.TransformForShipMove</c> mirrors a rotated ship against <c>Map.Size</c>.
    /// Replaced with a band-preserving mirror so a multi-deck hull keeps its decks.
    /// </summary>
    [HarmonyPatch]
    private static class TransformForShipMovePatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipInteriorType, "TransformForShipMove",
                new[] { typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(int) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(Map targetMap, IntVec3 pos, IntVec3 adjustment, int rotb, ref IntVec3 __result)
        {
            if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            (int x, int y, int z) = Sos2Geometry.Mirror(
                pos.x, pos.y, pos.z, owner.Layout!.Width, height, stride, layers, rotb);
            __result = new IntVec3(x, y, z) + adjustment;
            return false;
        }
    }

    /// <summary>
    /// <c>SpaceShipCache.CenterShipOnMap</c> centres against <c>Map.Size.z</c>, which on a layered map
    /// parks the whole hull in the middle band. Replaced with a centre inside the ground band.
    /// </summary>
    [HarmonyPatch]
    private static class CenterShipOnMapPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "CenterShipOnMap");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance, ref IntVec3 __result)
        {
            if (ShipArea?.GetValue(__instance) is not HashSet<IntVec3> { Count: > 0 } area) return true;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (IntVec3 v in area)
            {
                if (v.x < minX) minX = v.x;
                if (v.x > maxX) maxX = v.x;
                if (v.z < minZ) minZ = v.z;
                if (v.z > maxZ) maxZ = v.z;
            }
            Map map = (AccessTools.Field(__instance.GetType(), "Map")?.GetValue(__instance) as Map) ?? Find.CurrentMap;
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            (int x, int y, int z) = Sos2Geometry.CenterOnMap(
                owner.Layout!.Width, map.Size.z, height, stride, layers,
                maxX - minX, maxZ - minZ, minX, minZ);
            __result = new IntVec3(x, y, z);
            return false;
        }
    }
}
