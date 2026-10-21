using HarmonyLib;
using RimLayers.Core;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Mountains;

internal static class VerticalMining
{
    internal static IntVec3 Upper(Thing anchor)
    {
        if(anchor is Building_Elevator {ConnectsAdjacent:false} station)return station.MiningExit;
        var o=LayerMapComponent.For(anchor.Map);
        return o?.Adjacent(anchor.Position,ConnectorDefs.Direction(anchor.def.entityDefToBuild??anchor.def))??IntVec3.Invalid;
    }
    internal static Mineable? Rock(Thing anchor)
    {
        if(anchor is Building_Elevator {ConnectsAdjacent:false} station)return station.MiningRock;
        if(!anchor.Spawned||!ConnectorDefs.IsAdjacent(anchor.def.entityDefToBuild??anchor.def))return null;
        if(ConnectorDefs.IsDown(anchor.def.entityDefToBuild??anchor.def)&&!RimLayers.Underground.UndergroundService.TerrainAllows(anchor.Map,anchor.Position).Accepted)return null;
        var c=Upper(anchor);return c.IsValid?c.GetEdifice(anchor.Map) as Mineable:null;
    }
    internal static bool Ready(Thing anchor)=>anchor switch
    {
        Frame f=>f.workDone>=f.WorkToBuild&&Rock(f)!=null,
        Building_Stairs s=>Rock(s)!=null,
        _=>false
    };
    internal static bool BlockCommit(Frame frame,Pawn worker)
    {
        if(Rock(frame)==null)return false;
        if(worker.CurJob?.targetA.Thing==frame)worker.jobs.curDriver?.EndJobWith(JobCondition.Incompletable);
        return true;
    }
}

public sealed class WorkGiver_VerticalMine:WorkGiver_Scanner
{
    public override PathEndMode PathEndMode=>PathEndMode.Touch;
    public override Danger MaxPathDanger(Pawn pawn)=>Danger.Deadly;
    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)=>
        LayerMapComponent.For(pawn.Map)==null?Enumerable.Empty<Thing>():pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)
            .Concat(pawn.Map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs))
            .Concat(pawn.Map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("RL_LiftSingle")))
            .Concat(pawn.Map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("RL_StairsDown")))
            .Concat(pawn.Map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("RL_LiftSingleDown")))
            .Concat(pawn.Map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("RL_LiftVertical"))).Where(VerticalMining.Ready);
    public override bool HasJobOnThing(Pawn pawn,Thing t,bool forced=false)=>
        t.Faction==pawn.Faction&&VerticalMining.Ready(t)&&!t.IsForbidden(pawn)&&
        VerticalMining.Rock(t) is {} rock&&!rock.IsForbidden(pawn)&&pawn.CanReserve(rock)&&
        pawn.CanReserveAndReach(t,PathEndMode.Touch,Danger.Deadly);
    public override Job? JobOnThing(Pawn pawn,Thing t,bool forced=false)=>HasJobOnThing(pawn,t,forced)&&new HistoryEvent(HistoryEventDefOf.Mined,pawn.Named(HistoryEventArgsNames.Doer)).Notify_PawnAboutToDo_Job()?
        JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("RL_MineVertical"),VerticalMining.Rock(t),t):null;
}

/// <summary>Only approach geometry differs. Each strike invokes native Mine.DoDamage on the real rock.</summary>
public sealed class JobDriver_VerticalMine:JobDriver_Mine
{
    private int ticksToHit=-999;
    private static readonly Action<JobDriver_Mine,Thing,Toil,Pawn,IntVec3> Strike=
        AccessTools.MethodDelegate<Action<JobDriver_Mine,Thing,Toil,Pawn,IntVec3>>(AccessTools.Method(typeof(JobDriver_Mine),"DoDamage"));
    public override void ExposeData(){base.ExposeData();Scribe_Values.Look(ref ticksToHit,"verticalTicksToHit",-999);}
    public override bool TryMakePreToilReservations(bool errorOnFailed)=>
        pawn.Reserve(job.targetA,job,1,-1,null,errorOnFailed)&&pawn.Reserve(job.targetB,job,1,-1,null,errorOnFailed);
    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
        this.FailOn(()=>!VerticalMining.Ready(job.targetB.Thing)||VerticalMining.Rock(job.targetB.Thing)!=job.targetA.Thing);
        yield return Toils_Goto.GotoThing(TargetIndex.B,PathEndMode.Touch);
        var mine=ToilMaker.MakeToil("RimLayers_VerticalMine");
        mine.defaultCompleteMode=ToilCompleteMode.Never;
        mine.activeSkill=()=>SkillDefOf.Mining;
        mine.WithEffect(EffecterDefOf.Mine,TargetIndex.B);
        mine.WithProgressBar(TargetIndex.B,()=>job.targetA.Thing is {} t&&!t.Destroyed?1f-(float)t.HitPoints/t.MaxHitPoints:1f);
        mine.FailOnCannotTouch(TargetIndex.B,PathEndMode.Touch);
        mine.tickIntervalAction=delta=>
        {
            var rock=job.targetA.Thing;
            if(rock==null||rock.Destroyed){ReadyForNextToil();return;}
            if(ticksToHit<-100)ResetDelay();
            if(pawn.skills!=null&&(rock.Faction!=pawn.Faction||pawn.Faction==null))pawn.skills.Learn(SkillDefOf.Mining,.07f*delta);
            ticksToHit-=delta;if(ticksToHit>0)return;
            var at=rock.Position;Strike(this,rock,mine,pawn,at);
            if(rock.Destroyed)
            {
                Map.mineStrikeManager.CheckStruckOre(at,rock.def,pawn);pawn.records.Increment(RecordDefOf.CellsMined);
                if(pawn.Faction==Faction.OfPlayer&&MineStrikeManager.MineableIsVeryValuable(rock.def))TaleRecorder.RecordTale(TaleDefOf.MinedValuable,pawn,rock.def.building.mineableThing);
                if(pawn.Faction==Faction.OfPlayer&&MineStrikeManager.MineableIsValuable(rock.def)&&!Map.IsPlayerHome)TaleRecorder.RecordTale(TaleDefOf.CaravanRemoteMining,pawn,rock.def.building.mineableThing);
                if(job.targetB.Thing is Building_Elevator station)station.TickExtensions();
                LayerMapComponent.For(Map)?.Connectors.Invalidate();ReadyForNextToil();
            }
            else ResetDelay();
        };
        yield return mine;
    }
    private void ResetDelay()
    {float speed=pawn.GetStatValue(StatDefOf.MiningSpeed);if(pawn.Faction!=Faction.OfPlayer)speed=Math.Max(.6f,speed);ticksToHit=(int)Math.Round(100f/speed);}
}

[HarmonyPatch(typeof(Frame),nameof(Frame.GetInspectString))]
internal static class ShaftFrameInspection
{
    static void Postfix(Frame __instance,ref string __result)
    {if(VerticalMining.Ready(__instance))__result+="\n"+"RL_WaitingVerticalMine".Translate();}
}

[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.CanRemoveTopLayerAt))]
internal static class NaturalFloorRemoval
{
    static void Postfix(IntVec3 c,Map ___map,ref bool __result)
    {
        if(LayerMapComponent.For(___map)?.Mountains.IsNaturalFloor(c)==true&&___map.terrainGrid.TempTerrainAt(c)==null&&
            (___map.terrainGrid.UnderTerrainAt(c)==null||!___map.terrainGrid.TerrainAt(c).layerable))__result=false;
    }
}
[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.RemoveTopLayer))]
internal static class NaturalFloorRemovalCommit
{
    static bool Prefix(IntVec3 c,Map ___map)=>LayerMapComponent.For(___map)?.Mountains.IsNaturalFloor(c)!=true||___map.terrainGrid.TempTerrainAt(c)!=null||
        ___map.terrainGrid.UnderTerrainAt(c)!=null&&___map.terrainGrid.TerrainAt(c).layerable;
}
