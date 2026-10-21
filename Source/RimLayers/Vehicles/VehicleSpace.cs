using RimLayers.Core;
using Verse;

namespace RimLayers.Vehicles;

internal static class VehicleSpace
{
    internal static VehiclePose Pose(LayerMapComponent owner,Pawn vehicle,IntVec3 cell,double heading)
    {
        if(!owner.TryDecode(cell,out var logical))throw new ArgumentException("Position is outside a floor.");
        var occupied=VehicleCompatibility.Adapter!.OccupiedCells(vehicle,cell,heading).ToArray();
        // Native even-size anchoring differs between vertical and horizontal rotations. The
        // projected native rectangle supplies that centre without rotating or moving the real Pawn.
        double offsetX=(occupied.Min(c=>c.x)+occupied.Max(c=>c.x)+1)*.5-cell.x;
        double offsetZ=(occupied.Min(c=>c.z)+occupied.Max(c=>c.z)+1)*.5-cell.z;
        return new(new(logical.X+offsetX,owner.Elevation(logical.LayerId)*3,logical.Z+offsetZ),heading);
    }
    internal static bool CanTurn(LayerMapComponent owner,Pawn vehicle,IntVec3 cell,double fromHeading,double toHeading,bool ignoreVehicles=false)
    {
        if(VehicleCompatibility.Adapter is not {} adapter||!owner.TryDecode(cell,out var at))return false;
        if(!CanStand(owner,vehicle,cell,fromHeading,out _,ignoreVehicles)||!CanStand(owner,vehicle,cell,toHeading,out _,ignoreVehicles))return false;
        if(Math.Abs(VehicleGeometry.SignedTurn(fromHeading,toHeading))<1e-6)return true;
        var hull=adapter.Hull(vehicle);var from=Pose(owner,vehicle,cell,fromHeading);var to=Pose(owner,vehicle,cell,toHeading);
        return ClearMotion(owner,vehicle,at.LayerId,hull,from,to,ignoreVehicles);
    }
    internal static bool CanTravel(LayerMapComponent owner,Pawn vehicle,IntVec3 start,IntVec3 end,double heading,bool ignoreVehicles=false)
    {
        if(VehicleCompatibility.Adapter is not {} adapter||!owner.TryDecode(start,out var at)||!owner.TryDecode(end,out var target)||at.LayerId!=target.LayerId)return false;
        if(!CanStand(owner,vehicle,start,heading,out _,ignoreVehicles)||!CanStand(owner,vehicle,end,heading,out _,ignoreVehicles))return false;
        return ClearMotion(owner,vehicle,at.LayerId,adapter.Hull(vehicle),Pose(owner,vehicle,start,heading),Pose(owner,vehicle,end,heading),ignoreVehicles);
    }
    private static bool ClearMotion(LayerMapComponent owner,Pawn vehicle,int layerId,VehicleHull hull,VehiclePose from,VehiclePose to,bool ignoreVehicles)
    {
        double radius=Math.Sqrt(hull.Width*hull.Width+hull.Length*hull.Length)/2;
        var obstacles=new List<VehicleObstacle>();int key=1;
        double bottom=from.Position.Height,top=bottom+hull.Height;
        for(int z=(int)Math.Floor(Math.Min(from.Position.Z,to.Position.Z)-radius);z<(int)Math.Ceiling(Math.Max(from.Position.Z,to.Position.Z)+radius);z++)
            for(int x=(int)Math.Floor(Math.Min(from.Position.X,to.Position.X)-radius);x<(int)Math.Ceiling(Math.Max(from.Position.X,to.Position.X)+radius);x++)
            {
                var local=new RimLayers.Layout.LayerCell(layerId,x,z);
                if(!owner.Layout!.Contains(local))
                {obstacles.Add(new(x,bottom,z,x+1,top,z+1,key++));continue;}
                var storage=owner.ToStorage(local);
                var terrain=owner.map.terrainGrid.TerrainAt(storage);
                if(!owner.Structure.HasSurface(storage)||terrain.IsWater||terrain.passability==Traversability.Impassable)
                {obstacles.Add(new(x,bottom,z,x+1,top,z+1,key++));continue;}
                foreach(var thing in storage.GetThingList(owner.map))
                    if(thing!=vehicle&&thing is not Building_VehicleConnector&&!(ignoreVehicles&&VehicleCompatibility.Adapter!.IsVehicle(thing))&&(thing is Pawn||thing.def.passability==Traversability.Impassable))
                        obstacles.Add(new(x,bottom,z,x+1,bottom+VehicleGeometry.StoreyHeight,z+1,key++));
                for(int e=owner.Elevation(layerId);e*VehicleGeometry.StoreyHeight<top;e++)
                {
                    if(!owner.Layout.TryGetLayerAtElevation(e,out var layer))continue;
                    var above=owner.ToStorage(new(layer,x,z));
                    double plane=e*VehicleGeometry.StoreyHeight;
                    if(plane>bottom&&owner.Structure.HasSurface(above))obstacles.Add(new(x,plane,z,x+1,plane,z+1,key++));
                    if(owner.map.roofGrid.RoofAt(above)!=null)obstacles.Add(new(x,plane+3,z,x+1,plane+3,z+1,key++));
                }
            }
        // All inputs to the sweep are value snapshots; no live Map or Thing is read by the geometry kernel.
        return VehicleSweep.FirstHit(hull,from,to,obstacles)==null;
    }
    internal static bool CanStand(LayerMapComponent owner,Pawn vehicle,IntVec3 cell,double heading,out string reason,bool ignoreVehicles=false,Thing? ignoreThing=null)
    {
        reason="RL_VehicleNoSurface";
        if(!owner.TryDecode(cell,out var logical)||VehicleCompatibility.Adapter is not {} adapter)return false;
        var pose=Pose(owner,vehicle,cell,heading);var hull=adapter.Hull(vehicle);
        var occupied=VehicleGeometry.Footprint(hull,pose).ToHashSet();
        // Native footprints include their even-size anchoring and framework-specific hitbox rules.
        // The oriented hull adds corner/turn clearance; neither is allowed to under-check the other.
        foreach(var native in adapter.OccupiedCells(vehicle,cell,heading))
        {
            if(!owner.TryDecode(native,out var at)||at.LayerId!=logical.LayerId)return false;
            occupied.Add(new(at.X,at.Z));
        }
        foreach(var tile in occupied)
        {
            var at=new RimLayers.Layout.LayerCell(logical.LayerId,tile.X,tile.Z);
            if(!owner.Layout!.Contains(at))return false;
            var storage=owner.ToStorage(at);
            if(!owner.Structure.HasSurface(storage))return false;
            var terrain=owner.map.terrainGrid.TerrainAt(storage);
            if(terrain.IsWater||terrain.passability==Traversability.Impassable)return false;
            foreach(var thing in storage.GetThingList(owner.map))
            {
                if(thing==vehicle||thing==ignoreThing||thing is Building_VehicleConnector||ignoreVehicles&&adapter.IsVehicle(thing))continue;
                if(thing is Pawn||thing.def.passability==Traversability.Impassable)
                {reason="RL_VehicleOccupied";return false;}
            }
            // A roof at precisely the clearance height is contact, not penetration.
            for(int elevation=owner.Elevation(logical.LayerId);elevation*3<pose.Position.Height+hull.Height-1e-6;elevation++)
            {
                if(!owner.Layout.TryGetLayerAtElevation(elevation,out var layer))continue;
                var c=owner.ToStorage(new(layer,tile.X,tile.Z));
                if(elevation>owner.Elevation(logical.LayerId)&&owner.Structure.HasSurface(c))
                {reason="RL_VehicleClearance";return false;}
                if((elevation+1)*3<pose.Position.Height+hull.Height-1e-6&&owner.map.roofGrid.RoofAt(c)!=null)
                {reason="RL_VehicleClearance";return false;}
            }
        }
        reason="";return true;
    }
}
