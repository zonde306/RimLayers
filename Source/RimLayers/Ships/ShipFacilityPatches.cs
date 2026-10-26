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
    /// <summary>
    /// The facility declares itself a facility of some gravship engine, even when the engine in hand
    /// declares a different list. CompProperties_Facility.linkableBuildings is resolved from every
    /// ThingDef whose CompProperties_AffectedByFacilities.linkableFacilities names the facility, so it
    /// is the facility side statement of which engines it belongs to. VGE replaces the vanilla engine
    /// with its own engine defs whose lists omit the vanilla PilotConsole, so a vanilla console on a
    /// VGE hull names GravEngine while standing on VGE_GravjumperEngine. Native enumeration is driven
    /// by the ENGINE list, so that console is never even a candidate; Player-prev.log recorded
    /// allowed=False canLink=True belongs=True distance=3.6/200 engineFacilities=0, i.e. membership was
    /// the only failing gate and the distance was already fine.
    /// </summary>
    internal static bool EngineFacilityDef(CompProperties_Facility? props)=>
        props?.linkableBuildings is {} linkable&&linkable.Any(Compatibility.VanillaGravshipCompatibility.EngineDef);
    /// <summary>Fail-open membership: the engine accepts the facility if either side list says so.</summary>
    internal static bool LinksToDef(CompProperties_Facility? props,ThingDef engineDef)=>
        props!=null&&(props.linkableBuildings?.Contains(engineDef)==true||EngineFacilityDef(props));
    internal static bool LinksTo(CompProperties_Facility? props,Building_GravEngine engine)=>LinksToDef(props,engine.def);
    internal static bool IsEngineFacility(Building_GravEngine engine,Thing thing)=>
        thing.TryGetComp<CompFacility>() is {} facility&&facility.CanLink()&&LinksTo(facility.Props,engine);
    internal static int MaxSimultaneous(Thing thing)=>Props(thing)?.maxSimultaneous??1;
}

/// <summary>
/// One-shot trace for a gravship facility that stands on a layered ship deck but has no live engine
/// link. The link list is what drives both CompGravshipFacility's NotConnectedToGravEngine line and
/// VGE's CompDisconnectedFacilityOverlay badge, so an empty list is the whole user-visible bug. The
/// gate that actually failed is logged once per thing; opening the inspect window again is silent.
/// </summary>
internal static class ShipFacilityTrace
{
    private static readonly HashSet<int> Reported=new();
    internal static void MissingLink(LayerMapComponent owner,CompGravshipFacility facility)
    {
        var parent=facility.parent;
        if(!owner.Managed||!parent.Spawned)return;
        if(owner.Ships.BuildOwner(parent.Position) is not {} engine)return;
        if(!ShipStructureService.Core(engine))return;
        if(!Reported.Add(parent.thingIDNumber))return;
        var ship=owner.Ships.Get(engine);
        var props=facility.Props;
        var allowed=engine.def.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities;
        // Thrusters have their own extra gates (deck substructure in the exhaust zone, and the
        // outdoors room test). Without them in the line a thruster failure reads identically to a
        // membership failure, which cost two rounds of guessing already.
        var thruster=facility as CompGravshipThruster;
        var outdoors=thruster==null?"-":(AccessTools.Field(typeof(CompGravshipThruster),"outdoors").GetValue(thruster) as bool?)
            ?.ToString().ToLowerInvariant()??"unset";
        Log.Warning("[RimLayers] Gravship facility has no engine link: "+parent.def.defName+
            " at "+parent.Position+" engine="+engine.def.defName+"@"+engine.Position+
            " ship="+(ship==null?"null":ship.Ambiguous?"ambiguous":"cells="+ship.Cells.Count)+
            " allowed="+(allowed?.Contains(parent.def)==true)+
            " linksTo="+ShipFacilities.LinksTo(props,engine)+
            " canLink="+(parent.TryGetComp<CompFacility>()?.CanLink().ToString()??"none")+
            " belongs="+owner.Ships.Belongs(engine,parent)+
            " distance="+owner.Ships.Distance(parent.Position,engine.Position).ToString("F1")+
            "/"+props.maxDistance.ToString("F0")+
            " loose="+props.onlyRequiresLooseConnection+
            " blocked="+(thruster?.Blocked.ToString().ToLowerInvariant()??"-")+
            " outdoors="+outdoors+
            " engineFacilities="+engine.AffectedByFacilities.LinkedFacilitiesListForReading.Count);
    }
}

/// <summary>
/// Native CompGravshipThruster.IsBlocked treats ANY substructure foundation inside the exhaust zone as
/// a blocker. A single-deck gravship hangs its thrusters off the hull rim, so that test never fires.
/// In a layered ship the upper deck IS substructure, so every thruster standing on a deck is
/// permanently blocked: CanLink() returns false, CompFacility.LinkToNearbyBuildings never links it,
/// and the facility reports ThrusterNotConnected / NotConnectedToGravEngine forever.
///
/// Substructure that belongs to the thruster's own ship is the deck it stands on, not an obstacle.
/// The zone is rescanned here so a neighbouring ship's deck still blocks; a wind-blocking building is
/// never excused and the outdoors requirement is untouched.
/// </summary>
[HarmonyPatch(typeof(CompGravshipThruster),nameof(CompGravshipThruster.IsBlocked))]
internal static class ShipThrusterDeckBlock
{
    private static List<IntVec3> Zone=new();
    static void Postfix(ThingDef thrusterDef,Map map,IntVec3 position,Rot4 rotation,ref bool blockedBySubstructure)
    {
        if(!blockedBySubstructure)return;
        if(LayerMapComponent.For(map) is not {} owner||!owner.Managed)return;
        var props=thrusterDef.GetCompProperties<CompProperties_GravshipThruster>();
        if(props==null)return;
        var own=owner.Ships.BuildOwner(position);
        if(own==null||!ShipStructureService.Core(own))return; // Not on a player deck: keep native.
        Zone.Clear();props.GetExclusionZone(position,rotation,ref Zone);
        // Fail open: only a deck we can positively attribute to a DIFFERENT engine keeps blocking.
        // A cold ship cache must never leave a thruster permanently blocked, because CanLink() then
        // returns false and the thruster reports ThrusterNotConnected forever.
        foreach(var c in Zone)
        {
            if(!c.InBounds(map)||map.terrainGrid.FoundationAt(c)?.IsSubstructure!=true)continue;
            if(owner.Ships.BuildOwner(c) is {} deck&&deck!=own)return;
        }
        blockedBySubstructure=false;
    }
}

/// <summary>
/// Widens CompGravshipFacility.CanBeActive across layers, and only widens.
///
/// The native getter is a 2D test: it bails out as soon as one engine fails IntVec3.InHorDistOf,
/// then requires OnValidSubstructure (a same-band flood). Replacing it outright was wrong: every
/// extra condition this patch added - a live link, a cached ship, the engine footprint radius -
/// could then turn a working native verdict into "not connected". A facility whose link is fine but
/// whose ship cache has not refreshed yet would report NotConnectedToGravEngine for no reason.
/// A Postfix that can only flip false to true keeps the native answer authoritative and adds the
/// cross-deck case on top of it.
///
/// The replacement test is the deck itself, never the engine footprint radius. InHorDistOf spans the
/// storage stride, so with maxDistance 200 an upper-deck facility is already out of range on a 200
/// map (stride 204, one deck apart) - that is the reported live failure. The engine radius is far
/// smaller than maxDistance (VGE_GravjumperEngine 7.9 against 200), so using InField here would leave
/// any facility more than a few cells from the engine reading "not connected" even though native
/// OnValidSubstructure never had that limit. Substructure underneath the whole footprint is what
/// native actually tests, and it needs no ship cache, so a cold cache cannot break it.
/// </summary>
[HarmonyPatch(typeof(CompGravshipFacility),nameof(CompGravshipFacility.CanBeActive),MethodType.Getter)]
internal static class ShipFacilityActive
{
    static void Postfix(CompGravshipFacility __instance,ref bool __result)
    {
        if(__result)return;
        var c=__instance;
        if(LayerMapComponent.For(c.parent.Map) is not {} owner)return;
        if((c.engine??owner.Ships.BuildOwner(c.parent.Position)) is not {} engine||!ShipStructureService.Core(engine))return;
        if(c.parent.TryGetComp<CompPowerTrader>() is {} p&&!p.PowerOn)return;
        if(owner.Ships.Distance(c.parent.Position,engine.Position)>c.Props.maxDistance)return;
        // Veto only a deck we can positively attribute to a DIFFERENT engine; an unknown owner is not
        // evidence against the link.
        var cellOwner=owner.Ships.OwnerAt(c.parent.Position)??owner.Ships.BuildOwner(c.parent.Position);
        if(cellOwner!=null&&cellOwner!=engine)return;
        // Native OnValidSubstructure floods the engine's own band; every deck of a layered ship is
        // substructure, so testing the foundation directly is the same rule without the stride.
        foreach(var cell in c.parent.OccupiedRect())
            if(c.parent.Map.terrainGrid.FoundationAt(cell)?.IsSubstructure!=true)return;
        __result=true;
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
        if(__instance.LinkedBuildings.NullOrEmpty())ShipFacilityTrace.MissingLink(owner,__instance);
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

[HarmonyPatch(typeof(CompGravshipThruster),nameof(CompGravshipThruster.CompInspectStringExtra))]
internal static class ShipThrusterInspectTrace
{
    // CompGravshipThruster overrides CompInspectStringExtra without calling base, so the base-class
    // trace never runs for the one facility kind the user reported as "not connected".
    static void Postfix(CompGravshipThruster __instance)
    {
        if(!__instance.parent.Spawned)return;
        if(!__instance.LinkedBuildings.NullOrEmpty())return;
        if(LayerMapComponent.For(__instance.parent.Map) is {} owner)ShipFacilityTrace.MissingLink(owner,__instance);
    }
}

[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.PotentialThingsToLinkTo))]
internal static class ShipFacilityCandidates
{
    static bool Prefix(ThingDef myDef,IntVec3 myPos,Map myMap,ref IEnumerable<Thing> __result)
    {
        if(!Compatibility.VanillaGravshipCompatibility.EngineDef(myDef)||LayerMapComponent.For(myMap) is not {} owner)return true;
        var engine=myPos.GetThingList(myMap).OfType<Building_GravEngine>().FirstOrDefault();if(engine==null)return true;
        if(myDef.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities==null)return true;
        // Ownership is decided by CanLinkTo (fail-open, only vetoing a positively different ship),
        // not here: a partially filtered list would make the engine's RelinkAll drop links the
        // facility side had legitimately established.
        //
        // Membership is ShipFacilities.LinksTo, not allowed.Contains: the engine list is VGE's, but a
        // vanilla PilotConsole standing on a VGE hull is declared only for the vanilla GravEngine def,
        // so an engine-list-only filter drops it exactly as native does (live log: allowed=False
        // canLink=True belongs=True distance=3.6/200 engineFacilities=0).
        var logical=myMap.listerThings.AllThings.Where(t=>ShipFacilities.IsEngineFacility(engine,t)&&
                owner.Ships.Distance(myPos,t.Position)<=ShipFacilities.Props(t)!.maxDistance)
            .OrderBy(t=>owner.Ships.Distance(myPos,t.Position)).ThenBy(t=>t.thingIDNumber)
            .GroupBy(t=>t.def).SelectMany(g=>g.Take(ShipFacilities.MaxSimultaneous(g.First()))).ToArray();
        // Empty means the layer-aware filter found nothing the native scan would have found either, so
        // fall through instead of replacing a possibly-correct native answer with an empty list.
        if(logical.Length==0)return true;
        __result=logical;return false;
    }
}

[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.LooselyConnectedToGravEngine))]
internal static class ShipLooseConnection
{
    static void Postfix(Building_GravEngine __instance,Thing thing,ref bool __result)
    {if(__result||LayerMapComponent.For(__instance.Map) is not {} o)return;if(o.Ships.Belongs(__instance,thing))__result=true;}
}

[HarmonyPatch(typeof(PlaceWorker_InRangeOfGravEngine),nameof(PlaceWorker_InRangeOfGravEngine.AllowsPlacing))]
internal static class ShipFacilityPlacement
{
    // Only widen: a rejected native placement becomes accepted when the logical distance is in range.
    static void Postfix(BuildableDef checkingDef,IntVec3 loc,Map map,ref AcceptanceReport __result)
    {
        if(__result.Accepted)return;
        if(LayerMapComponent.For(map) is not {} o||checkingDef is not ThingDef d)return;
        if(o.Ships.BuildOwner(loc) is not {} e)return;
        if(d.GetCompProperties<CompProperties_Facility>() is not {} p)return;
        if(o.Ships.Distance(e.Position,loc)<=p.maxDistance)__result=true;
    }
}

[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.InsideFootprint))]
internal static class ShipLogicalFootprint
{
    // Only widen: native already covers same-band footprint cells, this adds the other decks.
    static void Postfix(IntVec3 loc,Map map,Thing thingToExclude,ref bool __result)
    {
        if(__result||thingToExclude!=null)return; // Native exclusion previews retain native policy.
        if(LayerMapComponent.For(map) is not {} o)return;
        if(o.Ships.BuildOwner(loc) is not {} e)return;
        if(o.Ships.InField(e,loc))__result=true;
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

/// <summary>
/// Keeps an engine from linking a facility that stands on another engine's deck.
///
/// This used to be a fail-closed Prefix: anything Ships.Belongs could not positively confirm - an
/// unrefreshed cache, an ambiguous hull, a facility footprint straddling a deck edge - was silently
/// rejected, which is indistinguishable from "never linked" to the player. The veto now fires only
/// when the facility's own cell positively belongs to a DIFFERENT engine.
/// </summary>
[HarmonyPatch(typeof(CompAffectedByFacilities),nameof(CompAffectedByFacilities.CanLinkTo))]
internal static class ShipFacilityLinkOwnership
{
    static void Postfix(CompAffectedByFacilities __instance,Thing facility,ref bool __result)
    {
        if(!__result)return;
        if(__instance.parent is not Building_GravEngine engine||LayerMapComponent.For(engine.Map) is not {} o)return;
        if(ShipFacilities.Props(facility)==null)return;
        var owner=o.Ships.OwnerAt(facility.Position)??o.Ships.BuildOwner(facility.Position);
        if(owner!=null&&owner!=engine)__result=false;
    }
}

[HarmonyPatch(typeof(Thing),nameof(Thing.SetFaction))]
internal static class ShipOwnershipFactionChange
{
    static void Postfix(Thing __instance)
    {if(__instance is Building_GravEngine||__instance.TryGetComp<CompGravshipFacility>()!=null)LayerMapComponent.For(__instance.Map)?.Ships.MarkDirty();}
}
