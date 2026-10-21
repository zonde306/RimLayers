using RimLayers.Core;
using RimLayers.Layout;
using Verse;
using RimWorld;

namespace RimLayers.Environment;

public sealed class AirLinks
{
    private readonly LayerMapComponent owner;
    private readonly HashSet<(int X,int Z)> active=new();
    public int ActiveColumns=>active.Count;
    public AirLinks(LayerMapComponent owner)=>this.owner=owner;
    public void Mark(IntVec3 cell)
    {
        if(owner.TryDecode(cell,out var logical)) active.Add((logical.X,logical.Z));
    }
    public void Rebuild()
    {
        active.Clear();
        for(int layer=0;layer<owner.Layout!.LayerCount;layer++)
            foreach(var cell in owner.LayerRect(layer).Cells)
                if((owner.map.gasGrid.GetDirect(cell)&0xFFFFFF00u)!=0) Mark(cell);
    }
    public bool IsOpen(IntVec3 lower,IntVec3 upper)=>VolumeLinks.Open(owner,lower,upper,LayerChangeKind.Air);
    public void Tick()
    { if(Find.TickManager.TicksGame%30==0) Exchange(); }
    public void Exchange()
    {
        if(active.Count==0) return;
        int[] ordered=owner.Layout!.OrderedLayers.ToArray();
        int count=ordered.Length;
        Span<uint> before=stackalloc uint[count],after=stackalloc uint[count];
        Span<bool> links=stackalloc bool[Math.Max(0,count-1)];
        foreach(var column in active.ToArray())
        {
            bool any=false;
            for(int layer=0;layer<count;layer++)
            {
                IntVec3 cell=owner.ToStorage(new LayerCell(ordered[layer],column.X,column.Z));
                before[layer]=owner.map.gasGrid.GetDirect(cell);any|=(before[layer]&0xFFFFFF00u)!=0;
                if(layer>0) links[layer-1]=IsOpen(owner.ToStorage(new LayerCell(ordered[layer-1],column.X,column.Z)),cell);
            }
            if(!any) { active.Remove(column);continue; }
            before.CopyTo(after);GasExchange.Apply(after,links,owner.Environment.Enabled?LayerEnvironment.Settings.AirExchange:1);
            for(int layer=0;layer<count;layer++)
                if(before[layer]!=after[layer])
                {
                    IntVec3 cell=owner.ToStorage(new LayerCell(ordered[layer],column.X,column.Z));
                    owner.map.gasGrid.SetDirect(cell,after[layer]);
                    owner.map.mapDrawer.MapMeshDirty(cell,MapMeshFlagDefOf.Gas);
                }
        }
    }
}
