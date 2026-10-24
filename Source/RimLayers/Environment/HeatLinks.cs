using RimLayers.Core;
using RimLayers.Layout;
using Verse;

namespace RimLayers.Environment;

public sealed class HeatLinks
{
    private readonly LayerMapComponent owner;
    private (float Slab,float Open,bool Enabled) cachedSettings;
    private bool settingsValid;
    private readonly IncrementalEdgeSum<Room> edges;
    private List<Room> cachedRooms=new();
    private HeatEdge[] cachedEdges=Array.Empty<HeatEdge>();
    public void Invalidate()=>edges.MarkFullRebuild();
    public int LastPairCount { get; private set; }
    public long Exchanges { get; private set; }
    public long Rebuilds { get; private set; }
    /// <summary>Columns re-evaluated by the incremental path (the metric the perf test asserts on).</summary>
    public long ColumnEvaluations => edges.ColumnEvaluations;
    public HeatLinks(LayerMapComponent owner)
    {
        this.owner=owner;
        // Reference identity keeps the edge order stable across a rebuild, matching the previous
        // insertion order without depending on Room's own ordering.
        edges=new IncrementalEdgeSum<Room>(ReferenceComparer<Room>.Instance);
        owner.Changed+=Changed;
    }

    private void Changed(LayerCell cell,LayerChangeKind kind)
    {
        // Only a structure/heat change can alter a vertical conductance or the roof of a column.
        if((kind&(LayerChangeKind.Structure|LayerChangeKind.Heat))==0)return;
        edges.MarkDirty(cell.X,cell.Z);
    }

    public double Conductance(IntVec3 lower,IntVec3 upper)
    {
        if(!owner.TryDecode(lower,out var a) || !owner.TryDecode(upper,out var b) ||
            a.X!=b.X || a.Z!=b.Z || owner.Elevation(b.LayerId)!=owner.Elevation(a.LayerId)+1) return 0;
        // Order matters for correctness: a roofed stair still conducts through its open channel via
        // the connector transfer edge, so the open test must run before the slab fallback.
        // HasTransferEdge is now an O(1) index lookup, which is what made this affordable.
        if(VolumeLinks.Open(owner,lower,upper,LayerChangeKind.Heat))return 0.084*(owner.Environment.Enabled?LayerEnvironment.Settings.OpeningHeat:1);
        RoofDef roof=owner.map.roofGrid.RoofAt(lower);
        return roof!=null && !roof.isThickRoof ? 0.006*(owner.Environment.Enabled?LayerEnvironment.Settings.SlabHeat:1) : 0;
    }

    public void Exchange()
    {
        var settings=(LayerEnvironment.Settings.SlabHeat,LayerEnvironment.Settings.OpeningHeat,owner.Environment.Enabled);
        bool settingsChanged=!settingsValid||cachedSettings!=settings;
        if(settingsChanged){cachedSettings=settings;settingsValid=true;}
        if(settingsChanged||edges.NeedsFullRebuild||edges.DirtyCount!=0)
        {
            EnsureEdges();
            Rebuilds++;
        }
        LastPairCount=cachedEdges.Length;Exchanges++;
        var volumes=cachedRooms.Select(r=>new HeatVolume(r.UsesOutdoorTemperature ? (owner.Environment.Enabled?owner.Environment.LocalOutside(r):owner.map.mapTemperature.OutdoorTemp) : r.Temperature,
            r.CellCount,r.UsesOutdoorTemperature)).ToArray();
        double[] energy=HeatExchange.Calculate(volumes,cachedEdges);
        for(int i=0;i<cachedRooms.Count;i++)if(!volumes[i].Reservoir)cachedRooms[i].PushHeat((float)energy[i]);
    }

    private void EnsureEdges()
    {
        edges.Update(Columns(),EvaluateColumn);
        // Materialise once: Edges() builds a fresh array on every call.
        var pairs=edges.Edges();
        // Re-index rooms in first-seen order for the volume array.
        var rooms=new List<Room>();
        var index=new Dictionary<Room,int>(ReferenceComparer<Room>.Instance);
        foreach(var pair in pairs)
        {
            if(index.TryAdd(pair.A,rooms.Count))rooms.Add(pair.A);
            if(index.TryAdd(pair.B,rooms.Count))rooms.Add(pair.B);
        }
        cachedRooms=rooms;
        cachedEdges=pairs.Select(p=>new HeatEdge(index[p.A],index[p.B],p.Weight)).ToArray();
    }

    /// <summary>Every column that can pair two allocated layers.</summary>
    private IEnumerable<(int X,int Z)> Columns()
    {
        var layout=owner.Layout!;
        if(!layout.TryGetAdjacentLayer(0,1,out _) && layout.LayerCount<2) yield break;
        for(int x=0;x<layout.Width;x++)
            for(int z=0;z<layout.Height;z++)
                yield return (x,z);
    }

    private (Room A,Room B,double Weight)[] EvaluateColumn((int X,int Z) column)
    {
        var layout=owner.Layout!;
        var contributions=new List<(Room,Room,double)>(1);
        Room? previousRoom=null;
        for(int position=0;position<layout.LayerCount;position++)
        {
            int layer=layout.OrderedLayers[position];
            IntVec3 upper=owner.ToStorage(new LayerCell(layer,column.X,column.Z));
            Room room=upper.GetRoom(owner.map);
            if(position>0)
            {
                IntVec3 lower=owner.ToStorage(new LayerCell(layout.OrderedLayers[position-1],column.X,column.Z));
                double conductance=Conductance(lower,upper);
                if(conductance>0 && previousRoom!=null && room!=null && previousRoom!=room &&
                    previousRoom.CellCount!=0 && room.CellCount!=0 &&
                    !(previousRoom.UsesOutdoorTemperature&&room.UsesOutdoorTemperature))
                    contributions.Add((previousRoom,room,conductance));
            }
            previousRoom=room;
        }
        return contributions.ToArray();
    }

    /// <summary>The actual column envelope exchanges with outdoors; covered slabs exchange with the upper volume.</summary>
    /// <remarks>
    /// Deliberately uncached. The result depends on the room's cell set, every cell's surface and roof,
    /// <see cref="LayerEnvironment.Enabled"/> and the room's CellCount — and native MapTemperatureTick
    /// calls this through the ThinRoof/NoRoof prefixes on every temperature interval. A cache here was
    /// measurable but could not be proven correct against room merges/splits and the enable toggle,
    /// and it broke the top-ceiling outdoor-loss check. The column-incremental edge sum above is where
    /// the real win is; this stays a straight walk.
    /// </remarks>
    public float OutdoorCoverage(Room room,bool thinRoof)
    {
        int cells=0;
        bool enabled=owner.Environment.Enabled;
        int highest=owner.Layout!.HighestElevation;
        foreach(IntVec3 at in room.Cells)
        {
            if(!owner.TryDecode(at,out var c) || !(enabled?owner.Sky.IsExposedAbove(at):owner.Elevation(c.LayerId)==highest)) continue;
            RoofDef roof=owner.map.roofGrid.RoofAt(at);
            if(thinRoof ? roof!=null && !roof.isThickRoof : roof==null) cells++;
        }
        return room.CellCount==0 ? 0 : (float)cells/room.CellCount;
    }
}

/// <summary>Identity comparer so room dictionaries do not depend on Room's own equality.</summary>
internal sealed class ReferenceComparer<T> : IComparer<T>, IEqualityComparer<T> where T : class
{
    internal static readonly ReferenceComparer<T> Instance=new();
    public int Compare(T? x,T? y)=>ReferenceEquals(x,y)?0:
        x==null?-1:y==null?1:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x)
            .CompareTo(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(y));
    public bool Equals(T? x,T? y)=>ReferenceEquals(x,y);
    public int GetHashCode(T obj)=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
