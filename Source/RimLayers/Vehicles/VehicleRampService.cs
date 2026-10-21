using RimLayers.Core;
using RimLayers.Layout;
using Verse;
using RimWorld;

namespace RimLayers.Vehicles;

internal static class VehicleRampService
{
    internal static IEnumerable<VehicleEdge> Edges(VehicleTraffic traffic,Pawn vehicle,Building_VehicleConnector ramp,VehiclePoint? near=null,double? facing=null)
    {
        var owner=LayerMapComponent.For(traffic.map)!;var adapter=VehicleCompatibility.Adapter!;
        if(!owner.TryDecode(ramp.Position,out var at)||!owner.Layout!.TryGetAdjacentLayer(at.LayerId,1,out var upper))yield break;
        var side=ramp.Rotation.IsHorizontal?new IntVec3(0,0,1):new IntVec3(1,0,0);
        bool Module(IntVec3 c)=>traffic.Connectors.Any(b=>b.Spawned&&!b.IsLift&&b.Rotation==ramp.Rotation&&b.Position==c&&!b.IsForbidden(vehicle));
        if(Module(ramp.Position-side))yield break; // One edge per contiguous side-by-side group.
        int count=1;while(Module(ramp.Position+side*count))count++;
        var hull=adapter.Hull(vehicle);if(hull.Width>count)yield break;
        var first=ramp.OccupiedRect();var last=GenAdj.OccupiedRect(ramp.Position+side*(count-1),ramp.Rotation,ramp.def.size);
        var rect=new CellRect(first.minX,first.minZ,last.maxX-first.minX+1,last.maxZ-first.minZ+1);
        var origin=owner.ToStorage(new(at.LayerId,0,0));
        double x=(rect.minX+rect.maxX+1)/2d-origin.x,z=(rect.minZ+rect.maxZ+1)/2d-origin.z;
        double yaw=ramp.Rotation.AsAngle,a=yaw*Math.PI/180,fx=Math.Sin(a),fz=Math.Cos(a);
        x-=fx*3;z-=fz*3;
        var profile=new VehicleRamp(x,z,yaw,owner.Elevation(at.LayerId)*3);
        double margin=hull.Length/2+1;
        IntVec3 Anchor(int layer,double px,double pz,double heading)
        {
            var zero=VehicleSpace.Pose(owner,vehicle,owner.ToStorage(new(layer,0,0)),heading).Position;
            return owner.ToStorage(new(layer,(int)Math.Round(px-zero.X),(int)Math.Round(pz-zero.Z)));
        }
        foreach(bool reverse in new[]{false,true})
        {
            double heading=(yaw+(reverse?180:0))%360;
            var lower=Anchor(at.LayerId,x-fx*margin,z-fz*margin,heading);
            var top=Anchor(upper,x+fx*(6+margin),z+fz*(6+margin),heading);
            var from=reverse?top:lower;var to=reverse?lower:top;
            var p0=VehicleSpace.Pose(owner,vehicle,from,heading).Position;var p1=VehicleSpace.Pose(owner,vehicle,to,heading).Position;
            if(near is {} point&&(Math.Abs(point.Height-p0.Height)>.1||Math.Pow(point.X-p0.X,2)+Math.Pow(point.Z-p0.Z,2)>.75*.75))continue;
            if(facing.HasValue&&Math.Abs(VehicleGeometry.SignedTurn(facing.Value,heading))>3)continue;
            if(!VehicleSpace.CanStand(owner,vehicle,lower,heading,out _,true)||!VehicleSpace.CanStand(owner,vehicle,top,heading,out _,true))continue;
            var b0=profile.Body(hull,p0.X,p0.Z);var b1=profile.Body(hull,p1.X,p1.Z);
            if(!CanSweep(owner,vehicle,profile,b0,b1,true))continue;
            int duration=(int)Math.Ceiling(Math.Sqrt(Math.Pow(p1.X-p0.X,2)+Math.Pow(p1.Z-p0.Z,2))*30);
            yield return new(ramp,ramp,from,to,heading,duration);
        }
    }
    internal static VehicleRamp Profile(LayerMapComponent owner,Building_VehicleConnector ramp)
    {
        owner.TryDecode(ramp.Position,out var at);var origin=owner.ToStorage(new(at.LayerId,0,0));var rect=ramp.OccupiedRect();
        double yaw=ramp.Rotation.AsAngle,a=yaw*Math.PI/180;
        return new((rect.minX+rect.maxX+1)/2d-origin.x-Math.Sin(a)*3,
                   (rect.minZ+rect.maxZ+1)/2d-origin.z-Math.Cos(a)*3,yaw,owner.Elevation(at.LayerId)*3);
    }
    internal static RampBody Body(LayerMapComponent owner,VehicleTravelState trip,double fraction)
    {
        var hull=VehicleCompatibility.Adapter!.Hull(trip.Vehicle);
        var p0=VehicleSpace.Pose(owner,trip.Vehicle,trip.EntryCell,trip.Heading).Position;
        var p1=VehicleSpace.Pose(owner,trip.Vehicle,trip.ExitCell,trip.Heading).Position;
        double a=trip.Ramp.Yaw*Math.PI/180;
        return trip.Ramp.Body(hull,p0.X+(p1.X-p0.X)*fraction+Math.Cos(a)*trip.ManualLateral,p0.Z+(p1.Z-p0.Z)*fraction-Math.Sin(a)*trip.ManualLateral);
    }
    internal static bool ModulesIntact(LayerMapComponent owner,VehicleTravelState trip)
    {
        if(trip.Entry is not {Spawned:true,IsLift:false})return false;
        var pose=Body(owner,trip,.5);var hull=VehicleCompatibility.Adapter!.Hull(trip.Vehicle);
        var footprint=VehicleGeometry.Footprint(hull,new(pose.Bottom,trip.Ramp.Yaw));
        if(!owner.Layout!.TryGetLayerAtElevation((int)Math.Round(trip.Ramp.BaseHeight/3),out var layer))return false;
        foreach(var tile in footprint)
        {
            double along=trip.Ramp.Along(tile.X+.5,tile.Z+.5);if(along<0||along>6)continue;
            var cell=owner.ToStorage(new(layer,tile.X,tile.Z));
            if(!cell.GetThingList(owner.map).OfType<Building_VehicleConnector>().Any(b=>!b.IsLift&&b.Rotation==trip.Entry.Rotation))return false;
        }
        return true;
    }
    internal static bool CanSweep(LayerMapComponent owner,Pawn vehicle,VehicleRamp profile,RampBody from,RampBody to,bool ignoreVehicles=false)
    {
        var hull=VehicleCompatibility.Adapter!.Hull(vehicle);
        var obstacles=Snapshot(owner,vehicle,profile,from,to,hull,ignoreVehicles);
        double distance=Math.Sqrt(Math.Pow(to.Bottom.X-from.Bottom.X,2)+Math.Pow(to.Bottom.Z-from.Bottom.Z,2));
        int steps=Math.Max(1,(int)Math.Ceiling(distance/.08));
        var previous=from;
        for(int i=1;i<=steps;i++)
        {
            double t=(double)i/steps;
            var next=profile.Body(hull,from.Bottom.X+(to.Bottom.X-from.Bottom.X)*t,from.Bottom.Z+(to.Bottom.Z-from.Bottom.Z)*t);
            if(obstacles.Any(o=>VehicleBodySweep.Intersects(hull,previous,next,o)))return false;
            previous=next;
        }
        return true;
    }
    private static List<VehicleObstacle> Snapshot(LayerMapComponent owner,Pawn vehicle,VehicleRamp profile,RampBody from,RampBody to,VehicleHull hull,bool ignoreVehicles)
    {
        var result=new List<VehicleObstacle>();int key=0;
        double radius=Math.Sqrt(hull.Width*hull.Width+hull.Length*hull.Length+hull.Height*hull.Height);
        for(int z=(int)Math.Floor(Math.Min(from.Bottom.Z,to.Bottom.Z)-radius);z<Math.Ceiling(Math.Max(from.Bottom.Z,to.Bottom.Z)+radius);z++)
        for(int x=(int)Math.Floor(Math.Min(from.Bottom.X,to.Bottom.X)-radius);x<Math.Ceiling(Math.Max(from.Bottom.X,to.Bottom.X)+radius);x++)
        {
            if(!owner.Layout!.Contains(new(0,x,z)))
            {result.Add(new(x,profile.BaseHeight-3,z,x+1,profile.BaseHeight+hull.Height+6,z+1,key++));continue;}
            double along=Math.Min(Math.Min(profile.Along(x,z),profile.Along(x+1,z)),Math.Min(profile.Along(x,z+1),profile.Along(x+1,z+1)));
            foreach(int layer in Enumerable.Range(0,owner.Layout.LayerCount))
            {
                double floor=owner.Elevation(layer)*3;
                if(floor+3<profile.BaseHeight||floor>profile.BaseHeight+hull.Height+6)continue;
                var cell=owner.ToStorage(new(layer,x,z));
                bool landing=along>=6-1e-6&&Math.Abs(floor-(profile.BaseHeight+3))<1e-6;
                if(floor>profile.BaseHeight&&!landing&&owner.Structure.HasSurface(cell))result.Add(new(x,floor,z,x+1,floor,z+1,key++));
                double roof=floor+3;
                if(roof>profile.BaseHeight&&!(along>=6-1e-6&&Math.Abs(roof-(profile.BaseHeight+3))<1e-6)&&owner.map.roofGrid.RoofAt(cell)!=null)
                    result.Add(new(x,roof,z,x+1,roof,z+1,key++));
                foreach(var thing in cell.GetThingList(owner.map))
                    if(thing!=vehicle&&thing is not Building_VehicleConnector&&!(ignoreVehicles&&VehicleCompatibility.Adapter!.IsVehicle(thing))&&(thing is Pawn||thing.def.passability==Traversability.Impassable))
                        result.Add(new(x,floor,z,x+1,floor+3,z+1,key++));
            }
        }
        return result;
    }
}
