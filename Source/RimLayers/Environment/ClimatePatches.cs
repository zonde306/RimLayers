using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(RoomTempTracker),"DeepEqualizationTempChangePerInterval")]
internal static class RockHeatPatch
{
    static bool Prefix(Room ___room,float ___thickRoofCoverage,ref float __result)
    {
        var owner=LayerMapComponent.For(___room.Map);
        if(owner?.Environment.Enabled!=true)return true;
        if(!owner.TryDecode(___room.Cells.FirstOrDefault(),out var c))return true;
        __result=(owner.Environment.Rock(c.LayerId)-___room.Temperature)*___thickRoofCoverage*.00005f*120;
        return false;
    }
}
[HarmonyPatch(typeof(RoomTempTracker),"WallEqualizationTempChangePerInterval")]
internal static class RockWallHeatPatch
{
    internal static bool AirTemperature(IntVec3 cell,Map map,out float temperature)
    {
        var owner=LayerMapComponent.For(map);
        if(owner?.Environment.Enabled==true&&owner.TryDecode(cell,out var c))
        {
            if(cell.GetEdifice(map)?.def.building?.isNaturalRock==true)
            {temperature=owner.Environment.Rock(c.LayerId);return true;}
            if(GenTemperature.TryGetDirectAirTemperatureForCell(cell,map,out temperature))return true;
            temperature=owner.Elevation(c.LayerId)<0?owner.Environment.Rock(c.LayerId):owner.Environment.Outdoor(cell);
            return true;
        }
        return GenTemperature.TryGetDirectAirTemperatureForCell(cell,map,out temperature);
    }
    static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)=>
        SkyQueries.Replace(instructions,AccessTools.Method(typeof(GenTemperature),nameof(GenTemperature.TryGetDirectAirTemperatureForCell)),
            AccessTools.Method(typeof(RockWallHeatPatch),nameof(AirTemperature)),1);
}
[HarmonyPatch(typeof(RoomTempTracker),nameof(RoomTempTracker.EqualizeTemperature))]
internal static class AltitudeRoomHeatPatch
{
    static void Postfix(Room ___room)
    {
        var owner=LayerMapComponent.For(___room.Map);
        if(owner?.Environment.Enabled==true&&___room.UsesOutdoorTemperature)___room.Temperature=owner.Environment.LocalOutside(___room);
    }
}
[HarmonyPatch(typeof(RimWorld.Building_Door),"DoorOpen")]
internal static class AtmosphereDoorOpen
{
    static void Prefix(RimWorld.Building_Door __instance,out bool __state)=>__state=__instance.Open;
    static void Postfix(RimWorld.Building_Door __instance,bool __state)
    {if(__instance.Open!=__state){var o=LayerMapComponent.For(__instance.Map);o?.Environment.Invalidate();o?.Heat.Invalidate();}}
}
[HarmonyPatch(typeof(RimWorld.Building_Door),"DoorTryClose")]
internal static class AtmosphereDoorClose
{
    static void Prefix(RimWorld.Building_Door __instance,out bool __state)=>__state=__instance.Open;
    static void Postfix(RimWorld.Building_Door __instance,bool __state)
    {if(__instance.Open!=__state){var o=LayerMapComponent.For(__instance.Map);o?.Environment.Invalidate();o?.Heat.Invalidate();}}
}
