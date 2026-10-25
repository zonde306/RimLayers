using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Ships;

/// <summary>
/// Shared "is this thing a facility of that engine" test. Native CompAffectedByFacilities filters on
/// CompProperties_GravshipFacility, but VGE also ships engine facilities declared with the plain
/// CompProperties_Facility (for example VGE_GravshipStructuralBeam), and VEF copies link lists in
/// LongEventHandler.ExecuteWhenFinished. So the linkableBuildings list is queried at call time
/// instead of being matched against a fixed component type.
/// </summary>
internal static class ShipFacilities
{
    internal static CompProperties_Facility? Props(Thing thing)=>thing.TryGetComp<CompFacility>()?.Props;
    internal static bool IsEngineFacility(Building_GravEngine engine,Thing thing)=>
        thing.TryGetComp<CompFacility>() is {} facility&&facility.CanLink()&&
        facility.Props?.linkableBuildings is {} linkable&&linkable.Contains(engine.def);
    internal static int MaxSimultaneous(Thing thing)=>Props(thing)?.maxSimultaneous??1;
}

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

/// <summary>
/// Native CompGravshipFacility.CompInspectStringExtra decides "in range of a grav engine" with
/// IntVec3.InHorDistOf, which only reads x/z. Layered storage keeps one band per layer, so the same
/// ship's decks are dozens of cells apart and a cross-layer facility reports
/// MessageMustBePlacedInRangeOfGravEngine even while its link is live. Only that verdict is replaced;
/// the link/active line stays native so a genuine linking failure is still visible.
/// </summary>
[HarmonyPatch(typeof(CompGravshipFacility),nameof(CompGravshipFacility.CompInspectStringExtra))]
internal static class ShipFacilityInspectRange
{
    static void Postfix(CompGravshipFacility __instance,ref string __result)
    {
        var parent=__instance.parent;
        if(!parent.Spawned||LayerMapComponent.For(parent.Map) is not {} owner)return;
        float best=float.PositiveInfinity;
        if(__instance.engine is {} linked)best=owner.Ships.Distance(parent.Position,linked.Position);
        else foreach(var engine in Compatibility.VanillaGravshipCompatibility.Engines(owner.map))
            if(ShipStructureService.Core(engine))best=MathF.Min(best,owner.Ships.Distance(parent.Position,engine.Position));
        string range="MessageMustBePlacedInRangeOfGravEngine".Translate().ToString();
        string text=string.Join("\n",__result.Split('\n').Where(line=>!line.Contains(range))).TrimEnd('\n');
        if(best>__instance.Props.maxDistance)
            text=(text.Length==0?text:text+"\n")+range.Colorize(ColorLibrary.RedReadable);
        __result=text;
    }
}

[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.PotentialThingsToLinkTo))]
internal static class ShipFacilityCandidates
{
    static bool Prefix(ThingDef myDef,IntVec3 myPos,Map myMap,ref IEnumerable<Thing> __result)
    {
        if(!Compatibility.VanillaGravshipCompatibility.EngineDef(myDef)||LayerMapComponent.For(myMap) is not {} owner)return true;
        var engine=myPos.GetThingList(myMap).OfType<Building_GravEngine>().FirstOrDefault();if(engine==null)return true;
        var allowed=myDef.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities;
        if(allowed==null)return true;
        __result=myMap.listerThings.AllThings.Where(t=>allowed.Contains(t.def)&&ShipFacilities.IsEngineFacility(engine,t)&&
                owner.Ships.Distance(myPos,t.Position)<=ShipFacilities.Props(t)!.maxDistance&&owner.Ships.Belongs(engine,t))
            .OrderBy(t=>owner.Ships.Distance(myPos,t.Position)).ThenBy(t=>t.thingIDNumber)
            .GroupBy(t=>t.def).SelectMany(g=>g.Take(ShipFacilities.MaxSimultaneous(g.First()))).ToArray();return false;
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
        var e=o.Ships.BuildOwner(loc);var p=d.GetCompProperties<CompProperties_Facility>();
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

/// <summary>
/// Native ValidSubstructure is a single-deck 2D flood around the engine. VGE consumers read it
/// directly (CompHeatManager room detection, GravheatUtility absorber protection,
/// SectionLayer_GravshipMask hull mask, DestroyTreesAroundSubstructure), so a layered ship loses
/// every upper deck there. Returning the owned cell set keeps one source of truth.
///
/// GravshipUtility.GenerateGravship uses the same property as its capture source and later feeds the
/// array into RemoveGravshipTerrainUnsafe, which is a flat single-deck operation; widening it there
/// corrupts upper decks. RimLayers performs its own multi-deck capture (ShipLayers.Prepare), so the
/// native capture keeps the native single-deck value.
/// </summary>
[HarmonyPatch]
internal static class ShipEngineDeckView
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructure));
        yield return AccessTools.PropertyGetter(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructureNoRegen));
    }
    static void Postfix(Building_GravEngine __instance,ref HashSet<IntVec3> __result)
    {
        // Native capture path owns the deck set here; RimLayers supplies the multi-deck payload itself.
        if(GravshipUtility.generatingGravship||ShipLayers.Capture!=null)return;
        // Only VGE reads ValidSubstructure for hull-wide queries (heat manager, gravheat absorbers,
        // hull mask, tree cleanup). Without VGE the native single-deck value is the documented
        // contract that native multi-ship scenarios assert on, so leave it untouched.
        if(!Compatibility.VanillaGravshipCompatibility.Active)return;
        if(ShipDeckTerrain.DeckCells(__instance) is {} cells)__result=cells;
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
        if(__instance.parent is not Building_GravEngine engine||LayerMapComponent.For(engine.Map) is not {} o)return true;
        if(ShipFacilities.Props(facility)==null)return true;
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
