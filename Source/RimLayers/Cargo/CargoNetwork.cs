using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Cargo;

/// <summary>Map-local port index. Cargo connections never enter ConnectorGraph.</summary>
public sealed class CargoNetwork:MapComponent
{
    public readonly HashSet<Building_CargoPort> Ports=new();
    public CargoNetwork(Map map):base(map){}
    public IEnumerable<Building_CargoHoist> Hoists=>Ports.OfType<Building_CargoHoist>();
    public static CargoNetwork For(Map map)=>map.GetComponent<CargoNetwork>();
    public void Register(Building_CargoPort port)=>Ports.Add(port);
    public void Remove(Building_CargoPort port)=>Ports.Remove(port);
    public override void FinalizeInit()
    {
        base.FinalizeInit();
        Ports.Clear();
        foreach(var p in map.listerThings.AllThings.OfType<Building_CargoPort>())Ports.Add(p);
        Reconnect();
    }
    public void Reconnect()
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null)return;
        // A station keeps its saved owner while a missing segment is repaired.
        bool changed;
        do
        {
            changed=false;
            foreach(var p in Ports.Where(p=>p.Master==null&&p.Props.shaft&&p is not Building_CargoHoist))
            {
                var masters=Neighbors(p).Select(n=>n.Master).Where(m=>m is {Spawned:true}&&m.Props.shaft).Distinct().ToList();
                if(masters.Count==1){p.Master=masters[0];changed=true;}
            }
        }while(changed);
    }
    public IEnumerable<Building_CargoPort> Neighbors(Building_CargoPort p)
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null)yield break;
        foreach(int direction in new[]{-1,1})
        {
            var at=owner.Adjacent(p.Position,direction);
            var match=Ports.FirstOrDefault(q=>q.Spawned&&q.Position==at);
            if(match!=null)yield return match;
        }
    }
    public bool Connected(Building_CargoPort p)
    {
        var m=p.Master;
        if(m?.Spawned!=true||p.Map!=m.Map||!p.Spawned)return false;
        if(p==m)return true;
        var visited=new HashSet<Building_CargoPort>{m};
        var queue=new Queue<Building_CargoPort>();queue.Enqueue(m);
        while(queue.TryDequeue(out var next))
            foreach(var n in Neighbors(next))
            {
                if(n.Master!=m||!visited.Add(n))continue;
                if(n==p)return true;
                queue.Enqueue(n);
            }
        return false;
    }
    public static Building_CargoHoist? OwnerOf(Thing? item)=>item?.ParentHolder as Building_CargoHoist;
    public static bool CanOperate(Pawn p)=>p.Spawned&&!p.Downed&&p.RaceProps.ToolUser&&
        p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);
    public Building_CargoPort? BestPort(Building_CargoHoist main,Pawn pawn,IntVec3 start,out float cost,TraverseParms? traversal=null)
    {
        cost=float.PositiveInfinity;Building_CargoPort? best=null;
        if(!CanOperate(pawn)||main.Map!=pawn.Map)return null;
        foreach(var port in Ports)
        {
            if(port.Master!=main||!port.Available||port.IsForbidden(pawn)||
                !port.Position.InAllowedArea(pawn)||!main.CanCall(port))continue;
            using var path=map.pathFinder.FindPathNow(start,port,traversal??TraverseParms.For(pawn),null,PathEndMode.Touch);
            if(!path.Found)continue;
            float c=path.TotalCost+main.EstimatedWait(port);
            if(c<cost){cost=c;best=port;}
        }
        return best;
    }
    public IEnumerable<Thing> Contents()=>Hoists.Where(h=>h.Spawned).SelectMany(h=>h.Contents);
    public override void MapComponentTick()
    {
        // Reconnect only at structural changes; no whole-map tick scan.
        foreach(var main in Hoists.ToArray())main.ServiceTick();
    }
}
