using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

public sealed class Acoustics
{
    private readonly LayerMapComponent owner;
    public Acoustics(LayerMapComponent owner)=>this.owner=owner;
    public Dictionary<IntVec3,float> Distances(IntVec3 source,float radius)
    {
        var distances=new Dictionary<IntVec3,float>();
        if(!owner.TryDecode(source,out _) || source.GetRegion(owner.map)==null)return distances;
        var queue=new PriorityQueue<IntVec3,float>();distances[source]=0;queue.Enqueue(source,0);
        void Visit(IntVec3 cell,float cost)
        {
            if(cost>radius || distances.TryGetValue(cell,out float old) && old<=cost)return;
            distances[cell]=cost;queue.Enqueue(cell,cost);
        }
        while(queue.TryDequeue(out IntVec3 at,out float distance))
        {
            if(distances[at]!=distance)continue;
            owner.TryDecode(at,out var here);
            foreach(IntVec3 offset in GenAdj.AdjacentCells)
            {
                IntVec3 next=at+offset;
                if(!owner.TryDecode(next,out var there) || here.LayerId!=there.LayerId || !owner.map.gasGrid.GasCanMoveTo(next))continue;
                bool diagonal=offset.x!=0 && offset.z!=0;
                if(diagonal && !owner.map.gasGrid.GasCanMoveTo(at+new IntVec3(offset.x,0,0)) &&
                    !owner.map.gasGrid.GasCanMoveTo(at+new IntVec3(0,0,offset.z)))continue;
                Visit(next,distance+(diagonal?1.41421356f:1));
            }
            foreach(int direction in new[]{-1,1})
            {
                if(!owner.Layout!.TryGetAdjacentLayer(here.LayerId,direction,out int layer))continue;
                IntVec3 next=owner.ToStorage(new LayerCell(layer,here.X,here.Z));
                if(VolumeLinks.Open(owner,direction>0?at:next,direction>0?next:at,LayerChangeKind.Sound))Visit(next,distance+3);
            }
        }
        return distances;
    }
    private float LowerBound(IntVec3 cell,LayerCell origin)
    {
        owner.TryDecode(cell,out var target);
        float dx=target.X-origin.X,dz=target.Z-origin.Z,dy=(owner.Elevation(target.LayerId)-owner.Elevation(origin.LayerId))*3;
        return dx*dx+dz*dz+dy*dy;
    }
    public void NotifyOtherFloors(Thing source,IntVec3 position,float radius,GenClamor.ClamorEffect effect)
    {
        if(!owner.TryDecode(position,out var origin))return;
        var listeners=owner.map.mapPawns.AllPawnsSpawned.Where(p=>owner.TryDecode(p.Position,out var place) && place.LayerId!=origin.LayerId)
            .Select(p=>(Pawn:p,Range:radius*Math.Clamp(p.health.capacities.GetLevel(PawnCapacityDefOf.Hearing),0,1)))
            .Where(p=>p.Range>0 && LowerBound(p.Pawn.Position,origin)<=p.Range*p.Range).ToList();
        if(listeners.Count==0)return;
        var distances=Distances(position,listeners.Max(p=>p.Range));
        foreach(var listener in listeners)
            if(distances.TryGetValue(listener.Pawn.Position,out float distance) && distance<=listener.Range)effect(source,listener.Pawn);
    }
}
