using RimLayers.Core;
using Verse;
using Verse.AI;

namespace RimLayers.Navigation;

/// <summary>Boolean connectivity only. Native reachability owns layer-local policy
/// and its region cache; the connector graph adds directed vertical links.
/// Complete weighted paths remain the responsibility of LayerRouter.</summary>
internal static class LayerReachability
{
    [ThreadStatic] internal static bool InsideNative;

    internal static bool CanReach(LayerMapComponent owner,IntVec3 start,LocalTargetInfo target,
        PathEndMode mode,TraverseParms parms)
    {
        if(!owner.TryDecode(start,out _)||!owner.TryDecode(target.Cell,out var goal)||
            target.HasThing&&target.Thing.MapHeld!=owner.map)return false;
        var visited=new HashSet<IntVec3>{start};
        var queue=new Queue<IntVec3>();queue.Enqueue(start);
        List<ConnectorEdge>? connectors=null;
        while(queue.TryDequeue(out var from))
        {
            owner.TryDecode(from,out var here);
            if(here.LayerId==goal.LayerId&&Native(owner.map,from,target,mode,parms))return true;
            connectors??=owner.Connectors.MovementEdges(parms);
            foreach(var edge in connectors)
            {
                // A failed same-floor check may still succeed via another floor.
                // Each landing is visited once; no nearest-stair or direction cutoff.
                if(visited.Contains(edge.To)||!owner.TryDecode(edge.From,out var entry)||entry.LayerId!=here.LayerId)continue;
                if(!Native(owner.map,from,edge.From,PathEndMode.OnCell,parms))continue;
                visited.Add(edge.To);queue.Enqueue(edge.To);
            }
        }
        return false;
    }

    private static bool Native(Map map,IntVec3 start,LocalTargetInfo target,PathEndMode mode,TraverseParms parms)
    {
        bool previous=InsideNative;InsideNative=true;
        try{return map.reachability.CanReach(start,target,mode,parms);}
        finally{InsideNative=previous;}
    }
}
