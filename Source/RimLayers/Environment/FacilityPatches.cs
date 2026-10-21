using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.CanPotentiallyLinkTo_Static),new[]{
    typeof(ThingDef),typeof(IntVec3),typeof(Rot4),typeof(ThingDef),typeof(IntVec3),typeof(Rot4),typeof(Map)})]
internal static class LayerFacilityScopePatch
{
    static bool Prefix(ThingDef facilityDef,IntVec3 facilityPos,ThingDef myDef,IntVec3 myPos,Map myMap,ref bool __result)
    {
        if(Compatibility.VanillaGravshipCompatibility.EngineDef(myDef)&&facilityDef.GetCompProperties<CompProperties_GravshipFacility>() is {} props&&LayerMapComponent.For(myMap) is {} owner)
        {float distance=owner.Ships.Distance(facilityPos,myPos);__result=distance<=props.maxDistance&&(props.minDistance<=0||distance>=props.minDistance);return false;}
        if(PowerLinks.SameLayer(myMap,facilityPos,myPos)) return true;
        __result=false;return false;
    }
}
[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.IsFacilityActive))]
internal static class LayerFacilityActivePatch
{
    static void Postfix(CompAffectedByFacilities __instance,Thing facility,ref bool __result)
    {
        var map=__instance.parent.Map;
        if(__instance.parent is Building_GravEngine && facility.TryGetComp<CompGravshipFacility>()!=null)return;
        if(__result && LayerMapComponent.For(map)!=null && (facility.Map!=map ||
            !PowerLinks.SameLayer(map,__instance.parent.Position,facility.Position) ||
            !PowerLinks.HasSurface(__instance.parent) || !PowerLinks.HasSurface(facility))) __result=false;
    }
}
