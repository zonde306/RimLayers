using HarmonyLib;
using RimLayers.Core;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(RegionTypeUtility),nameof(RegionTypeUtility.GetExpectedRegionType))]
internal static class IsolationRegionPatch
{
    static bool Prefix(IntVec3 c,Map map,ref RegionType __result)
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null || owner.TryDecode(c,out _)) return true;
        __result=RegionType.None;return false;
    }
}

[HarmonyPatch(typeof(Room),nameof(Room.OpenRoofCount),MethodType.Getter)]
internal static class LogicalRoomSkyPatch
{
    static bool Prefix(Room __instance,ref int __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null) return true;
        __result=owner.Sky.OpenRoofCount(__instance);return false;
    }
}

[HarmonyPatch(typeof(Room),nameof(Room.TouchesMapEdge),MethodType.Getter)]
internal static class LogicalRoomEdgePatch
{
    static bool Prefix(Room __instance,ref bool __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null) return true;
        __result=owner.Sky.TouchesEdge(__instance);return false;
    }
}

[HarmonyPatch(typeof(Room),nameof(Room.Notify_RoomShapeChanged))]
internal static class RoomSkyShapePatch
{
    static void Prefix(Room __instance)
    {var owner=LayerMapComponent.For(__instance.Map);owner?.Sky.RoomChanged(__instance);owner?.Environment.Invalidate();owner?.Heat.Invalidate();}
}

[HarmonyPatch(typeof(RoomTempTracker),"RegenerateEqualizeCells")]
internal static class HeatWallBoundaryPatch
{
    static void Postfix(Room ___room,List<IntVec3> ___equalizeCells)
    {
        var owner=LayerMapComponent.For(___room.Map);
        if(owner==null) return;
        IntVec3 first=___room.Cells.FirstOrDefault();
        if(!owner.TryDecode(first,out var local)) { ___equalizeCells.Clear();return; }
        ___equalizeCells.RemoveAll(c=>!owner.TryDecode(c,out var other) || other.LayerId!=local.LayerId);
    }
}

[HarmonyPatch(typeof(RoomTempTracker),"ThinRoofEqualizationTempChangePerInterval")]
internal static class HeatRoofBoundaryPatch
{
    static bool Prefix(Room ___room,ref float __result)=>HeatCeiling.Calculate(___room,true,ref __result);
}
[HarmonyPatch(typeof(RoomTempTracker),"NoRoofEqualizationTempChangePerInterval")]
internal static class HeatOpenBoundaryPatch
{
    static bool Prefix(Room ___room,ref float __result)=>HeatCeiling.Calculate(___room,false,ref __result);
}
internal static class HeatCeiling
{
    public static bool Calculate(Room room,bool thin,ref float result)
    {
        var owner=LayerMapComponent.For(room.Map);
        if(owner==null) return true;
        float outside=owner.Environment.Enabled?owner.Environment.LocalOutside(room):room.Map.mapTemperature.OutdoorTemp;
        result=HeatExchange.OutdoorChange(room.Temperature,outside,owner.Heat.OutdoorCoverage(room,thin),thin);
        if(owner.Environment.Enabled&&room.Cells.FirstOrDefault() is var at)result*=owner.Environment.WindFactor(at);
        return false;
    }
}

[HarmonyPatch(typeof(MapTemperature),nameof(MapTemperature.MapTemperatureTick))]
internal static class VerticalHeatTickPatch
{
    static void Postfix(Map ___map)
    {
        var owner=LayerMapComponent.For(___map);
        if(owner!=null && (Find.TickManager.TicksGame%120==7 || DebugSettings.fastEcology)) owner.Heat.Exchange();
    }
}
