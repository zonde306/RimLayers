using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
namespace RimLayers.Cargo;

internal static class CargoPlacement
{
    internal static bool Supported(Map map,IntVec3 at)=>at.IsValid&&at.InBounds(map)&&LayerMapComponent.For(map)?.Structure.HasSurface(at)==true;
    internal static bool Free(Map map,IntVec3 at,Thing? ignored)=>at.IsValid&&at.InBounds(map)&&
        !at.GetThingList(map).Any(t=>t!=ignored&&t is Building or Blueprint or Frame)&&
        !at.GetThingList(map).Any(t=>t.def.category==ThingCategory.Item);
    internal static bool HasOperatorCell(Map map,IntVec3 at)=>GenAdj.CardinalDirections.Any(d=>Supported(map,at+d)&&(at+d).Standable(map));
    internal static AcceptanceReport Validate(BuildableDef def,IntVec3 at,Map map,Thing? ignored=null)
    {
        if(def is not ThingDef td||td.GetModExtension<CargoProperties>() is not {} props)return true;
        var owner=LayerMapComponent.For(map);
        if(owner==null||!Supported(map,at))return "RL_CargoNeedsFloor".Translate();
        if(!HasOperatorCell(map,at))return "RL_CargoBlocked".Translate();
        if(props.direction!=0)
        {
            var remote=owner.Adjacent(at,props.direction);
            if(!Supported(map,remote))return "RL_CargoNeedsFloor".Translate();
            if(!Free(map,remote,null)||!HasOperatorCell(map,remote))return "RL_CargoOtherBlocked".Translate();
        }
        if(props.shaft)
        {
            var adjacent=new[]{owner.Adjacent(at,-1),owner.Adjacent(at,1)};
            var neighbors=CargoNetwork.For(map).Ports.Where(p=>p.Spawned&&adjacent.Contains(p.Position)).ToList();
            var masters=neighbors.Select(p=>p.Master).Where(m=>m!=null).Distinct().ToList();
            if(props.station)
            {
                if(masters.Count!=1||!masters[0]!.Props.shaft||neighbors.Any(p=>p.Master!=masters[0]))
                    return "RL_CargoNeedsShaft".Translate();
            }
            else if(neighbors.Count>0)return "RL_CargoTwoMasters".Translate();
        }
        return true;
    }
}
public sealed class PlaceWorker_Cargo:PlaceWorker
{
    public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef,IntVec3 loc,Rot4 rot,Map map,Thing? thingToIgnore=null,Thing? thing=null)=>
        CargoPlacement.Validate(checkingDef,loc,map,thingToIgnore);
    public override void DrawGhost(ThingDef def,IntVec3 center,Rot4 rot,Color ghostCol,Thing? thing=null)
    {
        var owner=LayerMapComponent.For(Find.CurrentMap);
        if(owner==null||def.GetModExtension<CargoProperties>() is not {direction: not 0} props)return;
        var other=owner.Adjacent(center,props.direction);
        if(other.IsValid)GenDraw.DrawFieldEdges(new List<IntVec3>{center},CargoPlacement.Supported(owner.map,other)?Color.green:Color.red);
    }
}
[HarmonyPatch(typeof(Frame),nameof(Frame.CompleteConstruction))]
internal static class CargoConstructionCommit
{
    static bool Prefix(Frame __instance,Pawn worker)
    {
        if(CargoPlacement.Validate(__instance.def.entityDefToBuild,__instance.Position,__instance.Map,__instance).Accepted)return true;
        if(worker.CurJob?.targetA.Thing==__instance)worker.jobs.curDriver.EndJobWith(JobCondition.Incompletable);
        return false;
    }
}
[HarmonyPatch(typeof(Designator_Deconstruct),nameof(Designator_Deconstruct.CanDesignateThing))]
internal static class CargoEmptyBeforeDismantle
{
    static void Postfix(Thing t,ref AcceptanceReport __result)
    {
        if(t is Building_CargoHoist {Contents.Count:>0})__result="RL_CargoUnloadFirst".Translate();
    }
}
