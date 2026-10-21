using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Cargo;

/// <summary>Extends the existing movement toil, not the Job/Driver. Persisted requests rebind after loading.</summary>
internal static class CargoAccess
{
    internal static void Wrap(Toil toil,TargetIndex index,bool deposit)
    {
        var init=toil.initAction;var tick=toil.tickAction;
        var interval=toil.tickIntervalAction;
        var mode=toil.defaultCompleteMode;
        bool progressAttached=false,cargoStep=false;
        CargoRequest? FindRequest(Pawn pawn)=>CargoNetwork.For(pawn.Map).Hoists
            .SelectMany(h=>h.Requests).FirstOrDefault(r=>r.Pawn==pawn&&r.Job==pawn.CurJob&&
                r.ToilIndex==pawn.jobs.curDriver.CurToilIndex&&r.Deposit==deposit&&!r.CallOnly);
        void AttachProgress()
        {
            if(!progressAttached)
            {
                progressAttached=true;
                toil.WithProgressBar(TargetIndex.None,()=>
                {
                    var current=FindRequest(toil.actor);
                    return current?.Port.Master is {} h?(float)current.Handled/h.Props.handlingTicks:0;
                });
            }
        }
        bool Begin(Pawn pawn)
        {
            Thing? target=pawn.CurJob.GetTarget(index).Thing;
            var master=deposit?(target as Building_CargoPort)?.Master:CargoNetwork.OwnerOf(target);
            if(master==null)return false;
            cargoStep=true;
            var port=deposit?target as Building_CargoPort:CargoNetwork.For(pawn.Map).BestPort(master,pawn,pawn.Position,out _);
            if(port==null){pawn.jobs.curDriver.EndJobWith(JobCondition.Incompletable);return true;}
            Thing? item=deposit?pawn.carryTracker.CarriedThing:target;
            int count=deposit?item?.stackCount??0:Math.Min(item?.stackCount??0,Math.Max(1,pawn.CurJob.count));
            if(!deposit&&pawn.CurJob.count<=0&&pawn.CurJob.countQueue?.Count>0)count=Math.Min(item!.stackCount,pawn.CurJob.countQueue[0]);
            var request=master.Request(pawn,port,item,count,deposit);
            if(request==null){pawn.jobs.curDriver.EndJobWith(JobCondition.Incompletable);return true;}
            request.ToilIndex=pawn.jobs.curDriver.CurToilIndex;
            AttachProgress();
            toil.defaultCompleteMode=ToilCompleteMode.Never;
            pawn.pather.StartPath(port,PathEndMode.Touch);
            return true;
        }
        toil.initAction=()=>
        {
            var p=toil.actor;
            if(Begin(p))return;
            toil.defaultCompleteMode=mode;init?.Invoke();
        };
        toil.tickAction=()=>
        {
            var p=toil.actor;
            if(!cargoStep)
            {
                var target=p.CurJob.GetTarget(index).Thing;
                if(CargoNetwork.OwnerOf(target)==null&&!(deposit&&target is Building_CargoPort)){tick?.Invoke();return;}
                cargoStep=true;
            }
            var r=FindRequest(p);
            if(r==null)
            {
                var t=p.CurJob.GetTarget(index).Thing;
                if(CargoNetwork.OwnerOf(t)!=null||deposit&&t is Building_CargoPort)
                {Begin(p);return;}
                tick?.Invoke();return;
            }
            AttachProgress();
            toil.defaultCompleteMode=ToilCompleteMode.Never;
            var main=r.Port.Master;
            if(main==null||!r.Valid){p.jobs.curDriver.EndJobWith(JobCondition.Incompletable);return;}
            if(!p.CanReachImmediate(r.Port,PathEndMode.Touch))
            {
                if(!p.pather.Moving)p.pather.StartPath(r.Port,PathEndMode.Touch);
                return;
            }
            if(!main.Ready(r))return;
            if(!deposit&&!Deliver(r,index)){p.jobs.curDriver.EndJobWith(JobCondition.Incompletable);return;}
            // Incoming cargo remains exclusively locked until native DepositHauledThingInContainer commits.
            if(!deposit)main.Requests.Remove(r);
            p.jobs.curDriver.ReadyForNextToil();
        };
        toil.tickIntervalAction=delta=>{if(!cargoStep||FindRequest(toil.actor)==null)interval?.Invoke(delta);};
    }
    private static bool Deliver(CargoRequest r,TargetIndex index)
    {
        var p=r.Pawn;var main=r.Port.Master!;
        if(r.Item==null||!main.Contents.Contains(r.Item))return false;
        // Stage the reserved real portion beside the operator; the original next toil performs pickup/wear/equip.
        // Direct placement never searches another floor or wipes an existing stack.
        var cell=new[]{p.Position}.Concat(GenAdj.CardinalDirections.Select(d=>p.Position+d))
            .FirstOrDefault(c=>c.InBounds(p.Map)&&c.Standable(p.Map)&&c.GetItemCount(p.Map)<c.GetMaxItemsAllowedInCell(p.Map)&&
                (c-r.Port.Position).LengthHorizontalSquared<=4,IntVec3.Invalid);
        if(!cell.IsValid)return false;
        var original=r.Item;
        if(!main.Contents.TryDrop(original,cell,p.Map,ThingPlaceMode.Direct,r.Count,out var delivered))return false;
        p.Map.reservationManager.Release(original,p,r.Job);
        if(!p.Reserve(delivered,r.Job,1,-1))return false;
        r.Job.SetTarget(index,delivered);
        return true;
    }
}

[HarmonyPatch(typeof(Toils_Goto),nameof(Toils_Goto.GotoThing),new[]{typeof(TargetIndex),typeof(PathEndMode),typeof(bool)})]
internal static class CargoGoto
{
    static void Postfix(Toil __result,TargetIndex ind)=>CargoAccess.Wrap(__result,ind,false);
}
[HarmonyPatch(typeof(Toils_Haul),nameof(Toils_Haul.CarryHauledThingToContainer))]
internal static class CargoGotoDeposit
{
    static void Postfix(Toil __result)=>CargoAccess.Wrap(__result,TargetIndex.B,true);
}
[HarmonyPatch(typeof(ToilFailConditions),nameof(ToilFailConditions.DespawnedOrNull))]
internal static class CargoHeldTargetLifetime
{
    static void Postfix(LocalTargetInfo target,Pawn actor,ref bool __result)
    {
        if(__result&&CargoNetwork.OwnerOf(target.Thing) is {Spawned:true} main&&main.Map==actor.Map)
            __result=false;
    }
}
[HarmonyPatch(typeof(ThingOwnerUtility),nameof(ThingOwnerUtility.TryGetInnerInteractableThingOwner))]
internal static class CargoDestinationOwner
{
    static bool Prefix(Thing thing,ref ThingOwner? __result)
    {
        if(thing is not Building_CargoPort p)return true;
        __result=p.Master?.Contents;return false;
    }
}
[HarmonyPatch(typeof(Toils_Haul),nameof(Toils_Haul.DepositHauledThingInContainer))]
internal static class CargoDeposit
{
    static void Postfix(Toil __result,TargetIndex containerInd,Action? onDeposited)
    {
        var init=__result.initAction;
        __result.initAction=()=>
        {
            var pawn=__result.actor;
            if(pawn.CurJob.GetTarget(containerInd).Thing is not Building_CargoPort port){init?.Invoke();return;}
            var main=port.Master;var carried=pawn.carryTracker.CarriedThing;
            var r=main?.Requests.FirstOrDefault(r=>r.Pawn==pawn&&r.Job==pawn.CurJob&&r.Deposit&&r.Port==port);
            if(r==null||!main!.Ready(r)||carried==null||!main.GetStoreSettings().AllowedToAccept(carried))
            {pawn.jobs.curDriver.EndJobWith(JobCondition.Incompletable);return;}
            int count=Math.Min(r.Count,Math.Min(carried.stackCount,main.SpaceFor(carried,pawn.CurJob)));
            int moved=pawn.carryTracker.innerContainer.TryTransferToContainer(carried,main.Contents,count);
            main.Requests.Remove(r);
            if(moved<=0){pawn.jobs.curDriver.EndJobWith(JobCondition.Incompletable);return;}
            onDeposited?.Invoke();
        };
    }
}
[HarmonyPatch(typeof(JobDriver),nameof(JobDriver.Cleanup))]
internal static class CargoRequestCleanup
{
    static void Prefix(JobDriver __instance)
    {
        if(__instance.pawn?.Map is not {} map)return;
        foreach(var h in CargoNetwork.For(map).Hoists)h.Release(__instance.pawn,__instance.job);
    }
}
[HarmonyPatch(typeof(Thing),"get_AmbientTemperature")]
internal static class CargoTemperature
{
    static bool Prefix(Thing __instance,ref float __result)
    {
        if(CargoNetwork.OwnerOf(__instance) is not {Spawned:true} main)return true;
        __result=GenTemperature.GetTemperatureForCell(main.BasketCell,main.Map);return false;
    }
}
public sealed class JobDriver_CallCargo:JobDriver
{
    public override bool TryMakePreToilReservations(bool errorOnFailed)=>true;
    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
        yield return Toils_Goto.GotoThing(TargetIndex.A,PathEndMode.Touch);
        var wait=ToilMaker.MakeToil("CallCargo");wait.defaultCompleteMode=ToilCompleteMode.Never;
        wait.initAction=()=>
        {
            var p=job.targetA.Thing as Building_CargoPort;
            var r=p?.Master?.Request(pawn,p,null,0,false,true);
            if(r==null){EndJobWith(JobCondition.Incompletable);return;}
            r.ToilIndex=CurToilIndex;
        };
        wait.tickAction=()=>
        {
            var port=job.targetA.Thing as Building_CargoPort;
            var r=port?.Master?.Requests.FirstOrDefault(r=>r.Pawn==pawn&&r.Job==job&&r.CallOnly);
            if(r==null||!r.Valid){EndJobWith(JobCondition.Incompletable);return;}
            if(port!.Master!.Ready(r)){port.Master.Requests.Remove(r);ReadyForNextToil();}
        };
        wait.WithProgressBar(TargetIndex.None,()=>
        {
            var port=job.targetA.Thing as Building_CargoPort;
            var r=port?.Master?.Requests.FirstOrDefault(r=>r.Pawn==pawn&&r.Job==job&&r.CallOnly);
            return r==null?0:(float)r.Handled/port!.Master!.Props.handlingTicks;
        });
        yield return wait;
    }
}
