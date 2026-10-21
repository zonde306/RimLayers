using RimLayers.Core;
using RimLayers.Underground;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Vehicles;

// A deliberate mining order on an existing reinforced station. Never creates a level,
// destroys a rock Thing, or removes a player slab as a side effect of building a lift.
public sealed partial class Building_VehicleConnector
{
    internal bool ExcavatingShaft;
    private float shaftWork;
    internal float ShaftWork=>shaftWork;
    internal const float ShaftCellWork=600;
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref ExcavatingShaft,"excavatingVehicleShaft");
        Scribe_Values.Look(ref shaftWork,"vehicleShaftWork");
    }
    internal Building_VehicleConnector? LowerShaftStation
    {
        get
        {
            if(!Spawned||!IsLift||LayerMapComponent.For(Map) is not {} owner||!owner.TryDecode(Position,out var at)||owner.Elevation(at.LayerId)>0)return null;
            var lower=owner.Adjacent(Position,-1);
            return Traffic.Connectors.FirstOrDefault(s=>s.Spawned&&s.IsLift&&s.Position==lower&&s.Faction==Faction);
        }
    }
    internal bool ShaftCellOpen(IntVec3 cell)
    {
        var owner=LayerMapComponent.For(Map)!;var lower=owner.Adjacent(cell,-1);
        return lower.IsValid&&Map.roofGrid.RoofAt(lower)==null&&Map.terrainGrid.TerrainAt(cell).defName=="RL_VehicleDeck";
    }
    internal AcceptanceReport ShaftExcavationConditions(Pawn? worker=null)
    {
        if(LowerShaftStation is not {} lower||LayerMapComponent.For(Map) is not {} owner)return "RL_VehicleDigNeedStation".Translate();
        if(Traffic.ShaftTrips(this).Any())return "RL_VehicleDigBusy".Translate();
        if(!VehicleFacilitySpace.DeckSupported(owner,this.OccupiedRect())||!VehicleFacilitySpace.DeckSupported(owner,lower.OccupiedRect()))return "RL_VehicleDeckSupport".Translate();
        foreach(var station in new[]{this,lower})foreach(var cell in station.OccupiedRect())
        {
            if(cell.GetThingList(Map).Any(t=>t!=station&&t!=worker&&(VehicleCompatibility.Adapter?.IsVehicle(t)==true||t is Blueprint||t is Frame||t is not Pawn&&t.def.passability==Traversability.Impassable)))return "RL_VehicleDigClear".Translate();
            var terrain=Map.terrainGrid.TerrainAt(cell);
            // Only the upper floor is excavated. Paving the receiving cabin is harmless.
            if(station==lower)
            {
                if(!owner.Structure.HasSurface(cell)||terrain.IsWater||terrain.passability==Traversability.Impassable)return "RL_VehicleNoSurface".Translate();
                continue;
            }
            var roof=Map.roofGrid.RoofAt(owner.Adjacent(cell,-1));
            if(roof!=null&&roof!=RoofDefOf.RoofRockThick&&roof!=RoofDefOf.RoofRockThin)return "RL_VehicleDigRoof".Translate();
            if(Map.terrainGrid.FoundationAt(cell)!=null||Map.terrainGrid.TempTerrainAt(cell)!=null||owner.StructuralCells.Contains(cell))return "RL_VehicleDigNatural".Translate();
            if(terrain.defName=="RL_VehicleDeck")continue;
            if(!UndergroundService.DryLand(terrain)||Map.terrainGrid.UnderTerrainAt(cell)!=null||!VehicleFacilitySpace.NativeSurface(owner,cell))return "RL_VehicleDigNatural".Translate();
            if(station==this&&!ShaftCellOpen(cell))
            {
                var report=UndergroundService.TerrainAllows(Map,cell);if(!report.Accepted)return report;
            }
        }
        if(this.OccupiedRect().All(ShaftCellOpen))return "RL_VehicleDigComplete".Translate();
        return true;
    }
    internal bool StartShaftExcavation()
    {
        var report=ShaftExcavationConditions();
        if(!report.Accepted){Messages.Message(report.Reason,this,MessageTypeDefOf.RejectInput,false);return false;}
        ExcavatingShaft=true;return true;
    }
    internal void CancelShaftExcavation(){ExcavatingShaft=false;}
    internal bool WorkShaft(Pawn worker,float work)
    {
        if(!ExcavatingShaft||!ShaftExcavationConditions(worker).Accepted)return false;
        shaftWork+=work;
        while(shaftWork>=ShaftCellWork)
        {
            // Revalidate at commit, not just when the work order was accepted.
            if(!ShaftExcavationConditions(worker).Accepted)return false;
            var cell=this.OccupiedRect().First(c=>!ShaftCellOpen(c));
            var owner=LayerMapComponent.For(Map)!;var below=owner.Adjacent(cell,-1);
            Map.terrainGrid.SetTerrain(cell,DefDatabase<TerrainDef>.GetNamed("RL_VehicleDeck"));
            Map.roofGrid.SetRoof(below,null);
            Map.fogGrid.Unfog(below);
            shaftWork-=ShaftCellWork;
            if(this.OccupiedRect().All(ShaftCellOpen)){ExcavatingShaft=false;shaftWork=0;break;}
        }
        return true;
    }
    private IEnumerable<Gizmo> ShaftExcavationGizmos()
    {
        var owner=LayerMapComponent.For(Map)!;
        if(!owner.TryDecode(Position,out var at)||owner.Elevation(at.LayerId)>0)yield break;
        bool cancel=ExcavatingShaft;
        var command=new Command_Action{
            defaultLabel=(cancel?"RL_VehicleDigCancel":"RL_VehicleDigStart").Translate(),
            defaultDesc="RL_VehicleDigHelp".Translate(),icon=TexCommand.Install,
            action=()=>{if(cancel)CancelShaftExcavation();else StartShaftExcavation();}};
        if(!cancel&&ShaftExcavationConditions() is var report&&!report.Accepted)command.Disable(report.Reason);
        yield return command;
    }
    private string ShaftExcavationStatus()
    {
        if(LowerShaftStation==null)return "";
        int completed=this.OccupiedRect().Count(ShaftCellOpen);
        if(completed==81)return "RL_VehicleDigComplete".Translate();
        var status="RL_VehicleDigProgress".Translate(completed,81).ToString();
        if(ExcavatingShaft&&!ShaftExcavationConditions().Accepted)status+="\n"+ShaftExcavationConditions().Reason;
        return status;
    }
    public override void DrawExtraSelectionOverlays()
    {
        base.DrawExtraSelectionOverlays();
        if(!Spawned||!IsLift||LowerShaftStation==null)return;
        GenDraw.DrawFieldEdges(this.OccupiedRect().Where(c=>!ShaftCellOpen(c)).ToList(),UnityEngine.Color.yellow);
    }
}

public sealed class WorkGiver_VehicleShaft:WorkGiver_Scanner
{
    public override PathEndMode PathEndMode=>PathEndMode.Touch;
    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)=>pawn.Map.GetComponent<VehicleTraffic>().Connectors.Where(s=>s.ExcavatingShaft);
    public override bool HasJobOnThing(Pawn pawn,Thing t,bool forced=false)=>t is Building_VehicleConnector s&&s.ExcavatingShaft&&s.Faction==pawn.Faction&&
        !s.IsForbidden(pawn)&&s.ShaftExcavationConditions(pawn).Accepted&&pawn.CanReserveAndReach(s,PathEndMode.Touch,Danger.Deadly)&&
        s.LowerShaftStation is {} lower&&!lower.IsForbidden(pawn)&&pawn.CanReserve(lower);
    public override Job? JobOnThing(Pawn pawn,Thing t,bool forced=false)=>HasJobOnThing(pawn,t,forced)?JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("RL_ExcavateVehicleShaft"),t,((Building_VehicleConnector)t).LowerShaftStation):null;
}
public sealed class JobDriver_VehicleShaft:JobDriver
{
    public override bool TryMakePreToilReservations(bool errorOnFailed)=>pawn.Reserve(job.targetA,job,1,-1,null,errorOnFailed)&&pawn.Reserve(job.targetB,job,1,-1,null,errorOnFailed);
    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.A);this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
        this.FailOn(()=>job.targetA.Thing is not Building_VehicleConnector s||!s.ExcavatingShaft||s.LowerShaftStation!=job.targetB.Thing||!s.ShaftExcavationConditions(pawn).Accepted);
        yield return Toils_Goto.GotoThing(TargetIndex.A,PathEndMode.Touch);
        var toil=ToilMaker.MakeToil("RimLayers_ExcavateVehicleShaft");
        toil.defaultCompleteMode=ToilCompleteMode.Never;toil.activeSkill=()=>SkillDefOf.Mining;
        toil.FailOnCannotTouch(TargetIndex.A,PathEndMode.Touch);
        toil.WithProgressBar(TargetIndex.A,()=>((Building_VehicleConnector)job.targetA.Thing).ShaftWork/Building_VehicleConnector.ShaftCellWork);
        toil.WithEffect(EffecterDefOf.Mine,TargetIndex.A);
        toil.tickIntervalAction=delta=>
        {
            var station=(Building_VehicleConnector)job.targetA.Thing;
            if(!station.WorkShaft(pawn,pawn.GetStatValue(StatDefOf.MiningSpeed)*delta)){EndJobWith(JobCondition.Incompletable);return;}
            pawn.skills?.Learn(SkillDefOf.Mining,.07f*delta);
            if(!station.ExcavatingShaft)ReadyForNextToil();
        };
        yield return toil;
    }
}
