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
        if(GenCommandLine.CommandLineArgPassed("rimlayersv1test"))
            Log.Message("[RimLayers.CaptureTrace] core="+engine.ThingID+",cells="+capture?.Cells.Count+",consoles="+string.Join(";",engine.Map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole).Select(t=>t.ThingID+"@"+t.Position+":"+engine.OnValidSubstructure(t))));
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
    static Exception? Finalizer(Exception? __exception,ShipCapture? __state){ShipLayers.Capture=__state;return __exception;}
}

[HarmonyPatch(typeof(Gravship),MethodType.Constructor,new[]{typeof(Building_GravEngine)})]
internal static class LayerShipNormalize
{static void Postfix(Gravship __instance,Building_GravEngine engine)
    {if(ShipLayers.Capture is {} c&&c.Engine==engine)ShipLayers.AttachAndNormalize(__instance,c);
     else if(LayerMapComponent.For(engine.Map)==null)ShipLayers.AttachNativeSingle(__instance,engine);}}

[HarmonyPatch]
internal static class LayerShipCaptureCells
{
    static IEnumerable<MethodBase> TargetMethods()=>new[]{"TransferZones","CopyAreas","CopyCellContents"}.Select(n=>AccessTools.Method(typeof(Gravship),n));
    static void Prefix(Gravship __instance,ref HashSet<IntVec3> engineFloors)
    {if(ShipLayers.Capture is {} c&&c.Engine==__instance.Engine)engineFloors=c.Cells;}
}
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
[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructureAt))]
internal static class LayerShipOnboardCell
{
    static bool Prefix(Building_GravEngine __instance,IntVec3 cell,ref bool __result)
    {
        if(LayerMapComponent.For(__instance.Map) is not {} o||!ShipStructureService.Core(__instance))return true;
        __result=o.Ships.Get(__instance) is {Ambiguous:false} s&&s.Cells.Contains(cell)&&o.Ships.InField(__instance,cell);return false;
    }
}
