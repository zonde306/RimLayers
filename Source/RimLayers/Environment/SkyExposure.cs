using System.Runtime.CompilerServices;
using RimLayers.Core;
using RimLayers.Layout;
using Verse;
using RimWorld;

namespace RimLayers.Environment;

/// <summary>Derived sky geometry; never writes substitute roofs into RoofGrid.</summary>
public sealed class SkyExposure
{
    private readonly LayerMapComponent owner;
    private readonly Dictionary<(int X,int Z),bool[]> columns=new();
    private readonly ConditionalWeakTable<Room,RoomSky> rooms=new();
    private long revision;
    private sealed class RoomSky { public long Revision=-1; public int Open; public bool Edge; }
    public SkyExposure(LayerMapComponent owner)
    {
        this.owner=owner;
        owner.Changed+=(cell,kind)=>
        {
            if((kind&(LayerChangeKind.Structure|LayerChangeKind.Light|LayerChangeKind.Heat))==0) return;
            columns.Remove((cell.X,cell.Z));revision++;
            foreach(IntVec3 at in owner.Column(cell.X,cell.Z))
                if(at.InBounds(owner.map)) owner.map.mapDrawer?.MapMeshDirty(at,MapMeshFlagDefOf.GroundGlow,true,true);
        };
    }
    public void Rebuild() { columns.Clear();rooms.Clear();revision++; }
    public void RoomChanged(Room room)=>rooms.Remove(room);
    public bool IsExposed(IntVec3 storage)
    {
        if(!owner.TryDecode(storage,out var cell)) return false;
        var key=(cell.X,cell.Z);
        if(!columns.TryGetValue(key,out var exposed))
        {
            exposed=new bool[owner.Layout!.LayerCount];bool blocked=false;
            foreach(int layer in owner.Layout.OrderedLayers.Reverse())
            {
                IntVec3 at=owner.ToStorage(new LayerCell(layer,cell.X,cell.Z));
                // During native ground-only generation the upper bands do not exist yet.
                if(!at.InBounds(owner.map)) { exposed[layer]=true;continue; }
                bool roof=owner.map.roofGrid.RoofAt(at)!=null;
                exposed[layer]=owner.Elevation(layer)>=0 && !blocked && !roof;
                blocked|=roof || owner.Structure.HasSurface(at);
            }
            columns.Add(key,exposed);
        }
        return exposed[cell.LayerId];
    }
    public bool IsExposedAbove(IntVec3 at)
    {
        if(!owner.TryDecode(at,out var local))return false;
        if(!owner.Layout!.TryGetAdjacentLayer(local.LayerId,1,out int next))return owner.Elevation(local.LayerId)>=0;
        IntVec3 upper=owner.ToStorage(new LayerCell(next,local.X,local.Z));
        return !owner.Structure.HasSurface(upper) && IsExposed(upper);
    }
    private RoomSky For(Room room)
    {
        RoomSky info=rooms.GetOrCreateValue(room);
        if(info.Revision==revision) return info;
        info.Open=0;info.Edge=false;
        foreach(IntVec3 at in room.Cells)
        {
            if(!owner.TryDecode(at,out var cell)) continue;
            if(IsExposed(at)) info.Open++;
            info.Edge|=owner.Elevation(cell.LayerId)>=0&&(cell.X==0 || cell.X==owner.Layout!.Width-1 || cell.Z==0 || cell.Z==owner.Layout!.Height-1);
        }
        info.Revision=revision;return info;
    }
    public int OpenRoofCount(Room room)=>For(room).Open;
    public bool TouchesEdge(Room room)=>For(room).Edge;
}
