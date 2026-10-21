using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Navigation;

/// <summary>Native holder/job/carry lifecycle, with tick-owned logical movement and conservative emergency descent.</summary>
public sealed class LayerJumpFlyer : PawnFlyer
{
    private Vector3 logicalStart,logicalEnd;
    private float rise,bodyHeight,halfWidth;
    private double consumed;
    private bool waiting,notified;
    public bool Waiting=>waiting;
    public double Progress=>consumed;
    public JumpArc Arc=>new(new(logicalStart.x,logicalStart.y,logicalStart.z),new(logicalEnd.x,logicalEnd.y,logicalEnd.z),rise,bodyHeight,halfWidth);
    public ShotPoint LogicalPosition=>Arc.Point(consumed);
    internal static readonly AccessTools.FieldRef<PawnFlyer,IntVec3> Destination=AccessTools.FieldRefAccess<PawnFlyer,IntVec3>("destCell");
    private static readonly AccessTools.FieldRef<PawnFlyer,JobQueue> CapturedJobs=AccessTools.FieldRefAccess<PawnFlyer,JobQueue>("jobQueue");
    private static readonly AccessTools.FieldRef<PawnFlyer,float> Distance=AccessTools.FieldRefAccess<PawnFlyer,float>("flightDistance");
    private static readonly AccessTools.FieldRef<PawnFlyer,Vector3> Ground=AccessTools.FieldRefAccess<PawnFlyer,Vector3>("groundPos");
    private static readonly AccessTools.FieldRef<PawnFlyer,Vector3> Effective=AccessTools.FieldRefAccess<PawnFlyer,Vector3>("effectivePos");
    private static readonly AccessTools.FieldRef<PawnFlyer,float> Height=AccessTools.FieldRefAccess<PawnFlyer,float>("effectiveHeight");

    private void SetArc(JumpArc arc)
    {
        logicalStart=new((float)arc.Start.X,(float)arc.Start.Height,(float)arc.Start.Z);
        logicalEnd=new((float)arc.End.X,(float)arc.End.Height,(float)arc.End.Z);
        rise=(float)arc.Rise;bodyHeight=(float)arc.BodyHeight;halfWidth=(float)arc.HalfWidth;
    }
    public override void SpawnSetup(Map map,bool respawningAfterLoad)
    {
        if(!respawningAfterLoad)
        {
            if(JumpService.LaunchArc is not {} plan)throw new InvalidOperationException("Layer jump must have a validated launch trajectory.");
            SetArc(plan);Distance(this)=(float)plan.Distance;
        }
        base.SpawnSetup(map,respawningAfterLoad);
        UpdatePose(true);
    }
    internal void UpdatePose(bool move)
    {
        var owner=LayerMapComponent.For(Map);if(owner==null)return;
        var p=LogicalPosition;var cell=JumpService.Cell(owner,p);if(!cell.IsValid)return;
        owner.TryDecode(cell,out var at);
        float height=(float)(p.Height-owner.Elevation(at.LayerId)*3);
        var ground=new Vector3((float)p.X,def.Altitude,(float)p.Z+at.LayerId*owner.Layout!.Stride);
        Ground(this)=ground;Height(this)=Math.Max(0,height/Math.Max(def.pawnFlyer.heightFactor,.01f));
        Effective(this)=ground+Vector3.forward*height+Altitudes.AltIncVect*Height(this);
        if(move)Position=cell;
    }
    private double AtTick(int tick)=>Math.Clamp(def.pawnFlyer.Worker.AdjustedProgress(Math.Clamp((float)tick/Math.Max(1,ticksFlightTime),0,1)),0,1);
    protected override void TickInterval(int delta)
    {
        var owner=LayerMapComponent.For(Map);if(owner==null)return;
        if(waiting)
        {
            if(this.IsHashIntervalTick(30,delta))TryEmergencyLanding(owner);
            return;
        }
        double to=AtTick(ticksFlying+delta);
        var hit=JumpGeometry.First(Arc,new LayerShotScene(owner,FlyingPawn,includeCover:true),consumed,to);
        if(hit is {} obstruction)
        {
            consumed=Math.Max(consumed,obstruction.Time-.001/Math.Max(Arc.Distance,1));
            Hold(owner);return;
        }
        consumed=to;UpdatePose(true);
        if(ticksFlying>=ticksFlightTime&&!CanLand(owner))
        {Hold(owner);return;}
        base.TickInterval(delta);
    }
    private bool CanLand(LayerMapComponent owner)
    {
        Thing? flying=FlyingPawn??GetDirectlyHeldThings().FirstOrDefault();
        return flying!=null&&JumpService.Landing(owner,flying,Destination(this))&&
            (CarriedThing==null||JumpService.Landing(owner,CarriedThing,Destination(this)));
    }
    private void Hold(LayerMapComponent owner)
    {
        waiting=true;UpdatePose(true);
        if(!TryEmergencyLanding(owner)&&!notified)
        {
            notified=true;
            Messages.Message("RL_JumpWaiting".Translate(),new TargetInfo(Position,Map),MessageTypeDefOf.CautionInput,false);
        }
    }
    private bool TryEmergencyLanding(LayerMapComponent owner)
    {
        Thing? flying=FlyingPawn??GetDirectlyHeldThings().FirstOrDefault();if(flying==null)return false;
        var p=LogicalPosition;
        foreach(int layer in owner.Layout!.OrderedLayers.Reverse())
        {
            double floor=owner.Elevation(layer)*3+JumpGeometry.FeetOffset;
            if(floor>p.Height+.0001)continue;
            var center=owner.ToStorage(new LayerCell(layer,(int)Math.Floor(p.X),(int)Math.Floor(p.Z)));
            foreach(var cell in GenRadial.RadialCellsAround(center,3.9f,true))
            {
                if(!owner.TryDecode(cell,out var at)||at.LayerId!=layer||!JumpService.Landing(owner,flying,cell)||
                   CarriedThing!=null&&!JumpService.Landing(owner,CarriedThing,cell))continue;
                var end=new ShotPoint(at.X+.5,floor,at.Z+.5);
                var descent=new JumpArc(p,end,0,bodyHeight,halfWidth);
                if(JumpGeometry.First(descent,new LayerShotScene(owner,flying,includeCover:true))!=null)continue;
                SetArc(descent);Distance(this)=(float)descent.Distance;consumed=0;ticksFlying=0;
                ticksFlightTime=Math.Max(30,(int)Math.Ceiling(descent.Distance/def.pawnFlyer.flightSpeed*60));
                Destination(this)=cell;waiting=false;UpdatePose(true);return true;
            }
        }
        return false;
    }
    protected override void RespawnPawn()
    {
        var owner=LayerMapComponent.For(Map);
        if(owner==null||!CanLand(owner)){if(owner!=null)Hold(owner);return;}
        var pawn=FlyingPawn;
        if(pawn==null)
        {
            // A prior partial native drop may have placed the pawn but retained its cargo.
            foreach(var cargo in GetDirectlyHeldThings().ToList())
                GetDirectlyHeldThings().TryDrop(cargo,Destination(this),Map,ThingPlaceMode.Direct,out _,playDropSound:false);
            return;
        }
        var previous=JumpService.HandlingPawn;JumpService.HandlingPawn=pawn;
        // Vanilla restores jobs, then immediately performs another override query. Restore once,
        // after that query, so a newly resumed player order cannot be overwritten by Wait_Combat.
        var queued=CapturedJobs(this);CapturedJobs(this)=null!;
        try
        {
            base.RespawnPawn();
            if(pawn.Spawned&&queued!=null)pawn.jobs.RestoreCapturedJobs(queued);
        }
        finally
        {
            if(queued?.Count>0)CapturedJobs(this)=queued;
            JumpService.HandlingPawn=previous;
        }
    }
    public override void Destroy(DestroyMode mode=DestroyMode.Vanish)
    {
        // Native TickInterval unconditionally destroys after RespawnPawn. Never discard a failed drop.
        if(GetDirectlyHeldThings().Count>0)
        {
            waiting=true;
            if(!notified){notified=true;Log.Warning("[RimLayers] Jump landing retained its native holder; waiting for a safe landing.");}
            return;
        }
        base.Destroy(mode);
    }
    public override string GetInspectString()=>waiting?"RL_JumpWaiting".Translate().ToString():base.GetInspectString();
    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach(var gizmo in base.GetGizmos())yield return gizmo;
        if(waiting)yield return RetryGizmo();
    }
    internal Command_Action RetryGizmo()=>new(){
        defaultLabel="RL_JumpRetry".Translate(),defaultDesc="RL_JumpWaiting".Translate(),
        action=()=>{if(LayerMapComponent.For(Map) is {} owner)TryEmergencyLanding(owner);}
    };
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref logicalStart,"rlJumpStart");Scribe_Values.Look(ref logicalEnd,"rlJumpEnd");
        Scribe_Values.Look(ref rise,"rlJumpRise");Scribe_Values.Look(ref bodyHeight,"rlJumpBody");
        Scribe_Values.Look(ref halfWidth,"rlJumpHalfWidth");Scribe_Values.Look(ref consumed,"rlJumpProgress");
        Scribe_Values.Look(ref waiting,"rlJumpWaiting");Scribe_Values.Look(ref notified,"rlJumpNotified");
    }
}

[HarmonyPatch(typeof(PawnFlyer),"RecomputePosition")]
internal static class LayerJumpPositionPatch
{
    static bool Prefix(PawnFlyer __instance)
    {
        if(__instance is not LayerJumpFlyer flyer)return true;
        flyer.UpdatePose(false);return false;
    }
}
[HarmonyPatch(typeof(PawnFlyer),"CheckDestination")]
internal static class LayerJumpDestinationPatch
{
    static bool Prefix(PawnFlyer __instance)=>__instance is not LayerJumpFlyer;
}
