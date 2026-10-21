using RimLayers.Core;
using RimLayers.Layout;
using Verse;

namespace RimLayers.Environment;

public sealed class HeatLinks
{
    private readonly LayerMapComponent owner;
    private long cachedRevision=-1,cachedConnectors=-1;
    private (float Slab,float Open,bool Enabled) cachedSettings;
    private List<Room> cachedRooms=new();
    private HeatEdge[] cachedEdges=Array.Empty<HeatEdge>();
    public void Invalidate()=>cachedRevision=-1;
    public int LastPairCount { get; private set; }
    public long Exchanges { get; private set; }
    public HeatLinks(LayerMapComponent owner)=>this.owner=owner;
    public double Conductance(IntVec3 lower,IntVec3 upper)
    {
        if(!owner.TryDecode(lower,out var a) || !owner.TryDecode(upper,out var b) ||
            a.X!=b.X || a.Z!=b.Z || owner.Elevation(b.LayerId)!=owner.Elevation(a.LayerId)+1) return 0;
        if(VolumeLinks.Open(owner,lower,upper,LayerChangeKind.Heat))return 0.084*(owner.Environment.Enabled?LayerEnvironment.Settings.OpeningHeat:1);
        RoofDef roof=owner.map.roofGrid.RoofAt(lower);
        return roof!=null && !roof.isThickRoof ? 0.006*(owner.Environment.Enabled?LayerEnvironment.Settings.SlabHeat:1) : 0;
    }
    public void Exchange()
    {
        var settings=(LayerEnvironment.Settings.SlabHeat,LayerEnvironment.Settings.OpeningHeat,owner.Environment.Enabled);
        if(cachedRevision!=owner.Revision||cachedConnectors!=owner.Connectors.Version||cachedSettings!=settings)
        {Rebuild();cachedRevision=owner.Revision;cachedConnectors=owner.Connectors.Version;cachedSettings=settings;}
        LastPairCount=cachedEdges.Length;Exchanges++;
        var volumes=cachedRooms.Select(r=>new HeatVolume(r.UsesOutdoorTemperature ? (owner.Environment.Enabled?owner.Environment.LocalOutside(r):owner.map.mapTemperature.OutdoorTemp) : r.Temperature,
            r.CellCount,r.UsesOutdoorTemperature)).ToArray();
        double[] energy=HeatExchange.Calculate(volumes,cachedEdges);
        for(int i=0;i<cachedRooms.Count;i++)if(!volumes[i].Reservoir)cachedRooms[i].PushHeat((float)energy[i]);
    }
    private void Rebuild()
    {
        var rooms=new List<Room>();var indices=new Dictionary<Room,int>();
        var pairs=new Dictionary<(int A,int B),double>();
        int Index(Room room)
        {
            if(indices.TryGetValue(room,out int index)) return index;
            index=rooms.Count;indices.Add(room,index);rooms.Add(room);return index;
        }
        foreach(int layer in owner.Layout!.OrderedLayers.Where(l=>owner.Layout.TryGetAdjacentLayer(l,1,out _)))
            foreach(IntVec3 lower in owner.LayerRect(layer).Cells)
            {
                IntVec3 upper=owner.Adjacent(lower,1);
                double conductance=Conductance(lower,upper);
                if(conductance==0) continue;
                Room a=lower.GetRoom(owner.map),b=upper.GetRoom(owner.map);
                if(a==null || b==null || a==b || a.CellCount==0 || b.CellCount==0 ||
                    a.UsesOutdoorTemperature && b.UsesOutdoorTemperature) continue;
                int ia=Index(a),ib=Index(b);var key=ia<ib ? (ia,ib) : (ib,ia);
                pairs[key]=pairs.GetValueOrDefault(key)+conductance;
            }
        cachedRooms=rooms;cachedEdges=pairs.Select(p=>new HeatEdge(p.Key.A,p.Key.B,p.Value)).ToArray();
    }
    /// <summary>The actual column envelope exchanges with outdoors; covered slabs exchange with the upper volume.</summary>
    public float OutdoorCoverage(Room room,bool thinRoof)
    {
        int cells=0;
        foreach(IntVec3 at in room.Cells)
        {
            if(!owner.TryDecode(at,out var c) || !(owner.Environment.Enabled?owner.Sky.IsExposedAbove(at):owner.Elevation(c.LayerId)==owner.Layout!.HighestElevation)) continue;
            RoofDef roof=owner.map.roofGrid.RoofAt(at);
            if(thinRoof ? roof!=null && !roof.isThickRoof : roof==null) cells++;
        }
        return room.CellCount==0 ? 0 : (float)cells/room.CellCount;
    }
}
