using HarmonyLib;
using RimLayers.Core;
using RimLayers.Ships;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.CanPotentiallyLinkTo_Static),new[]{
    typeof(ThingDef),typeof(IntVec3),typeof(Rot4),typeof(ThingDef),typeof(IntVec3),typeof(Rot4),typeof(Map)})]
internal static class LayerFacilityScopePatch
{
    static bool Prefix(ThingDef facilityDef,IntVec3 facilityPos,ThingDef myDef,IntVec3 myPos,Map myMap,ref bool __result)
    {
        // Engine links are widened in the Postfix below; let native answer first.
        if(Compatibility.VanillaGravshipCompatibility.EngineDef(myDef))return true;
        // Everything else keeps the native same-layer rule: a workbench on one floor must not feed
        // off a tool cabinet on another floor.
        if(PowerLinks.SameLayer(myMap,facilityPos,myPos)) return true;
        __result=false;return false;
    }

    /// <summary>
    /// Widens engine facility links across decks, and only widens.
    ///
    /// Native measures GenThing.TrueCenter on storage coordinates, where a deck of separation is
    /// exactly Stride cells (z = layer*Stride + z). Stride is 255 on a 250 map while VGE's pilot
    /// console allows maxDistance 200, so native can never accept a facility one deck above its
    /// engine. Storage distance is always >= the decoded logical distance, so the two cases are
    /// exact: a maxDistance rejection can be legitimately widened, and a minDistance rejection can
    /// never be. That is why this is a Postfix - the earlier Prefix replaced the native verdict and
    /// silently dropped any native reason to accept.
    /// </summary>
    static void Postfix(ThingDef facilityDef,IntVec3 facilityPos,ThingDef myDef,IntVec3 myPos,Map myMap,ref bool __result)
    {
        if(__result)return;
        if(!Compatibility.VanillaGravshipCompatibility.EngineDef(myDef))return;
        if(LayerMapComponent.For(myMap) is not {} owner)return;
        if(facilityDef.GetCompProperties<CompProperties_Facility>() is not {} props)return;
        // Membership is ShipFacilities.LinksToDef, not props.linkableBuildings.Contains(myDef): a vanilla
        // PilotConsole standing on a VGE hull declares only the vanilla GravEngine def, and VGE never adds
        // vanilla PilotConsole to its own engines' linkableFacilities. An engine-list-only gate therefore
        // drops exactly the pair the user builds (live log: allowed=False canLink=True belongs=True).
        if(!ShipFacilities.LinksToDef(props,myDef))return;
        // Bed-head/adjacency variants are inherently same-deck; keep their native same-layer rule.
        if(props.mustBePlacedAdjacent||props.mustBePlacedAdjacentCardinalToBedHead||
            props.mustBePlacedAdjacentCardinalToAndFacingBedHead||props.mustBePlacedFacingThingLinear)return;
        float distance=owner.Ships.Distance(facilityPos,myPos);
        if(distance<=props.maxDistance&&(props.minDistance<=0||distance>=props.minDistance))__result=true;
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
            facility.TryGetComp<CompFacility>() is {} f&&ShipFacilities.LinksTo(f.Props,engine))return;
        if(__result && LayerMapComponent.For(map)!=null && (facility.Map!=map ||
            !PowerLinks.SameLayer(map,__instance.parent.Position,facility.Position) ||
            !PowerLinks.HasSurface(__instance.parent) || !PowerLinks.HasSurface(facility))) __result=false;
    }
}
