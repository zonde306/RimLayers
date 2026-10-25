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
        // Engines accept any facility whose linkableBuildings contains the engine def, not only the
        // Gravship flavour: VGE declares VGE_GravshipStructuralBeam with a plain
        // CompProperties_Facility, and VEF copies link lists after defs load.
        if(Compatibility.VanillaGravshipCompatibility.EngineDef(myDef)&&LayerMapComponent.For(myMap) is {} owner&&
            facilityDef.GetCompProperties<CompProperties_Facility>() is {} props&&
            props.linkableBuildings is {} linkable&&linkable.Contains(myDef))
        {
            // Bed-head/adjacency variants are inherently same-deck; keep their native same-layer rule.
            if(props.mustBePlacedAdjacent||props.mustBePlacedAdjacentCardinalToBedHead||
                props.mustBePlacedAdjacentCardinalToAndFacingBedHead||props.mustBePlacedFacingThingLinear)return true;
            float distance=owner.Ships.Distance(facilityPos,myPos);
            __result=distance<=props.maxDistance&&(props.minDistance<=0||distance>=props.minDistance);
            return false;
        }
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
        // CompGravshipFacility has its own cross-deck verdict (Ships/ShipFacilityPatches.cs).
        if(facility.TryGetComp<CompGravshipFacility>()!=null)return;
        // Plain-facility engine links (for example VGE_GravshipStructuralBeam) also span decks.
        if(__instance.parent is Building_GravEngine engine&&
            facility.TryGetComp<CompFacility>() is {} f&&f.Props?.linkableBuildings?.Contains(engine.def)==true)return;
        if(__result && LayerMapComponent.For(map)!=null && (facility.Map!=map ||
            !PowerLinks.SameLayer(map,__instance.parent.Position,facility.Position) ||
            !PowerLinks.HasSurface(__instance.parent) || !PowerLinks.HasSurface(facility))) __result=false;
    }
}
