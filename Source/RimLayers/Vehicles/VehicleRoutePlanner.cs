using RimWorld;
using RimLayers.Core;
using RimLayers.Navigation;
using Verse;
using Verse.AI;

namespace RimLayers.Vehicles;

internal sealed record VehicleEdge(Building_VehicleConnector Entry,Building_VehicleConnector Exit,IntVec3 From,IntVec3 To,double Heading,int Ticks);
internal static class VehicleRoutePlanner
{
    internal static List<VehicleEdge> Edges(VehicleTraffic traffic,Pawn vehicle)
    {
        var result=new List<VehicleEdge>();
        var owner=LayerMapComponent.For(traffic.map);var adapter=VehicleCompatibility.Adapter;
        if(owner==null||adapter==null)return result;
        var hull=adapter.Hull(vehicle);
        foreach(var entry in traffic.Connectors.Where(b=>b.Spawned&&!b.IsForbidden(vehicle)).OrderBy(b=>b.thingIDNumber))
        {
            if(!owner.TryDecode(entry.Position,out var at))continue;
            if(entry.IsLift)
            {
                if(!entry.Powered)continue;
                foreach(var exit in traffic.Connectors.Where(b=>b!=entry&&b.IsLift&&b.Spawned&&!b.IsForbidden(vehicle)))
                {
                    if(!owner.TryDecode(exit.Position,out var dest)||dest.X!=at.X||dest.Z!=at.Z||!ShaftIntact(traffic,owner,entry,exit,true))continue;
                    foreach(double heading in new[]{adapter.Heading(vehicle)}.Concat(Enumerable.Range(0,8).Select(i=>(double)i*45)).Distinct())
                    if(VehicleFacilitySpace.FitsCabin(owner,vehicle,entry,heading)&&VehicleFacilitySpace.FitsCabin(owner,vehicle,exit,heading)&&VehicleSpace.CanStand(owner,vehicle,entry.Position,heading,out _,true)&&VehicleSpace.CanStand(owner,vehicle,exit.Position,heading,out _,true))
                        result.Add(new(entry,exit,entry.Position,exit.Position,heading,60+owner.Layout!.Distance(at.LayerId,dest.LayerId)*90));
                }
                continue;
            }
            result.AddRange(VehicleRampService.Edges(traffic,vehicle,entry));
        }
        return result;
    }
    internal static bool ShaftIntact(VehicleTraffic traffic,LayerMapComponent owner,Building_VehicleConnector a,Building_VehicleConnector b,bool powered)
    {
        if(!a.Spawned||!b.Spawned||!owner.TryDecode(a.Position,out var from)||!owner.TryDecode(b.Position,out var to)||from.X!=to.X||from.Z!=to.Z)return false;
        for(int e=Math.Min(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));e<=Math.Max(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));e++)
        {
            var cell=owner.AtElevation(e,from.X,from.Z);
            var station=traffic.Connectors.FirstOrDefault(c=>c.Spawned&&c.IsLift&&c.Position==cell);
            if(station==null||powered&&!station.Powered||!VehicleFacilitySpace.ShaftClear(owner,station,e<Math.Max(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId))))return false;
        }
        return true;
    }
    internal static List<string> Claims(LayerMapComponent owner,VehicleEdge edge,VehicleHull hull)
    {
        owner.TryDecode(edge.From,out var from);owner.TryDecode(edge.To,out var to);
        var result=new List<string>();
        int low=Math.Min(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));
        int high=Math.Max(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));
        // Every stop of a shaft shares one mutex, even if the two journeys use disjoint floors.
        if(edge.Entry.IsLift)result.Add($"shaft:{from.X}:{from.Z}");
        else
        {
            var side=edge.Entry.Rotation.IsHorizontal?new IntVec3(0,0,1):new IntVec3(1,0,0);
            var first=edge.Entry;
            while(owner.map.GetComponent<VehicleTraffic>().Connectors.FirstOrDefault(c=>c.Spawned&&!c.IsLift&&c.Rotation==first.Rotation&&c.Position==first.Position-side) is {} previous)first=previous;
            result.Add($"ramp:{first.thingIDNumber}");
        }
        // Reserve both landings and the complete rotation envelope, not only the vehicle's centre.
        int radius=edge.Entry.IsLift?4:(int)Math.Ceiling(Math.Sqrt(hull.Width*hull.Width+hull.Length*hull.Length)/2);
        for(int e=low;e<=high;e++)
        {
            if(!owner.Layout!.TryGetLayerAtElevation(e,out var layer))continue;
            for(int z=Math.Min(from.Z,to.Z)-radius;z<=Math.Max(from.Z,to.Z)+radius;z++)
                for(int x=Math.Min(from.X,to.X)-radius;x<=Math.Max(from.X,to.X)+radius;x++)
                    result.Add($"cell:{layer}:{x}:{z}");
        }
        return result;
    }
    private readonly record struct State(IntVec3 Cell,double Heading);
    /// <summary>The graph keeps arrival orientation. Native paths supply each floor leg;
    /// our immutable geometry checks its entire hull and every intermediate turn.</summary>
    internal static VehicleEdge? FirstCrossing(VehicleTraffic traffic,Pawn vehicle,LocalTargetInfo destination,PathEndMode endMode)
    {
        var adapter=VehicleCompatibility.Adapter;var owner=LayerMapComponent.For(traffic.map);
        if(!UnityData.IsInMainThread||adapter==null||owner==null||!owner.TryDecode(vehicle.Position,out _)||!owner.TryDecode(destination.Cell,out _))return null;
        long version=traffic.Version,revision=owner.Revision;
        var crossings=Edges(traffic,vehicle);
        var nodes=new List<State>{new(destination.Cell,double.NaN),new(vehicle.Position,adapter.Heading(vehicle))};
        foreach(var e in crossings)
        {
            var a=new State(e.From,e.Heading);var b=new State(e.To,e.Heading);
            if(!nodes.Contains(a))nodes.Add(a);if(!nodes.Contains(b))nodes.Add(b);
        }
        var arcs=new Dictionary<(int,int),VehicleEdge>();
        foreach(var e in crossings)
        {
            var key=(nodes.IndexOf(new(e.From,e.Heading)),nodes.IndexOf(new(e.To,e.Heading)));
            if(!arcs.TryGetValue(key,out var old)||e.Ticks<old.Ticks)arcs[key]=e;
        }
        var nativePaths=new Dictionary<(IntVec3,IntVec3,PathEndMode),IReadOnlyList<IntVec3>?>();
        IEnumerable<RouteArc> Adjacent(int from)
        {
            if(from==0)yield break;
            var start=nodes[from];owner.TryDecode(start.Cell,out var local);
            for(int to=0;to<nodes.Count;to++)
            {
                if(to==from||to==1)continue;
                if(arcs.TryGetValue((from,to),out var e)){yield return new(to,e.Ticks);continue;}
                var end=nodes[to];
                if(!owner.TryDecode(end.Cell,out var other)||local.LayerId!=other.LayerId)continue;
                var mode=to==0?endMode:PathEndMode.OnCell;
                var key=(start.Cell,end.Cell,mode);
                if(!nativePaths.TryGetValue(key,out var path))
                {
                    path=start.Cell==end.Cell?new[]{start.Cell}:adapter.FindLeg(vehicle,start.Cell,to==0?destination:new LocalTargetInfo(end.Cell),mode);
                    nativePaths[key]=path;
                }
                if(path==null)continue;
                double heading=start.Heading;var previous=start.Cell;bool clear=true;
                foreach(var cell in path)
                {
                    if(cell==previous)continue;
                    double nextHeading=(Math.Atan2(cell.x-previous.x,cell.z-previous.z)*180/Math.PI+360)%360;
                    if(!VehicleSpace.CanTurn(owner,vehicle,previous,heading,nextHeading,true)||
                       !VehicleSpace.CanTravel(owner,vehicle,previous,cell,nextHeading,true)){clear=false;break;}
                    previous=cell;heading=nextHeading;
                }
                if(clear&&(to==0||VehicleSpace.CanTurn(owner,vehicle,previous,heading,end.Heading,true)))
                    yield return new(to,Math.Max(1,path.Count)*13+(to==0?0:(int)Math.Abs(VehicleGeometry.SignedTurn(heading,end.Heading))/3));
            }
        }
        var route=EndpointSearch.Find(1,0,Adjacent);
        if(route==null||version!=traffic.Version||revision!=owner.Revision)return null;
        for(int i=1;i<route.Count;i++)if(arcs.TryGetValue((route[i-1],route[i]),out var edge))return edge;
        return null;
    }
}
