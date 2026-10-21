using HarmonyLib;
using RimLayers.Core;
using RimLayers.Work;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Cargo;

internal static class CargoCandidates
{
    internal static Thing? Better(Thing? current,IntVec3 start,Map map,Pawn? pawn,Predicate<Thing>? validator,
        Predicate<Thing> matches,float maxDistance,Func<Thing,float>? priority=null,TraverseParms? parms=null)
    {
        if(pawn==null||!CargoNetwork.CanOperate(pawn))return current;
        var net=CargoNetwork.For(map);if(!net.Hoists.Any())return current;
        var owner=LayerMapComponent.For(map);if(owner==null)return current;
        float best=float.PositiveInfinity,score=current==null?float.NegativeInfinity:priority?.Invoke(current)??0;
        if(current!=null)
        {
            if(CargoNetwork.OwnerOf(current) is {} existing)net.BestPort(existing,pawn,start,out best,parms);
            else {using var path=map.pathFinder.FindPathNow(start,current,parms??TraverseParms.For(pawn),null,PathEndMode.ClosestTouch);if(path.Found)best=path.TotalCost;}
        }
        foreach(var main in net.Hoists)
        {
            if(!main.Contents.Any(t=>matches(t)))continue;
            var port=net.BestPort(main,pawn,start,out var cost,parms);
            if(port==null||QueryGeometry.DistanceSquared(owner,start,port.Position)>maxDistance*maxDistance)continue;
            foreach(var item in main.Contents)
            {
                if(!matches(item)||item.IsForbidden(pawn)||validator!=null&&!validator(item))continue;
                float s=priority?.Invoke(item)??0;
                if(float.IsNaN(s)||s<score||s==score&&cost>=best)continue;
                current=item;score=s;best=cost;
            }
        }
        return current;
    }
}
[HarmonyPatch(typeof(Reachability),nameof(Reachability.CanReach),new[]{typeof(IntVec3),typeof(LocalTargetInfo),typeof(PathEndMode),typeof(TraverseParms)})]
internal static class CargoItemReachability
{
    [HarmonyPriority(Priority.First)]
    static bool Prefix(Map ___map,IntVec3 start,LocalTargetInfo dest,TraverseParms traverseParams,ref bool __result)
    {
        if(CargoNetwork.OwnerOf(dest.Thing) is not {Spawned:true} main)return true;
        __result=traverseParams.pawn is {} p&&CargoNetwork.For(___map).BestPort(main,p,start,out _,traverseParams)!=null;
        return false;
    }
}
[HarmonyPatch(typeof(GenClosest),nameof(GenClosest.ClosestThingReachable))]
internal static class CargoReachableCandidates
{
    static void Postfix(IntVec3 root,Map map,ThingRequest thingReq,TraverseParms traverseParams,float maxDistance,
        Predicate<Thing>? validator,IEnumerable<Thing>? customGlobalSearchSet,ref Thing? __result)
    {
        if(thingReq.IsUndefined&&customGlobalSearchSet==null)return;
        __result=CargoCandidates.Better(__result,root,map,traverseParams.pawn,validator,
            t=>(thingReq.IsUndefined||thingReq.Accepts(t))&&(customGlobalSearchSet==null||customGlobalSearchSet.Contains(t)),maxDistance,parms:traverseParams);
    }
}
[HarmonyPatch(typeof(GenClosest),nameof(GenClosest.ClosestThing_Global_Reachable))]
internal static class CargoGlobalCandidates
{
    static void Postfix(IntVec3 center,Map map,IEnumerable<Thing> searchSet,TraverseParms traverseParams,float maxDistance,
        Predicate<Thing>? validator,Func<Thing,float>? priorityGetter,ref Thing? __result)
    {
        __result=CargoCandidates.Better(__result,center,map,traverseParams.pawn,validator,
            t=>searchSet.Contains(t),maxDistance,priorityGetter,traverseParams);
    }
}
[HarmonyPatch(typeof(FoodUtility),"SpawnedFoodSearchInnerScan")]
internal static class CargoFoodCandidates
{
    static void Postfix(Pawn eater,IntVec3 root,TraverseParms traverseParams,float maxDistance,Predicate<Thing>? validator,ref Thing? __result)
    {
        var pawn=traverseParams.pawn??eater;
        if(pawn.Map is not {} map||LayerMapComponent.For(map) is not {} owner)return;
        var net=CargoNetwork.For(map);
        float score=__result==null?float.NegativeInfinity:
            FoodUtility.FoodOptimality(eater,__result,FoodUtility.GetFinalIngestibleDef(__result),
                (float)Math.Sqrt(QueryGeometry.DistanceSquared(owner,root,__result.PositionHeld)));
        foreach(var main in net.Hoists)
        {
            var port=net.BestPort(main,pawn,root,out float cost,traverseParams);
            if(port==null||QueryGeometry.DistanceSquared(owner,root,port.Position)>maxDistance*maxDistance)continue;
            foreach(var item in main.Contents)
            {
                if(!item.def.IsNutritionGivingIngestible||item.IsForbidden(pawn)||!pawn.CanReserve(item,10,1)||
                    validator!=null&&!validator(item))continue;
                float s=FoodUtility.FoodOptimality(eater,item,FoodUtility.GetFinalIngestibleDef(item),cost/13f);
                if(s>score){score=s;__result=item;}
            }
        }
    }
}
[HarmonyPatch(typeof(ItemAvailability),nameof(ItemAvailability.ThingsAvailableAnywhere))]
internal static class CargoMaterialAvailability
{
    static void Postfix(ThingDef need,int amount,Pawn pawn,ref bool __result)
    {
        if(__result||pawn.Map==null)return;
        int local=pawn.Map.listerThings.ThingsOfDef(need).Where(t=>!t.IsForbidden(pawn)).Sum(t=>t.stackCount);
        foreach(var main in CargoNetwork.For(pawn.Map).Hoists)
            if(CargoNetwork.For(pawn.Map).BestPort(main,pawn,pawn.Position,out _)!=null)
                local+=main.Contents.Where(t=>t.def==need&&!t.IsForbidden(pawn)).Sum(t=>t.stackCount);
        __result=local>=amount;
    }
}
[HarmonyPatch(typeof(JobDriver_HaulToContainer),nameof(JobDriver_HaulToContainer.TryMakePreToilReservations))]
internal static class CargoCapacityReservation
{
    static void Postfix(JobDriver_HaulToContainer __instance,ref bool __result)
    {
        if(!__result||__instance.job.targetB.Thing is not Building_CargoPort {Master:{} main} port)return;
        var item=__instance.job.targetA.Thing;
        // Native ordered-job preflight can run before pawn.CurJob is assigned.
        var request=main.Request(__instance.pawn,port,item,Math.Min(item.stackCount,__instance.job.count),true,job:__instance.job);
        __result=request!=null;
        if(request!=null)__instance.job.count=request.Count;
    }
}
