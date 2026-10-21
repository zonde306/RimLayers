using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Vehicles;

public sealed partial class VehicleTraffic
{
    private bool ShaftVacant(VehicleTravelState trip,LayerMapComponent owner)
    {
        if(!owner.TryDecode(trip.EntryCell,out var from)||!owner.TryDecode(trip.ExitCell,out var to))return false;
        int low=Math.Min(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));
        int high=Math.Max(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));
        for(int floor=low;floor<=high;floor++)
        {
            var center=owner.AtElevation(floor,from.X,from.Z);
            foreach(var cell in CellRect.CenteredOn(center,4))
                if(cell.GetThingList(map).Any(t=>t is Pawn&&t!=trip.Vehicle))return false;
        }
        return true;
    }
    internal IEnumerable<VehicleTravelState> ShaftTrips(Building_VehicleConnector station)
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null||!owner.TryDecode(station.Position,out var at))yield break;
        foreach(var t in trips)
            if(owner.TryDecode(t.EntryCell,out var entry)&&entry.X==at.X&&entry.Z==at.Z&&t.Entry?.IsLift!=false)yield return t;
    }
    internal bool RequestFloor(Pawn vehicle,Building_VehicleConnector target)
    {
        if(!vehicle.Spawned||vehicle.Map!=map||!target.Spawned||target.Map!=map||VehicleCompatibility.Adapter is not {} adapter||
            !adapter.CanMove(vehicle)||Trip(vehicle)!=null||VehicleRoutePlanner.FirstCrossing(this,vehicle,target.Position,PathEndMode.OnCell)==null)return false;
        SteelManualAdapter.Release(vehicle);
        return vehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,target.Position));
    }
    internal IEnumerable<Building_VehicleConnector> FloorDestinations(Pawn vehicle)
    {
        var owner=LayerMapComponent.For(map);var adapter=VehicleCompatibility.Adapter;
        if(owner==null||adapter==null||!vehicle.Spawned||vehicle.Map!=map||Trip(vehicle)!=null)yield break;
        var heading=adapter.Heading(vehicle);
        var occupied=adapter.OccupiedCells(vehicle,vehicle.Position,heading).ToArray();
        foreach(var source in connectors.Where(b=>b.Spawned&&b.IsLift&&!b.IsForbidden(vehicle)))
        {
            if(!occupied.All(source.OccupiedRect().Contains))continue;
            foreach(var target in connectors.Where(b=>b!=source&&b.Spawned&&b.IsLift&&!b.IsForbidden(vehicle)))
                if(VehicleRoutePlanner.ShaftIntact(this,owner,source,target,false)&&VehicleFacilitySpace.FitsCabin(owner,vehicle,target,heading))yield return target;
        }
    }
    internal IEnumerable<Gizmo> VehicleGizmos(Pawn vehicle)
    {
        if(vehicle.Faction!=Faction.OfPlayer||!vehicle.Spawned||LayerMapComponent.For(map) is not {} owner)yield break;
        if(Trip(vehicle) is {} trip)
        {
            yield return new Command_Action{defaultLabel="RL_VehicleCancel".Translate(),defaultDesc="RL_VehicleCancelHelp".Translate(),
                icon=TexCommand.ClearPrioritizedWork,action=()=>CancelOrder(vehicle)};
            yield break;
        }
        if(!FloorDestinations(vehicle).Any())yield break;
        var command=new Command_Action{defaultLabel="RL_VehicleChooseFloor".Translate(),defaultDesc="RL_VehicleChooseFloorHelp".Translate(),
            icon=TexCommand.Install,action=()=>
            {
                var choices=FloorDestinations(vehicle).OrderBy(s=>owner.Elevation(owner.TryDecode(s.Position,out var at)?at.LayerId:0))
                    .Select(s=>new FloatMenuOption(owner.LevelName(owner.TryDecode(s.Position,out var at)?at.LayerId:0),()=>
                    {if(!RequestFloor(vehicle,s))Messages.Message("RL_VehicleRouteLost".Translate(),vehicle,MessageTypeDefOf.RejectInput,false);})).ToList();
                if(choices.Count>0)Find.WindowStack.Add(new FloatMenu(choices));
            }};
        if(!VehicleCompatibility.Adapter!.CanMove(vehicle))command.Disable("RL_VehicleNotReady".Translate());
        yield return command;
    }
    internal void CancelOrder(Pawn vehicle)
    {
        var trip=Trip(vehicle);if(trip==null)return;
        bool atomic=held.Contains(vehicle)||trip.Phase==VehicleTravelPhase.Crossing;
        Cancel(vehicle);
        if(!atomic)Command(()=>VehicleCompatibility.Adapter!.Fail(vehicle));
    }
    internal int RecoveryCount=>trips.Count(t=>held.Contains(t.Vehicle)&&t.Phase==VehicleTravelPhase.Recovering);
    internal List<FloatMenuOption> RecoveryOptions()
    {
        var owner=LayerMapComponent.For(map)!;var options=new List<FloatMenuOption>();
        foreach(var trip in trips.Where(t=>held.Contains(t.Vehicle)&&t.Phase==VehicleTravelPhase.Recovering))
        {
            var saved=trip;
            bool clear=VehicleSpace.CanStand(owner,trip.Vehicle,trip.EntryCell,trip.Heading,out _);
            options.Add(new FloatMenuOption("RL_VehicleRecoverOriginal".Translate(trip.Vehicle.LabelShortCap),clear?()=>
            {
                if(held.Contains(saved.Vehicle)&&VehicleSpace.CanStand(owner,saved.Vehicle,saved.EntryCell,saved.Heading,out _))ReturnToOrigin(saved);
                else Messages.Message("RL_VehicleRecoverBlocked".Translate(),MessageTypeDefOf.RejectInput,false);
            }:null));
            options.Add(new FloatMenuOption("RL_VehicleLocateRecovery".Translate(trip.Vehicle.LabelShortCap),()=>
            {
                RimLayers.Presentation.LayerView.ShowTarget(saved.EntryCell,map);
                Find.CameraDriver.SetRootPosAndSize(saved.EntryCell.ToVector3Shifted(),12);
                Messages.Message("RL_VehicleRestoreSource".Translate(),MessageTypeDefOf.NeutralEvent,false);
            }));
        }
        return options;
    }
    internal void DrawRecoveryControl(UnityEngine.Rect rect)
    {
        if(RecoveryCount==0)return;
        if(Widgets.ButtonText(rect,"RL_VehicleRecoveryCount".Translate(RecoveryCount)))Find.WindowStack.Add(new FloatMenu(RecoveryOptions()));
        TooltipHandler.TipRegion(rect,"RL_VehicleRestoreSource".Translate());
    }
    internal string StationStatus(Building_VehicleConnector station)
    {
        var lines=new List<string>();
        foreach(var t in ShaftTrips(station).OrderBy(t=>t.QueueTicket))
        {
            string phase="RL_VehiclePhase"+t.Phase;
            var owner=LayerMapComponent.For(map)!;
            string target=owner.TryDecode(t.ExitCell,out var at)?owner.LevelName(at.LayerId):"?";
            lines.Add("RL_VehicleJourney".Translate(t.Vehicle.LabelShortCap,phase.Translate(),target));
            if(t.Phase==VehicleTravelPhase.Held&&!ShaftVacant(t,owner))lines.Add("RL_VehicleShaftOccupied".Translate());
        }
        return string.Join("\n",lines);
    }
}
