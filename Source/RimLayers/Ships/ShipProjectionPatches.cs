using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Ships;

[HarmonyPatch]
internal static class LayerShipTerrainProjection
{
    static IEnumerable<MethodBase> TargetMethods()=>new[]{nameof(Gravship.Terrains),nameof(Gravship.Foundations)}.Select(n=>AccessTools.PropertyGetter(typeof(Gravship),n));
    static void Postfix(Gravship __instance,ref Dictionary<IntVec3,TerrainDef> __result)
    {if(ShipLayers.TryData(__instance,out _))__result=ShipLayers.Project(__instance,__result.Where(p=>p.Value!=null).ToDictionary(p=>p.Key,p=>ShipLayers.FoundationForDeck(p.Value,p.Key.y)));}
}
[HarmonyPatch]
internal static class LayerShipThingProjection
{
    static IEnumerable<MethodBase> TargetMethods()=>new[]{nameof(Gravship.ThingPlacements),nameof(Gravship.ThrusterPlacements),nameof(Gravship.ExteriorDoorPlacements)}.Select(n=>AccessTools.PropertyGetter(typeof(Gravship),n));
    static void Postfix(Gravship __instance,ref Dictionary<Thing,PositionData.Data> __result)=>__result=ShipLayers.Project(__instance,__result);
}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.PawnPlacements),MethodType.Getter)]
internal static class LayerShipPawnProjection
{static void Postfix(Gravship __instance,ref Dictionary<Pawn,PositionData.Data> __result)=>__result=ShipLayers.Project(__instance,__result);}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.Roofs),MethodType.Getter)]
internal static class LayerShipRoofProjection
{
    static void Postfix(Gravship __instance,ref IEnumerable<(IntVec3,RoofDef)> __result)
    {
        if(ShipLayers.TryData(__instance,out var data)&&data.supportRoofs.Count>0)
        {
            var roofs=__result.ToDictionary(p=>p.Item1,p=>p.Item2);
            foreach(var support in data.supportRoofs)roofs.TryAdd(PrefabUtility.GetAdjustedLocalPosition(support.local,__instance.Rotation),support.roof);
            __result=roofs.Select(p=>(p.Key,p.Value)).ToList();
        }
        __result=ShipLayers.Project(__instance,__result);
    }
}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.TerrainColors),MethodType.Getter)]
internal static class LayerShipColorProjection
{static void Postfix(Gravship __instance,ref IEnumerable<(IntVec3,ColorDef)> __result)=>__result=ShipLayers.Project(__instance,__result);}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.TerrainDesignations),MethodType.Getter)]
internal static class LayerShipDesignationProjection
{static void Postfix(Gravship __instance,ref IEnumerable<(IntVec3,Designation)> __result)=>__result=ShipLayers.Project(__instance,__result);}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.Gases),MethodType.Getter)]
internal static class LayerShipGasProjection
{static void Postfix(Gravship __instance,ref IEnumerable<(IntVec3,uint)> __result)=>__result=ShipLayers.Project(__instance,__result);}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.GravFieldExtenderPositions),MethodType.Getter)]
internal static class LayerShipExtenderProjection
{
    static void Postfix(Gravship __instance,ref IEnumerable<IntVec3> __result)
    {if(ShipLayers.TryData(__instance,out _)){bool p=ShipLayers.IsPlacing(__instance);__result=__result.Where(c=>p||c.y==0).Select(c=>p?ShipLayers.DecodeOffset(c):c).ToList();}}
}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.RoomTemperatures),MethodType.Getter)]
internal static class LayerShipTemperatureProjection
{
    static void Postfix(Gravship __instance,ref IEnumerable<RoomTemperatureVacuum.Data> __result)
    {
        if(!ShipLayers.TryData(__instance,out _))return;
        bool p=ShipLayers.IsPlacing(__instance);
        __result=__result.Where(c=>p||c.local.y==0).Select(c=>{if(p)c.local=ShipLayers.DecodeOffset(c.local);return c;}).ToList();
    }
}
[HarmonyPatch(typeof(MoveableArea),nameof(MoveableArea.RelativeCells),MethodType.Getter)]
internal static class LayerShipAreaProjection
{
    static void Postfix(Gravship ___gravship,ref IEnumerable<IntVec3> __result)
    {if(___gravship!=null&&ShipLayers.TryData(___gravship,out _)){bool p=ShipLayers.IsPlacing(___gravship);__result=__result.Where(c=>p||c.y==0).Select(c=>p?ShipLayers.DecodeOffset(c):c).ToList();}}
}
