using RimLayers.Core;
using Verse;
namespace RimLayers.Vehicles;
public sealed partial class VehicleTraffic
{
    internal bool AllowNativeTick(Pawn pawn,IntVec3 next)
    {
        var trip=Trip(pawn);
        if(trip?.Manual==true)return SteelManualAdapter.Active(pawn);
        if(trip?.Phase is VehicleTravelPhase.Crossing or VehicleTravelPhase.Queued or VehicleTravelPhase.Waiting)return false;
        if(VehicleCompatibility.Adapter is not {} adapter||LayerMapComponent.For(map) is not {} owner)return true;
        if(!next.IsValid||next==pawn.Position)return true;
        string? Claim(IntVec3 c)=>owner.TryDecode(c,out var at)?$"cell:{at.LayerId}:{at.X}:{at.Z}":null;
        bool Reserved(IntVec3 c)=>Claim(c) is {} claim&&reservations.ClaimedByOther(pawn.thingIDNumber,claim);
        var heading=adapter.Heading(pawn);
        // A vehicle already in a newly claimed area must remain able to drive out.
        bool inside=adapter.OccupiedCells(pawn,pawn.Position,heading).Any(Reserved);
        if(!inside&&adapter.OccupiedCells(pawn,next,heading).Any(Reserved))return false;
        if(trip?.Phase is not (VehicleTravelPhase.Approaching or VehicleTravelPhase.FinalLeg))return true;
        if(!owner.TryDecode(next,out var target)||!owner.TryDecode(pawn.Position,out var source)||source.LayerId!=target.LayerId)return false;
        double movingHeading=(Math.Atan2(next.x-pawn.Position.x,next.z-pawn.Position.z)*180/Math.PI+360)%360;
        bool clear=VehicleSpace.CanTurn(owner,pawn,pawn.Position,heading,movingHeading)&&VehicleSpace.CanTravel(owner,pawn,pawn.Position,next,movingHeading);
        int tick=Find.TickManager.TicksGame;
        if(clear&&(trip.LookAheadCell!=next||tick-trip.LookAheadTick>=15))
        {
            trip.LookAheadCell=next;trip.LookAheadTick=tick;
            int distance=(int)Math.Ceiling(adapter.Hull(pawn).Length*2+4);
            trip.LookAheadBlocked=!ClearFloorPath(pawn,((FrameworkVehicleAdapter)adapter).LookAhead(pawn,distance),pawn.Position,heading,out _,out _);
        }
        if(clear&&!trip.LookAheadBlocked)return true;
        // Stop before the swept hull reaches a dynamic obstruction. The native pathfinder owns the detour.
        if(Find.TickManager.TicksGame%60==pawn.thingIDNumber%60)
        {
            Command(()=>adapter.Stop(pawn));
            if(!StartClearLeg(trip)){if(trip.Phase==VehicleTravelPhase.Approaching)trip.Phase=VehicleTravelPhase.Queued;else trip.Waypoint=pawn.Position;}
        }
        return false;
    }
}
