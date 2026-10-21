using RimLayers.Core;
using Verse;
namespace RimLayers.Vehicles;

internal readonly record struct AirSurface(VehicleObstacle Bounds,IntVec3 Cell,Thing? Thing,bool Horizontal);
internal sealed record AirspaceSnapshot(long Revision,IReadOnlyList<AirSurface> Surfaces)
{
    internal VehicleSweepHit? FirstHit(VehicleHull hull,RampBody from,RampBody to)=>VehicleFlightSweep.FirstHit(hull,from,to,Surfaces.Select(s=>s.Bounds).ToArray());
}
internal static class VehicleAirspace
{
    internal static AirspaceSnapshot Snapshot(LayerMapComponent owner,Pawn vehicle,RampBody from,RampBody to,double margin=0)
    {
        var hull=VehicleCompatibility.Adapter!.Hull(vehicle);var surfaces=new List<AirSurface>();
        double radius=Math.Sqrt(hull.Width*hull.Width+hull.Length*hull.Length+hull.Height*hull.Height)+margin;
        double bottom=Math.Min(from.Bottom.Height,to.Bottom.Height)-radius,top=Math.Max(from.Bottom.Height,to.Bottom.Height)+radius;
        void Add(int x,double y,int z,double endY,IntVec3 cell,Thing? thing=null,bool horizontal=false)
            =>surfaces.Add(new(new(x,y,z,x+1,endY,z+1,surfaces.Count),cell,thing,horizontal));
        int width=owner.Layout!.Width,height=owner.Layout.Height;
        foreach(var wall in new[]{new VehicleObstacle(-radius,bottom,-radius,0,top,height+radius,0),new VehicleObstacle(width,bottom,-radius,width+radius,top,height+radius,1),
            new VehicleObstacle(0,bottom,-radius,width,top,0,2),new VehicleObstacle(0,bottom,height,width,top,height+radius,3)})
            surfaces.Add(new(wall,IntVec3.Invalid,null,false));
        for(int z=Math.Max(0,(int)Math.Floor(Math.Min(from.Bottom.Z,to.Bottom.Z)-radius));z<=Math.Min(height-1,Math.Ceiling(Math.Max(from.Bottom.Z,to.Bottom.Z)+radius));z++)
        for(int x=Math.Max(0,(int)Math.Floor(Math.Min(from.Bottom.X,to.Bottom.X)-radius));x<=Math.Min(width-1,Math.Ceiling(Math.Max(from.Bottom.X,to.Bottom.X)+radius));x++)
        {
            if(!owner.Layout!.Contains(new(0,x,z)))
            {Add(x,bottom,z,top,IntVec3.Invalid);continue;}
            for(int layer=0;layer<owner.Layout.LayerCount;layer++)
            {
                double floor=owner.Elevation(layer)*3;
                if(floor>top||floor+3<bottom)continue;
                var c=owner.ToStorage(new(layer,x,z));
                if(owner.Structure.HasSurface(c))Add(x,floor,z,floor,c,horizontal:true);
                if(owner.map.roofGrid.RoofAt(c)!=null)Add(x,floor+3,z,floor+3,c,horizontal:true);
                foreach(var t in c.GetThingList(owner.map))
                {
                    if(t==vehicle||!t.Spawned||t.Destroyed)continue;
                    if(t is Pawn p&&VehicleCompatibility.Flight?.Active(p)==true)continue; // airborne hulls are added below at their actual altitude
                    if(t is Pawn||t.def.passability==Traversability.Impassable||t.def.Fillage==FillCategory.Full)
                        Add(x,floor,z,floor+(t is Pawn?VehicleCompatibility.Adapter.IsVehicle(t)?VehicleCompatibility.Adapter.Hull((Pawn)t).Height:1.8:3),c,t);
                }
            }
        }
        // Dynamic aircraft cannot be represented by a floor-high occupied-cell proxy.
        if(VehicleCompatibility.Flight is {} adapter)
        foreach(var other in owner.map.mapPawns.AllPawnsSpawned.Where(p=>p!=vehicle&&VehicleCompatibility.Adapter!.IsVehicle(p)&&adapter.Active(p)))
        {
            var otherHeight=adapter.Height(other);if(otherHeight>top||otherHeight+VehicleCompatibility.Adapter!.Hull(other).Height<bottom)continue;
            owner.TryDecode(other.Position,out var at);var pose=VehicleSpace.Pose(owner,other,other.Position,VehicleCompatibility.Adapter!.Heading(other));
            foreach(var cell in VehicleGeometry.Footprint(VehicleCompatibility.Adapter.Hull(other),pose))
                Add(cell.X,otherHeight,cell.Z,otherHeight+VehicleCompatibility.Adapter.Hull(other).Height,other.Position,other);
        }
        return new(owner.Revision,surfaces);
    }
}
