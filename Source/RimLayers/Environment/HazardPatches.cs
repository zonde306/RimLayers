using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(FireUtility),nameof(FireUtility.ChanceToStartFireIn))]
internal static class FireSurfaceBoundaryPatch
{
    static bool Prefix(IntVec3 c,Map map,ref float __result)
    {
        if(LayerMapComponent.For(map) is not { } owner || owner.Structure.HasSurface(c))return true;
        __result=0;return false;
    }
}
[HarmonyPatch(typeof(Fire),"TrySpread")]
internal static class VerticalFireSpreadPatch
{
    static void Postfix(Fire __instance)=>LayerMapComponent.For(__instance.Map)?.Fire.Spread(__instance);
}
[HarmonyPatch(typeof(GenClamor),nameof(GenClamor.DoClamor),new[]{typeof(Thing),typeof(IntVec3),typeof(float),typeof(GenClamor.ClamorEffect)})]
internal static class VerticalClamorPatch
{
    static void Postfix(Thing source,IntVec3 position,float radius,GenClamor.ClamorEffect clamorEffect)=>
        LayerMapComponent.For(source.MapHeld)?.Sound.NotifyOtherFloors(source,position,radius,clamorEffect);
}
