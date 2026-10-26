using RimLayers.Core;
using RimLayers.Underground;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Vehicles;

// A deliberate mining order on an existing station. The order converts whatever covers the
// upper cabin of the shaft into the retractable deck and clears the roof beneath it, in
// either direction. It never creates a level and never refunds the removed structure.
public sealed partial class Building_VehicleConnector
{
    internal bool ExcavatingShaft;
    private int shaftDirection=-1;
    private float shaftWork;
    internal int ShaftDirection=>shaftDirection;
    internal float ShaftWork=>shaftWork;
    internal const float ShaftCellWork=600;
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref ExcavatingShaft,"excavatingVehicleShaft");
        Scribe_Values.Look(ref shaftWork,"vehicleShaftWork");
        Scribe_Values.Look(ref shaftDirection,"vehicleShaftDirection",-1);
    }
    internal Building_VehicleConnector? ShaftPartner(int direction)
    {
        if(direction is not (-1 or 1))return null;
        if(!Spawned||!IsLift||LayerMapComponent.For(Map) is not {} owner||!owner.TryDecode(Position,out var at))return null;
        if(!owner.Layout!.TryGetAdjacentLayer(at.LayerId,direction,out _))return null;
        var other=owner.Adjacent(Position,direction);
        if(!other.IsValid)return null;
        return Traffic.Connectors.FirstOrDefault(s=>s.Spawned&&s.IsLift&&s.Position==other&&s.Faction==Faction);
    }
    internal Building_VehicleConnector? LowerShaftStation=>ShaftPartner(-1);
    // The deck always belongs to the upper cabin of the pair, whichever side ordered the work.
    internal CellRect ShaftDeckRect(int direction)
    {
        if(direction is not (-1 or 1))return default;
        if(!Spawned||!IsLift||LayerMapComponent.For(Map) is not {} owner||!owner.TryDecode(Position,out var at))return default;
        if(!owner.Layout!.TryGetAdjacentLayer(at.LayerId,direction,out _))return default;
        var rect=this.OccupiedRect();
        if(direction<0)return rect;
        var min=owner.Adjacent(new IntVec3(rect.minX,0,rect.minZ),direction);
        return min.IsValid?new CellRect(min.x,min.z,rect.Width,rect.Height):default;
    }
    internal bool ShaftCellOpen(IntVec3 cell)
    {
        var owner=LayerMapComponent.For(Map)!;var lower=owner.Adjacent(cell,-1);
        return lower.IsValid&&Map.roofGrid.RoofAt(lower)==null&&Map.terrainGrid.TerrainAt(cell).defName=="RL_VehicleDeck";
    }
    // Natural earth has to be mined; a constructed slab or floor is dismantled far faster.
    internal float ShaftWorkAt(IntVec3 cell)
    {
        if(!cell.IsValid)return ShaftCellWork;
        var terrain=Map.terrainGrid.TerrainAt(cell);
        return terrain!=null&&(terrain.natural||terrain.affordances.Any(a=>a.defName=="Diggable"))?ShaftCellWork:ShaftCellWork*.2f;
    }
    internal IntVec3 NextShaftCell
    {
        get{foreach(var c in ShaftDeckRect(shaftDirection))if(!ShaftCellOpen(c))return c;return IntVec3.Invalid;}
    }
    internal AcceptanceReport ShaftExcavationConditions(Pawn? worker=null)=>ShaftConditions(shaftDirection,worker);
    internal AcceptanceReport ShaftConditions(int direction,Pawn? worker=null)
    {
        if(direction is not (-1 or 1))return "RL_VehicleDigNoLevel".Translate();
        if(!Spawned||!IsLift||LayerMapComponent.For(Map) is not {} owner||!owner.TryDecode(Position,out var at))return "RL_VehicleDigNeedStation".Translate();
        var deck=ShaftDeckRect(direction);
        if(deck.Area==0)return "RL_VehicleDigNoLevel".Translate();
        var partner=ShaftPartner(direction);
        // Opening the floor away from an existing station is allowed upward only; downward
        // the shaft must land on a real cabin instead of solid earth.
        if(direction<0&&partner==null)return "RL_VehicleDigNeedStation".Translate();
        if(Traffic.ShaftTrips(this).Any())return "RL_VehicleDigBusy".Translate();
        if(!VehicleFacilitySpace.DeckSupported(owner,deck))return "RL_VehicleDeckSupport".Translate();
        if(partner!=null&&!VehicleFacilitySpace.DeckSupported(owner,partner.OccupiedRect()))return "RL_VehicleDeckSupport".Translate();
        var cabins=new List<IntVec3>(this.OccupiedRect());
        if(partner!=null)cabins.AddRange(partner.OccupiedRect());
        if(cabins.Any(cell=>cell.GetThingList(Map).Any(t=>t!=this&&t!=partner&&t!=worker&&
            (VehicleCompatibility.Adapter?.IsVehicle(t)==true||t is Blueprint||t is Frame||t is not Pawn&&t.def.passability==Traversability.Impassable))))
            return "RL_VehicleDigClear".Translate();
        foreach(var cell in deck)
        {
            // The receiving cabin keeps its own floor; only the roof over it has to go.
            var floor=owner.Adjacent(cell,-1);
            if(!floor.IsValid||!owner.Structure.HasSurface(floor))return "RL_VehicleNoSurface".Translate();
            var lower=Map.terrainGrid.TerrainAt(floor);
            if(lower.IsWater||lower.passability==Traversability.Impassable)return "RL_VehicleNoSurface".Translate();
            // A roof over the ordering cabin is the ceiling that cabin rises through, so this
            // order owns it. A roof over the receiving cabin below belongs to another room.
            if(direction<0&&Map.roofGrid.RoofAt(floor) is {} roof&&roof!=RoofDefOf.RoofRockThick&&roof!=RoofDefOf.RoofRockThin)
                return "RL_VehicleDigRoof".Translate();
            // A foundation carries its own deck; replacing it would silently delete a substructure.
            if(Map.terrainGrid.FoundationAt(cell)!=null)return "RL_VehicleDigNatural".Translate();
            var terrain=Map.terrainGrid.TerrainAt(cell);
            if(terrain.defName=="RL_VehicleDeck")continue;
            if(terrain.IsWater||terrain.passability==Traversability.Impassable)return "RL_VehicleDigNatural".Translate();
            // Only breaking into real earth is limited by the underground rules. An artificial
            // upper floor is a covering layer that this order is allowed to replace.
            if(direction<0&&owner.Elevation(at.LayerId)<=0)
            {
                var report=UndergroundService.TerrainAllows(Map,cell);
                if(!report.Accepted)return report;
            }
        }
        if(deck.All(ShaftCellOpen))return "RL_VehicleDigComplete".Translate();
        return true;
    }
    internal bool StartShaftExcavation(int direction)
    {
        var report=ShaftConditions(direction);
        if(!report.Accepted){Messages.Message(report.Reason,this,MessageTypeDefOf.RejectInput,false);return false;}
        ExcavatingShaft=true;shaftDirection=direction;shaftWork=0;return true;
    }
    internal bool StartShaftExcavation()=>StartShaftExcavation(shaftDirection);
    internal void CancelShaftExcavation(){ExcavatingShaft=false;}
    internal bool WorkShaft(Pawn worker,float work)
    {
        if(!ExcavatingShaft)return false;
        var cell=NextShaftCell;
        if(!cell.IsValid){ExcavatingShaft=false;shaftWork=0;return true;}
        // Revalidate before the cell is committed, not only when the order was accepted.
        if(!ShaftConditions(shaftDirection,worker).Accepted)return false;
        shaftWork+=work;
        if(shaftWork<ShaftWorkAt(cell))return true;
        var owner=LayerMapComponent.For(Map)!;var below=owner.Adjacent(cell,-1);
        // Drop the covering slab from the structural set before its roof disappears so the
        // new opening is not queued as a collapse behind the miner.
        owner.StructuralCells.Remove(cell);
        Map.roofGrid.SetRoof(below,null);
        Map.terrainGrid.SetTerrain(cell,DefDatabase<TerrainDef>.GetNamed("RL_VehicleDeck"));
        Map.fogGrid.Unfog(below);
        Map.fogGrid.Unfog(cell);
        shaftWork=0;
        if(!NextShaftCell.IsValid){ExcavatingShaft=false;return true;}
        return ShaftConditions(shaftDirection,worker).Accepted;
    }
    private IEnumerable<Gizmo> ShaftExcavationGizmos()
    {
        if(!Spawned||!IsLift)yield break;
        if(ExcavatingShaft)
        {
            yield return new Command_Action{
                defaultLabel="RL_VehicleDigCancel".Translate(),
                defaultDesc=(shaftDirection<0?"RL_VehicleDigHelp":"RL_VehicleDigHelpUp").Translate(),icon=TexCommand.Install,
                action=CancelShaftExcavation};
            yield break;
        }
        foreach(int direction in new[]{-1,1})
        {
            var deck=ShaftDeckRect(direction);
            if(deck.Area==0)continue;
            // A finished shaft needs no order; the inspect string already reports it.
            if(deck.All(ShaftCellOpen))continue;
            var command=new Command_Action{
                defaultLabel=(direction<0?"RL_VehicleDigStart":"RL_VehicleDigStartUp").Translate(),
                defaultDesc=(direction<0?"RL_VehicleDigHelp":"RL_VehicleDigHelpUp").Translate(),icon=TexCommand.Install,
                action=()=>StartShaftExcavation(direction)};
            if(ShaftConditions(direction) is var report&&!report.Accepted)command.Disable(report.Reason);
            yield return command;
        }
    }
    private string ShaftExcavationStatus()
    {
        if(!Spawned||!IsLift)return "";
        var deck=ShaftDeckRect(shaftDirection);
        if(deck.Area==0)return "";
        int completed=deck.Count(ShaftCellOpen);
        // Silence for an untouched station: the two order buttons already carry the reason.
        if(!ExcavatingShaft&&shaftWork<=0f&&completed==0)return "";
        if(completed==deck.Area)return (shaftDirection<0?"RL_VehicleDigComplete":"RL_VehicleDigCompleteUp").Translate();
        var status=(shaftDirection<0?"RL_VehicleDigProgress":"RL_VehicleDigProgressUp").Translate(completed,deck.Area).ToString();
        if(ExcavatingShaft&&ShaftConditions(shaftDirection) is var report&&!report.Accepted)status+="\n"+report.Reason;
        return status;
    }
    public override void DrawExtraSelectionOverlays()
    {
        base.DrawExtraSelectionOverlays();
        if(!Spawned||!IsLift)return;
        if(LayerMapComponent.For(Map) is not {} owner||!owner.TryDecode(Position,out var at))return;
        foreach(int direction in new[]{-1,1})
        {
            if(direction!=shaftDirection&&ShaftPartner(direction)==null)continue;
            var deck=ShaftDeckRect(direction);
            if(deck.Area==0||!owner.TryDecode(deck.Min,out var deckAt)||deckAt.LayerId!=at.LayerId)continue;
            var closed=deck.Where(c=>!ShaftCellOpen(c)).ToList();
            if(closed.Count>0)GenDraw.DrawFieldEdges(closed,UnityEngine.Color.yellow);
        }
    }
}

public sealed class WorkGiver_VehicleShaft:WorkGiver_Scanner
{
    public override PathEndMode PathEndMode=>PathEndMode.Touch;
    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)=>pawn.Map.GetComponent<VehicleTraffic>().Connectors.Where(s=>s.ExcavatingShaft);
    public override bool HasJobOnThing(Pawn pawn,Thing t,bool forced=false)
    {
        if(t is not Building_VehicleConnector s||!s.ExcavatingShaft||s.Faction!=pawn.Faction)return false;
        if(s.IsForbidden(pawn)||!s.ShaftConditions(s.ShaftDirection,pawn).Accepted)return false;
        if(s.ShaftPartner(s.ShaftDirection) is {} end&&(end.IsForbidden(pawn)||!pawn.CanReserve(end)))return false;
        return pawn.CanReserveAndReach(s,PathEndMode.Touch,Danger.Deadly);
    }
    public override Job? JobOnThing(Pawn pawn,Thing t,bool forced=false)
    {
        if(!HasJobOnThing(pawn,t,forced))return null;
        var station=(Building_VehicleConnector)t;
        var job=JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("RL_ExcavateVehicleShaft"),t);
        // Upward orders may open a floor before the upper station exists; then the source
        // cabin is the only reserved target.
        if(station.ShaftPartner(station.ShaftDirection) is {} end)job.targetB=end;
        return job;
    }
}
public sealed class JobDriver_VehicleShaft:JobDriver
{
    public override bool TryMakePreToilReservations(bool errorOnFailed)=>pawn.Reserve(job.targetA,job,1,-1,null,errorOnFailed)&&
        (!job.targetB.HasThing||pawn.Reserve(job.targetB,job,1,-1,null,errorOnFailed));
    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
        if(job.targetB.HasThing)this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
        this.FailOn(()=>job.targetA.Thing is not Building_VehicleConnector s||!s.ExcavatingShaft||
            job.targetB.HasThing&&s.ShaftPartner(s.ShaftDirection)!=job.targetB.Thing||
            !s.ShaftConditions(s.ShaftDirection,pawn).Accepted);
        yield return Toils_Goto.GotoThing(TargetIndex.A,PathEndMode.Touch);
        var toil=ToilMaker.MakeToil("RimLayers_ExcavateVehicleShaft");
        toil.defaultCompleteMode=ToilCompleteMode.Never;toil.activeSkill=()=>SkillDefOf.Mining;
        toil.FailOnCannotTouch(TargetIndex.A,PathEndMode.Touch);
        toil.WithProgressBar(TargetIndex.A,()=>{var s=(Building_VehicleConnector)job.targetA.Thing;return s.ShaftWork/s.ShaftWorkAt(s.NextShaftCell);});
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
