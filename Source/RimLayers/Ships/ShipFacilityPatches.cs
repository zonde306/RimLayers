using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Ships;

[HarmonyPatch(typeof(CompGravshipFacility),nameof(CompGravshipFacility.CanBeActive),MethodType.Getter)]
internal static class ShipFacilityActive
{
    static bool Prefix(CompGravshipFacility __instance,ref bool __result)
    {
        var c=__instance;var owner=LayerMapComponent.For(c.parent.Map);if(owner==null)return true;
        var engine=c.engine;
        __result=engine!=null&&ShipStructureService.Core(engine)&&
            (c.parent.TryGetComp<CompPowerTrader>() is not {} p||p.PowerOn)&&
            owner.Ships.Distance(c.parent.Position,engine.Position)<=c.Props.maxDistance&&owner.Ships.CanConnect(engine,c.parent,c.Props.onlyRequiresLooseConnection);
        return false;
    }
}
[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.PotentialThingsToLinkTo))]
internal static class ShipFacilityCandidates
{
    static bool Prefix(ThingDef myDef,IntVec3 myPos,Map myMap,ref IEnumerable<Thing> __result)
    {
        if(!Compatibility.VanillaGravshipCompatibility.EngineDef(myDef)||LayerMapComponent.For(myMap) is not {} owner)return true;
        var engine=myPos.GetThingList(myMap).OfType<Building_GravEngine>().FirstOrDefault();if(engine==null)return true;
        var allowed=myDef.GetCompProperties<CompProperties_AffectedByFacilities>().linkableFacilities;
        __result=myMap.listerThings.AllThings.Where(t=>allowed.Contains(t.def)&&t.TryGetComp<CompGravshipFacility>() is {} c&&
                c.CanLink()&&owner.Ships.Distance(myPos,t.Position)<=c.Props.maxDistance&&owner.Ships.Belongs(engine,t))
            .OrderBy(t=>owner.Ships.Distance(myPos,t.Position)).ThenBy(t=>t.thingIDNumber)
            .GroupBy(t=>t.def).SelectMany(g=>g.Take(g.First().TryGetComp<CompGravshipFacility>().Props.maxSimultaneous)).ToArray();return false;
    }
}
[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.LooselyConnectedToGravEngine))]
internal static class ShipLooseConnection
{
    static bool Prefix(Building_GravEngine __instance,Thing thing,ref bool __result)
    {if(LayerMapComponent.For(__instance.Map) is not {} o)return true;__result=o.Ships.Belongs(__instance,thing);return false;}
}
[HarmonyPatch(typeof(PlaceWorker_InRangeOfGravEngine),nameof(PlaceWorker_InRangeOfGravEngine.AllowsPlacing))]
internal static class ShipFacilityPlacement
{
    static bool Prefix(BuildableDef checkingDef,IntVec3 loc,Map map,ref AcceptanceReport __result)
    {
        if(LayerMapComponent.For(map) is not {} o||checkingDef is not ThingDef d)return true;
        var e=o.Ships.BuildOwner(loc);var p=d.GetCompProperties<CompProperties_GravshipFacility>();
        __result=e!=null&&p!=null&&o.Ships.Distance(e.Position,loc)<=p.maxDistance?true:"MessageMustBePlacedInRangeOfGravEngine".Translate();return false;
    }
}
[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.InsideFootprint))]
internal static class ShipLogicalFootprint
{
    static bool Prefix(IntVec3 loc,Map map,Thing thingToExclude,ref bool __result)
    {
        if(LayerMapComponent.For(map) is not {} o)return true;
        var e=o.Ships.BuildOwner(loc);
        if(e==null)return true;
        if(thingToExclude!=null)return true; // Native exclusion previews retain native policy.
        __result=o.Ships.InField(e,loc);return false;
    }
}
[HarmonyPatch(typeof(Building),nameof(Building.SpawnSetup))]
internal static class ShipBuildingSpawnDirty{static void Postfix(Building __instance){var o=LayerMapComponent.For(__instance.Map);if(__instance is Building_GravEngine||__instance.TryGetComp<CompGravshipFacility>()!=null)o?.Ships.MarkDirty();if(o!=null&&__instance is Building_GravEngine)RimLayers.Presentation.RoofSurfaces.Clear(o);}}
[HarmonyPatch(typeof(Building),nameof(Building.DeSpawn))]
internal static class ShipBuildingDespawnDirty{static void Prefix(Building __instance){var o=LayerMapComponent.For(__instance.Map);if(__instance is Building_GravEngine||__instance.TryGetComp<CompGravshipFacility>()!=null)o?.Ships.MarkDirty();if(o!=null&&__instance is Building_GravEngine)RimLayers.Presentation.RoofSurfaces.Clear(o);}}
[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.SetFoundation))]
internal static class ShipFoundationChange
{
    static void Postfix(IntVec3 c,TerrainDef newTerr,Map ___map)
    {
        var o=LayerMapComponent.For(___map);if(o==null)return;
        if(newTerr.IsSubstructure&&o.TryDecode(c,out var l)&&l.LayerId>0)o.StructuralCells.Add(c);
        if(newTerr.IsSubstructure&&o.Ships.ClaimAt(c)==0&&o.Ships.BuildOwner(c) is {} buildOwner)o.Ships.Assign(c,buildOwner.thingIDNumber);
        o.map.pathing.RecalculatePerceivedPathCostAt(c);
        o.Structure.RoofChanged(c);o.Invalidate(c,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Light);
    }
}
[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.GetInspectString))]
internal static class ShipCapacityInspect
{
    static void Postfix(Building_GravEngine __instance,ref string __result)
    {if(LayerMapComponent.For(__instance.Map)?.Ships.Get(__instance) is {} s)__result+="\n"+"RL_ShipCapacity".Translate(Compatibility.VanillaGravshipCompatibility.SupportCost(__instance.Map,s.Cells),(int)__instance.GetStatValue(StatDefOf.SubstructureSupport));}
}

[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.CanLinkTo))]
internal static class ShipFacilityLinkOwnership
{
    static bool Prefix(CompAffectedByFacilities __instance,Thing facility,ref bool __result)
    {
        if(__instance.parent is not Building_GravEngine engine||facility.TryGetComp<CompGravshipFacility>()==null||LayerMapComponent.For(engine.Map) is not {} o)return true;
        if(o.Ships.Belongs(engine,facility))return true;
        __result=false;return false;
    }
}

[HarmonyPatch(typeof(Thing),nameof(Thing.SetFaction))]
internal static class ShipOwnershipFactionChange
{
    static void Postfix(Thing __instance)
    {if(__instance is Building_GravEngine||__instance.TryGetComp<CompGravshipFacility>()!=null)LayerMapComponent.For(__instance.Map)?.Ships.MarkDirty();}
}
