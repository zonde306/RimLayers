using RimLayers.Core;
using RimLayers.Presentation;
using RimLayers.Recovery;
using UnityEngine;
using Verse;
namespace RimLayers.Vehicles;

public sealed partial class VehicleTraffic
{
    internal VehicleTravelState? ManualRamp(Pawn pawn,Vector3 point,double heading)
    {
        if(Trip(pawn) is {} existing)return existing.Manual?existing:null;
        var owner=LayerMapComponent.For(map)!;var adapter=VehicleCompatibility.Adapter!;
        owner.TryDecode(pawn.Position,out var at);var origin=owner.ToStorage(new(at.LayerId,0,0));
        double x=point.x-origin.x,z=point.z-origin.z;
        foreach(var edge in connectors.Where(c=>c.Spawned&&!c.IsLift).SelectMany(c=>VehicleRampService.Edges(this,pawn,c,new VehiclePoint(x,owner.Elevation(at.LayerId)*3,z),heading)))
        {
            if(!owner.TryDecode(edge.From,out var start)||start.LayerId!=at.LayerId||Math.Abs(VehicleGeometry.SignedTurn(heading,edge.Heading))>3)continue;
            var p0=VehicleSpace.Pose(owner,pawn,edge.From,edge.Heading).Position;
            var p1=VehicleSpace.Pose(owner,pawn,edge.To,edge.Heading).Position;
            double dx=p1.X-p0.X,dz=p1.Z-p0.Z,length=dx*dx+dz*dz;
            if(Math.Pow(x-p0.X,2)+Math.Pow(z-p0.Z,2)>.75*.75)continue;
            if(((x-p0.X)*dx+(z-p0.Z)*dz)<0)continue;
            var profile=VehicleRampService.Profile(owner,edge.Entry);double angle=profile.Yaw*Math.PI/180;
            var trip=new VehicleTravelState{Vehicle=pawn,Entry=edge.Entry,Exit=edge.Exit,EntryCell=edge.From,ExitCell=edge.To,
                Destination=edge.To,Heading=edge.Heading,Ramp=profile,TotalTicks=edge.Ticks,RemainingTicks=edge.Ticks,
                Manual=true,ManualFraction=Math.Clamp(((x-p0.X)*dx+(z-p0.Z)*dz)/length,0,1),
                ManualLateral=(x-p0.X)*Math.Cos(angle)-(z-p0.Z)*Math.Sin(angle),Phase=VehicleTravelPhase.Queued,
                JobId=pawn.CurJob?.loadID??-1,Claims=VehicleRoutePlanner.Claims(owner,edge,adapter.Hull(pawn))};
            if(!VehicleRampService.ModulesIntact(owner,trip))continue;
            trips.Add(trip);tripIndex[pawn.thingIDNumber]=trip;return trip;
        }
        return null;
    }
    internal bool AdmitManual(VehicleTravelState trip)
    {
        if(reservationConflict||!Reserve(trip))return false;
        trip.Phase=VehicleTravelPhase.Crossing;return true;
    }
    internal bool PublishManual(VehicleTravelState trip,ref Vector3 point)
    {
        var pawn=trip.Vehicle;var owner=LayerMapComponent.For(map)!;
        owner.TryDecode(pawn.Position,out var at);var origin=owner.ToStorage(new(at.LayerId,0,0));
        var p0=VehicleSpace.Pose(owner,pawn,trip.EntryCell,trip.Heading).Position;
        var p1=VehicleSpace.Pose(owner,pawn,trip.ExitCell,trip.Heading).Position;
        double dx=p1.X-p0.X,dz=p1.Z-p0.Z;
        double fraction=((point.x-origin.x-p0.X)*dx+(point.z-origin.z-p0.Z)*dz)/(dx*dx+dz*dz);
        // End exactly at a legal landing; the next native tick may steer again.
        fraction=Math.Clamp(fraction,0,1);
        var before=VehicleRampService.Body(owner,trip,trip.ManualFraction);var after=VehicleRampService.Body(owner,trip,fraction);
        bool clear=VehicleRampService.CanSweep(owner,pawn,trip.Ramp,before,after);
        if(clear){trip.ManualFraction=fraction;trip.RemainingTicks=(int)Math.Round(trip.TotalTicks*(1-fraction));}
        else after=before;
        point=new((float)after.Bottom.X+origin.x,point.y,(float)after.Bottom.Z+origin.z);
        return clear;
    }
    internal Vector3 FinishManualFrame(VehicleTravelState trip)
    {
        var owner=LayerMapComponent.For(map)!;var pawn=trip.Vehicle;var adapter=(FrameworkVehicleAdapter)VehicleCompatibility.Adapter!;
        var body=VehicleRampService.Body(owner,trip,trip.ManualFraction);
        owner.TryDecode(trip.EntryCell,out var from);owner.TryDecode(trip.ExitCell,out var to);
        int high=owner.Elevation(from.LayerId)>owner.Elevation(to.LayerId)?from.LayerId:to.LayerId;
        int low=high==from.LayerId?to.LayerId:from.LayerId;
        int layer=body.Bottom.Height>=owner.Elevation(high)*3-1e-5?high:low;
        var zero=VehicleSpace.Pose(owner,pawn,owner.ToStorage(new(layer,0,0)),trip.Heading).Position;
        var cell=owner.ToStorage(new(layer,(int)Math.Round(body.Bottom.X-zero.X),(int)Math.Round(body.Bottom.Z-zero.Z)));
        if(cell!=pawn.Position)Command(()=>adapter.CommitContinuous(pawn,cell,trip.Heading));
        RecoveryCompatibility.DeferFor(pawn,3);
        if(owner.ViewedLayer!=layer&&SteelManualAdapter.Active(pawn))LayerView.Switch(owner,layer);
        var origin=owner.ToStorage(new(layer,0,0));
        return new((float)body.Bottom.X+origin.x,pawn.def.Altitude,(float)body.Bottom.Z+origin.z);
    }
    internal void EndManualIfClear(VehicleTravelState trip,double previous)
    {
        if(trip.ManualFraction>=1||trip.ManualFraction<=0&&previous>0)
            Forget(trip);
    }
}
