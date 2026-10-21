using RimLayers.Core;
using RimLayers.Recovery;
using Verse;
using UnityEngine;
namespace RimLayers.Vehicles;

public sealed partial class VehicleTraffic
{
    internal bool ControlsPose(Pawn pawn)=>Trip(pawn)?.Phase==VehicleTravelPhase.Crossing;
    internal Vector3? DrawPosition(Pawn pawn)
    {
        var trip=Trip(pawn);var owner=LayerMapComponent.For(map);
        if(trip?.Phase!=VehicleTravelPhase.Crossing||owner==null||trip.TotalTicks<=0)return null;
        var body=VehicleRampService.Body(owner,trip,trip.Manual?trip.ManualFraction:1-(double)trip.RemainingTicks/trip.TotalTicks);
        owner.TryDecode(pawn.Position,out var at);var origin=owner.ToStorage(new(at.LayerId,0,0));
        return new((float)body.Bottom.X+origin.x,pawn.def.Altitude,(float)body.Bottom.Z+origin.z);
    }
    private void TickRamp(VehicleTravelState trip,LayerMapComponent owner)
    {
        var adapter=VehicleCompatibility.Adapter!;var pawn=trip.Vehicle;
        RecoveryCompatibility.DeferFor(pawn,3);
        if(!VehicleRampService.ModulesIntact(owner,trip))
        {
            // A missing deck is not permission to teleport through the gap or drop recovery's lease.
            Board(trip);ReturnToOrigin(trip);return;
        }
        int remaining=Math.Clamp(trip.RemainingTicks+(trip.AbortRequested?1:-1),0,trip.TotalTicks);
        var old=VehicleRampService.Body(owner,trip,trip.Manual?trip.ManualFraction:1-(double)trip.RemainingTicks/trip.TotalTicks);
        var next=VehicleRampService.Body(owner,trip,1-(double)remaining/trip.TotalTicks);
        if(!VehicleRampService.CanSweep(owner,pawn,trip.Ramp,old,next))return;
        double fraction=1-(double)remaining/trip.TotalTicks;
        owner.TryDecode(trip.EntryCell,out var entry);owner.TryDecode(trip.ExitCell,out var exit);
        int layer=next.Bottom.Height>=Math.Max(owner.Elevation(entry.LayerId),owner.Elevation(exit.LayerId))*3-1e-5?
            (owner.Elevation(entry.LayerId)>owner.Elevation(exit.LayerId)?entry.LayerId:exit.LayerId):
            (owner.Elevation(entry.LayerId)<owner.Elevation(exit.LayerId)?entry.LayerId:exit.LayerId);
        var zero=VehicleSpace.Pose(owner,pawn,owner.ToStorage(new(layer,0,0)),trip.Heading).Position;
        var cell=owner.ToStorage(new(layer,(int)Math.Round(next.Bottom.X-zero.X),(int)Math.Round(next.Bottom.Z-zero.Z)));
        if(cell!=pawn.Position)Command(()=>adapter.Commit(pawn,cell,trip.Heading));
        trip.RemainingTicks=remaining;
        if(remaining==0||trip.AbortRequested&&remaining==trip.TotalTicks)
        {
            var landing=trip.AbortRequested?trip.EntryCell:trip.ExitCell;
            if(!VehicleSpace.CanStand(owner,pawn,landing,trip.Heading,out _))return;
            Command(()=>adapter.Commit(pawn,landing,trip.Heading));
            if(trip.AbortRequested){Forget(trip);Command(()=>adapter.Fail(pawn));}else Continue(trip);
        }
    }
    private void Board(VehicleTravelState trip)
    {
        var pawn=trip.Vehicle;var oldBoarding=Boarding;Boarding=pawn;
        try
        {
            using var recovery=RecoveryCompatibility.BeginHandling(pawn);
            Command(()=>pawn.DeSpawn());
            if(!held.TryAdd(pawn,false))throw new InvalidOperationException("Vehicle custody refused an unspawned vehicle.");
            trip.Phase=VehicleTravelPhase.Held;
        }
        catch(Exception e)
        {
            if(!pawn.Spawned&&!held.Contains(pawn))held.TryAdd(pawn,false);
            ReturnToOrigin(trip);if(pawn.Spawned)Forget(trip);
            Log.Error("[RimLayers vehicles] Boarding failed; original vehicle retained: "+e);
        }
        finally{Boarding=oldBoarding;}
    }
}
