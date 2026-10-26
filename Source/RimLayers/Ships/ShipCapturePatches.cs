using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Ships;

[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.CanLaunch))]
internal static class LayerShipLaunchRules
{
    static void Postfix(Building_GravEngine __instance,ref AcceptanceReport __result)
    {
        if(!ShipLayers.Eligible(__instance))return;
        var report=LayerMapComponent.For(__instance.Map)?.Ships.LaunchReport(__instance)??true;
        if(!report.Accepted)__result=report;
        else if(__result.Accepted)ShipLayers.Prepare(__instance,out __result);
    }
}

[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.GenerateGravship))]
internal static class LayerShipCapture
{
    static void Prefix(Building_GravEngine engine,out ShipCapture? __state)
    {
        __state=ShipLayers.Capture;
        if(!ShipLayers.Eligible(engine))return;
        if(LayerMapComponent.For(engine.Map)==null)return; // Existing unmanaged maps require the separately gated expansion stage.
        var capture=ShipLayers.Prepare(engine,out var report);
        if(!report.Accepted)throw new InvalidOperationException("[RimLayers] Unsafe multi-layer ship capture: "+report.Reason);
        if(capture!=null)
        {
            ShipSeparation.Commit(capture);
            capture=ShipLayers.Prepare(engine,out report);
            if(!report.Accepted)throw new InvalidOperationException("[RimLayers] Ship changed during separation: "+report.Reason);
        }
        ShipLayers.Capture=capture;
    }
    static void Postfix(Gravship __result)
    {
        var capture=ShipLayers.Capture;
        if(capture==null||__result==null||!ShipLayers.TryData(__result,out _))return;
        var map=capture.Owner.map;
        foreach(var c in capture.Cells)
        {
            if(!capture.Owner.TryDecode(c,out var logical))continue;
            capture.Owner.StructuralCells.Remove(c);capture.Owner.PendingCollapse.Remove(c);
        }
        foreach(var c in capture.Cells)
        {
            if(!capture.Owner.TryDecode(c,out var logical))continue;
            map.roofGrid.SetRoof(c,null);
            map.terrainGrid.RemoveGravshipTerrainUnsafe(c,map.cellIndices.CellToIndex(c));
            if(logical.LayerId>0)map.terrainGrid.SetTerrain(c,RimLayersDefOf.RL_OpenAir);
            map.gasGrid.ClearCellUnsafe(c);

            map.snowGrid.SetDepth(c,0f);
            map.sandGrid.SetDepth(c,0f);
            foreach(var designation in map.designationManager.AllDesignationsAt(c).ToList())map.designationManager.RemoveDesignation(designation);
            map.areaManager.Home[c]=false;map.areaManager.BuildRoof[c]=false;map.areaManager.NoRoof[c]=false;
            capture.Owner.PendingCollapse.Remove(c);
            capture.Owner.Underlays.Release(c);capture.Owner.Ships.Forget(c);
            capture.Owner.Invalidate(c,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Power|LayerChangeKind.Light);
        }
        foreach(var roof in capture.HollowRoofs)
            if(!capture.Owner.Underlays.ReleaseSupportRoof(roof,capture.Engine.thingIDNumber))map.roofGrid.SetRoof(roof,null);
        capture.Owner.Connectors.Invalidate();
    }
    static Exception? Finalizer(Exception? __exception,ShipCapture? __state)
    {
        if(__exception!=null)
            Log.Error("[RimLayers] GenerateGravship threw; layered payload rolled back.\n"+__exception);
        ShipLayers.Capture=__state;
        return __exception;
    }
}

/// <summary>
/// Native DetermineLaunchDirection dereferences the pilotConsole field, which CopyCellContents only
/// fills for ThingDefOf.PilotConsole. VGE's pilot cockpit / pilot bridge are different defs, so a
/// VGE ship threw NullReferenceException inside Gravship..ctor and could never launch. Resolve the
/// console here, before the native read, from the engine's own Controls facility.
/// </summary>
[HarmonyPatch(typeof(Gravship),"DetermineLaunchDirection")]
internal static class LayerShipPilotConsoleFallback
{
    /// <summary>
    /// Native DetermineLaunchDirection dereferences pilotConsole, which CopyCellContents only fills for
    /// ThingDefOf.PilotConsole. VGE fills it from its own AddThing postfix, but a third-party engine may
    /// ship a Controls facility that neither path recognises, so resolve one here before the native
    /// read. Candidates come from the ship payload first: by this point CopyCellContents has already
    /// DESPAWNED the facilities into Gravship, so the map is no longer authoritative.
    /// </summary>
    static void Prefix(Gravship __instance)
    {
        if(__instance.PilotConsole!=null)return;
        var engine=__instance.Engine;
        if(engine==null)return;
        IEnumerable<Thing> candidates=Enumerable.Empty<Thing>();
        if(AccessTools.Field(typeof(Gravship),"things")?.GetValue(__instance) is System.Collections.IDictionary payload)
            candidates=payload.Keys.Cast<Thing>().ToList();
        if(engine.Spawned&&engine.Map!=null)candidates=candidates.Concat(engine.Map.listerThings.AllThings);
        Building? best=null;float bestDistance=float.PositiveInfinity;
        foreach(var thing in candidates)
        {
            if(thing is not Building building||building.Destroyed)continue;
            var facilityDef=building.def.GetCompProperties<CompProperties_GravshipFacility>();
            if(facilityDef?.componentTypeDef?.defName!="Controls"&&building.TryGetComp<CompPilotConsole>()==null)continue;
            float distance=building.Position.DistanceToSquared(engine.Position);
            if(distance<bestDistance){bestDistance=distance;best=building;}
        }
        if(best==null)return;
        AccessTools.Field(typeof(Gravship),"pilotConsole").SetValue(__instance,best);
    }
}

[HarmonyPatch(typeof(Gravship),MethodType.Constructor,new[]{typeof(Building_GravEngine)})]
internal static class LayerShipNormalize
{static void Postfix(Gravship __instance,Building_GravEngine engine)
    {
        try
        {
            if(ShipLayers.Capture is {} c&&c.Engine==engine)ShipLayers.AttachAndNormalize(__instance,c);
            else if(LayerMapComponent.For(engine.Map)==null)ShipLayers.AttachNativeSingle(__instance,engine);
        }
        catch(Exception exception)
        {
            Log.Error("[RimLayers] Layered ship normalization failed: "+exception);
            throw;
        }
    }}

/// <summary>
/// Diagnostics only: Gravship..ctor merges the whole constructor into one Harmony frame, so a null
/// reference thrown after DetermineLaunchDirection cannot be attributed to a statement. These two
/// markers bracket the remaining steps of the native constructor.
/// </summary>
[HarmonyPatch]
internal static class LayerShipCaptureCells
{
    static IEnumerable<MethodBase> TargetMethods()=>new[]{"TransferZones","CopyAreas","CopyCellContents"}.Select(n=>AccessTools.Method(typeof(Gravship),n));
    static void Prefix(Gravship __instance,ref HashSet<IntVec3> engineFloors)
    {
        // Widen the native capture walk to every deck of the ship. Without this, CopyCellContents only
        // visits the engine's own layer and every upper-deck thing (including VGE's pilot bridge) never
        // reaches Gravship.AddThing.
        if(ShipLayers.Capture is {} c&&c.Engine==__instance.Engine)engineFloors=c.Cells;
    }
}

/// <summary>
/// VGE sets Gravship.pilotConsole from its own AddThing postfix, but that only fires for things that
/// CopyCellContents actually walks. On a multi-deck ship the bridge can sit on an upper deck, so record
/// every Controls facility that passes through AddThing and pick one before DetermineLaunchDirection.
/// </summary>
[HarmonyPatch(typeof(Gravship),"CopyCellContents")]
internal static class LayerShipPhysicalBounds
{
    static void Postfix(Gravship __instance,ref HashSet<IntVec3> outOccupiedCells)
    {
        if(ShipLayers.Capture is not {} c||c.Engine!=__instance.Engine)return;
        c.Owner.TryDecode(c.Origin,out var origin);
        outOccupiedCells=c.Cells.Select(p=>{c.Owner.TryDecode(p,out var l);return new IntVec3(l.X-origin.X,0,l.Z-origin.Z);}).ToHashSet();
    }
}
[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.OnValidSubstructure))]
internal static class LayerShipCaptureMembership
{
    static bool Prefix(Building_GravEngine __instance,Thing thing,ref bool __result)
    {
        if(ShipLayers.Capture is not {} c||c.Engine!=__instance||thing.Map!=c.Owner.map)return true;
        if(thing.def.building?.isAttachment==true)
        {var wall=GenConstruct.GetWallAttachedTo(thing);__result=thing.Spawned&&wall!=null&&c.Cells.Contains(wall.Position);}
        else __result=thing.Spawned&&thing.OccupiedRect().All(c.Cells.Contains);
        return false;
    }
}
[HarmonyPatch(typeof(Gravship),nameof(Gravship.ExposeData))]
internal static class LayerShipSave
{static void Postfix(Gravship __instance)=>ShipLayers.Expose(__instance);}

[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.IsOnboardGravship_NewTemp))]
internal static class LayerShipBoarding
{
    static bool Prefix(IntVec3 cell,Building_GravEngine engine,Pawn pawn,bool desperate,bool respectAllowedAreas,ref bool __result)
    {
        if(!ShipLayers.Eligible(engine)||LayerMapComponent.For(engine.Map) is not {} o||o.Ships.Get(engine) is not {Ambiguous:false} s||!s.Cells.Contains(cell))return true;
        __result=(!respectAllowedAreas||pawn==null||cell.InAllowedArea(pawn))&&
            (desperate||cell.GetRoom(engine.Map)?.PsychologicallyOutdoors==false);return false;
    }
}
// LayerShipOnboardCell used to live here: a Prefix on Building_GravEngine.ValidSubstructureAt that
// replaced the native answer with `Ships.Get(engine).Cells.Contains(cell) && Ships.InField(engine,cell)`.
// It is gone for two reasons. It was redundant - with VGE active ShipEngineDeckView already widens
// ValidSubstructure to every deck (ShipDeckTerrain.DeckCells), so the native `ValidSubstructure.Contains`
// returns true for an upper-deck console's interaction cell on its own; and it was wrong - InField is
// bounded by the engine's CompSubstructureFootprint radius (VGE_GravjumperEngine is 7.9) while facilities
// allow maxDistance 200, so a console far from its engine lost the launch button. Without VGE the native
// single-deck contract must stay intact, so there is nothing left for this patch to widen.
