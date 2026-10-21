using RimLayers.Core;
using Verse;
using Verse.AI;
namespace RimLayers.Vehicles;

public sealed partial class VehicleTraffic
{
    /// <summary>Native searches still own every floor leg. When their shortest path's
    /// rotated hull hits a new obstruction, try a bounded set of native two-leg detours.
    /// Only our intermediate waypoint is stored; native path objects never enter saves.</summary>
    private bool ClearFloorPath(Pawn pawn,IReadOnlyList<IntVec3>? path,IntVec3 from,double yaw,out double result,out string obstruction)
    {
        result=yaw;obstruction="";
        if(path==null){obstruction="native path missing";return false;}
        var owner=LayerMapComponent.For(map)!;
        foreach(var cell in path)
        {
            if(cell==from)continue;
            double next=(Math.Atan2(cell.x-from.x,cell.z-from.z)*180/Math.PI+360)%360;
            if(!VehicleSpace.CanTurn(owner,pawn,from,result,next)||!VehicleSpace.CanTravel(owner,pawn,from,cell,next))
            {obstruction=$"sweep {from}->{cell} yaw {result}->{next}";return false;}
            from=cell;result=next;
        }
        return true;
    }
    private int lastDetourTrace;
    private bool StartClearLeg(VehicleTravelState trip)
    {
        if(Find.TickManager.TicksGame<trip.NextRouteRetry)return false;
        var pawn=trip.Vehicle;var adapter=VehicleCompatibility.Adapter!;var owner=LayerMapComponent.For(map)!;
        bool final=trip.Phase==VehicleTravelPhase.FinalLeg;
        var goal=final?trip.Destination:new LocalTargetInfo(trip.EntryCell);
        var mode=final?trip.EndMode:PathEndMode.OnCell;
        if(!pawn.Spawned||!owner.TryDecode(pawn.Position,out var start)||!owner.TryDecode(goal.Cell,out var end)||start.LayerId!=end.LayerId)return false;
        long version=Version,revision=owner.Revision;
        bool trace=GenCommandLine.CommandLineArgPassed("rimlayersv1test")&&Find.TickManager.TicksGame-lastDetourTrace>300;
        string obstruction="";var diagnostics=new List<string>();
        bool Clear(IReadOnlyList<IntVec3>? path,IntVec3 from,double yaw,out double result)=>ClearFloorPath(pawn,path,from,yaw,out result,out obstruction);
        bool EndClear(IReadOnlyList<IntVec3>? path,IntVec3 from,double yaw)=>Clear(path,from,yaw,out var last)&&
            (final||VehicleSpace.CanTurn(owner,pawn,goal.Cell,last,trip.Heading));
        var direct=adapter.FindLeg(pawn,pawn.Position,goal,mode);
        if(EndClear(direct,pawn.Position,adapter.Heading(pawn)))
        {
            trip.Waypoint=IntVec3.Invalid;
            if(version!=Version||revision!=owner.Revision)return false;
            trip.LookAheadCell=IntVec3.Invalid;trip.LookAheadBlocked=false;
            Command(()=>VehicleCompatibility.Resume(pawn,goal,mode));return true;
        }
        var directions=new[]{new IntVec3(0,0,1),new IntVec3(1,0,1),new IntVec3(1,0,0),new IntVec3(1,0,-1),
            new IntVec3(0,0,-1),new IntVec3(-1,0,-1),new IntVec3(-1,0,0),new IntVec3(-1,0,1)};
        foreach(var radius in new[]{3,6,9,12})
        foreach(var waypoint in directions.Select(d=>pawn.Position+d*radius).OrderBy(c=>c.DistanceToSquared(goal.Cell)))
        {
            if(!owner.TryDecode(waypoint,out var at)||at.LayerId!=start.LayerId||waypoint==goal.Cell)continue;
            var first=adapter.FindLeg(pawn,pawn.Position,waypoint,PathEndMode.OnCell);
            if(!Clear(first,pawn.Position,adapter.Heading(pawn),out var arrival)){if(trace)diagnostics.Add($"{waypoint} first {obstruction}");continue;}
            var second=adapter.FindLeg(pawn,waypoint,goal,mode);
            if(!EndClear(second,waypoint,arrival)){if(trace)diagnostics.Add($"{waypoint} second {obstruction}");continue;}
            if(version!=Version||revision!=owner.Revision)return false;
            trip.LookAheadCell=IntVec3.Invalid;trip.LookAheadBlocked=false;
            trip.Waypoint=waypoint;Command(()=>adapter.StartLeg(pawn,waypoint));return true;
        }
        trip.NextRouteRetry=Find.TickManager.TicksGame+180;
        if(trace){lastDetourTrace=Find.TickManager.TicksGame;Log.Message($"[RimLayers.Detour] {pawn.ThingID} {pawn.Position}->{goal} {trip.Phase}: "+string.Join("; ",diagnostics));}
        return false;
    }
}
