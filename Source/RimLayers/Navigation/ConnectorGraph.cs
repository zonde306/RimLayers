using RimLayers.Core;
using RimLayers.Structure;
using Verse;
using RimWorld;

namespace RimLayers.Navigation;

public enum ConnectorKind { Stairs, SingleLift, VerticalLift }
public readonly record struct ConnectorEdge(Building_Stairs Stairs, IntVec3 From, IntVec3 To,int Ticks=90,Building_Elevator[]? Shaft=null)
{
    internal bool ConstructionAccess { get; init; }
    public ConnectorEdge(Building_Stairs Stairs,IntVec3 From,IntVec3 To):this(Stairs,From,To,90,null){}
    public void Deconstruct(out Building_Stairs Stairs,out IntVec3 From,out IntVec3 To){Stairs=this.Stairs;From=this.From;To=this.To;}
    public ConnectorKind Kind=>Shaft!=null?ConnectorKind.VerticalLift:Stairs is Building_Elevator?ConnectorKind.SingleLift:ConnectorKind.Stairs;
    public int CurrentTicks
    {
        get
        {
            var settings=RimLayersMod.Settings;
            if(ConstructionAccess)return SettingsMath.TravelTicks(210,settings.EmergencyLiftTime);
            if(Shaft!=null)
            {
                bool powered=Shaft.All(s=>s.Powered);
                return SettingsMath.TravelTicks(powered?30+45*(Shaft.Length-1):90+120*(Shaft.Length-1),powered?settings.PoweredLiftTime:settings.EmergencyLiftTime);
            }
            if(Stairs is Building_Elevator lift)return SettingsMath.TravelTicks(lift.Powered?25:180,lift.Powered?settings.PoweredLiftTime:settings.EmergencyLiftTime);
            return SettingsMath.TravelTicks(90,settings.StairTime);
        }
    }
}

public sealed class ConnectorGraph
{
    private readonly LayerMapComponent owner;
    private readonly HashSet<Building_Stairs> stairs = new();
    // Connector usability depends only on the columns that actually hold a connector: Position and
    // UpperCell share (x,z), and vertical lifts stay in one column. Structural writes elsewhere must
    // not invalidate the graph, or every terrain/roof edit rebuilds heat, air and environment topology.
    private readonly HashSet<(int X,int Z)> columns = new();
    // The registration set changes rarely but MovementEdges/HasTransferEdge run on hot native paths
    // (Reachability.CanReach, every stair step commit, every per-cell VolumeLinks.Open). Cache the
    // derived arrays and the transfer index against Version instead of re-sorting and re-scanning.
    private long cachedVersion = -1;
    private Building_Stairs[] orderedStairs = Array.Empty<Building_Stairs>();
    private Building_Elevator[] shaftLifts = Array.Empty<Building_Elevator>();
    private Dictionary<IntVec3,Building_Elevator> shaftLiftAt = new();
    private Dictionary<(IntVec3 A,IntVec3 B),int> transferFlags = new();
    public long Version { get; private set; }
    public bool HasAny => stairs.Count != 0;
    public ConnectorGraph(LayerMapComponent owner) => this.owner = owner;
    public void Register(Building_Stairs stair) { if (stairs.Add(stair)) { columns.Add((stair.Position.x, stair.Position.z)); Invalidate(); } }
    internal void TickExtensions()
    {foreach(var lift in CachedShaftLifts())lift.TickExtensions();}
    public void Remove(Building_Stairs stair) { if (stairs.Remove(stair)) { if(!stairs.Any(s => (s.Position.x, s.Position.z) == (stair.Position.x, stair.Position.z))) columns.Remove((stair.Position.x, stair.Position.z)); Invalidate(); } }
    public void Invalidate() { Version++; owner.map.reachability?.ClearCache(); }
    /// <summary>True when a structural change in this column can affect connector edges.</summary>
    public bool TouchesColumn(IntVec3 cell) => columns.Contains((cell.x, cell.z));

    private void EnsureCached()
    {
        if (cachedVersion == Version) return;
        cachedVersion = Version;
        // Ascending thingIDNumber keeps edge order identical to the previous OrderBy projection.
        orderedStairs = stairs.OrderBy(s => s.thingIDNumber).ToArray();
        shaftLifts = orderedStairs.OfType<Building_Elevator>().Where(l => !l.ConnectsAdjacent).ToArray();
        shaftLiftAt = new Dictionary<IntVec3,Building_Elevator>();
        foreach (var lift in shaftLifts) shaftLiftAt.TryAdd(lift.Position, lift);
        transferFlags = new Dictionary<(IntVec3,IntVec3),int>();
        foreach (var stair in orderedStairs)
        {
            if (!stair.ConnectsAdjacent) continue;
            if (stair.def.GetModExtension<ConnectorTransmission>() is not { } permission) continue;
            int flags = 0;
            if (permission.air) flags |= (int)LayerChangeKind.Air;
            if (permission.heat) flags |= (int)LayerChangeKind.Heat;
            if (permission.sound) flags |= (int)LayerChangeKind.Sound;
            if (permission.fire) flags |= (int)LayerChangeKind.Fire;
            if (permission.liquid) flags |= (int)LayerChangeKind.Liquid;
            if (flags == 0) continue;
            var key = Normalize(stair.Position, stair.UpperCell);
            transferFlags[key] = transferFlags.GetValueOrDefault(key) | flags;
        }
    }
    private static (IntVec3 A,IntVec3 B) Normalize(IntVec3 a, IntVec3 b) =>
        a.x != b.x ? (a.x < b.x ? (a, b) : (b, a)) : (a.z <= b.z ? (a, b) : (b, a));
    private Building_Elevator[] CachedShaftLifts() { EnsureCached(); return shaftLifts; }

    public List<ConnectorEdge> MovementEdges(TraverseParms parms)
    {
        var result = new List<ConnectorEdge>();
        EnsureCached();
        foreach (var stair in orderedStairs)
        {
            if(stair is Building_Elevator {Direction:0} station)
            {
                if(!station.StopEnabled||!StationUsable(station,parms))continue;
                foreach(int direction in new[]{-1,1})
                {
                    var exit=station.ConstructionExit(direction);
                    if(!exit.IsValid||!owner.Structure.HasSurface(exit)||!exit.Walkable(owner.map)||!owner.Structure.HasRequiredRoof(direction<0?station.Position:exit))continue;
                    if(EntryAllowed(exit,parms)){var access=new ConnectorEdge(station,station.Position,exit,Shaft:new[]{station}){ConstructionAccess=true};result.Add(access with{Ticks=access.CurrentTicks});}
                    if(EntryAllowed(station.Position,parms)){var access=new ConnectorEdge(station,exit,station.Position,Shaft:new[]{station}){ConstructionAccess=true};result.Add(access with{Ticks=access.CurrentTicks});}
                }
                owner.TryDecode(station.Position,out var from);
                foreach(var peer in shaftLifts)
                {
                    if(peer==station||!peer.StopEnabled)continue;
                    if(!owner.TryDecode(peer.Position,out var to)||from.X!=to.X||from.Z!=to.Z||!EntryAllowed(peer.Position,parms))continue;
                    var span=new List<Building_Elevator>();bool valid=true;
                    int low=Math.Min(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));
                    int high=Math.Max(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId));
                    foreach(int layer in owner.Layout!.OrderedLayers)
                    {
                        int elevation=owner.Elevation(layer);
                        if(elevation<low||elevation>high)continue;
                        var at=owner.ToStorage(new(layer,from.X,from.Z));
                        shaftLiftAt.TryGetValue(at,out var segment);
                        if(segment==null||!StationUsable(segment,parms)){valid=false;break;}
                        if(elevation>low&&!owner.Structure.HasRequiredRoof(at)){valid=false;break;}
                        span.Add(segment);
                    }
                    if(valid){var edge=new ConnectorEdge(station,station.Position,peer.Position,Shaft:span.ToArray());result.Add(edge with{Ticks=edge.CurrentTicks});}
                }
                continue;
            }
            if (!Usable(stair, parms)) continue;
            // Door permission applies to ENTRY, not leaving a door one already occupies.
            if(EntryAllowed(stair.UpperCell,parms)){var e=new ConnectorEdge(stair,stair.Position,stair.UpperCell);result.Add(e with{Ticks=e.CurrentTicks});}
            if(EntryAllowed(stair.Position,parms)){var e=new ConnectorEdge(stair,stair.UpperCell,stair.Position);result.Add(e with{Ticks=e.CurrentTicks});}
        }
        return result;
    }

    private bool StationUsable(Building_Elevator lift,TraverseParms parms)=>lift.Spawned&&lift.Map==owner.map&&
        owner.Structure.HasSurface(lift.Position)&&lift.Position.Walkable(owner.map)&&
        (parms.pawn==null||!lift.IsForbidden(parms.pawn))&&
        !(parms.mode==TraverseMode.ByPawn&&parms.fenceBlocked&&parms.pawn is {Roamer:true} animal&&!animal.roping.IsRopedByPawn);

    public bool Usable(Building_Stairs stair, TraverseParms parms) =>
        stair.Spawned && stair.Map == owner.map && stair.UpperCell.IsValid &&
        owner.Structure.HasSurface(stair.Position) && owner.Structure.HasSurface(stair.UpperCell) &&
        owner.Structure.HasRequiredRoof(stair.UpperCell) &&
        (stair.Direction>=0 || RimLayers.Underground.UndergroundService.TerrainAllows(owner.map,stair.Position).Accepted&&RimLayers.Underground.UndergroundService.DryLand(owner.map.terrainGrid.TerrainAt(stair.UpperCell))) &&
        (parms.pawn == null || !stair.IsForbidden(parms.pawn)) &&
        // Stairs act like pen gates for native fence-blocked roamers. A real rope
        // permits the follower; prospective human-roper/PassDoors queries still work.
        !(parms.mode == TraverseMode.ByPawn && parms.fenceBlocked &&
          parms.pawn is { Roamer: true } animal && !animal.roping.IsRopedByPawn) &&
        stair.Position.Walkable(owner.map) && stair.UpperCell.Walkable(owner.map);

    public bool HasTransferEdge(IntVec3 lower,IntVec3 upper,LayerChangeKind medium)
    {
        EnsureCached();
        if (transferFlags.Count == 0) return false;
        // The structural preconditions stay dynamic; only the registration-derived lookup is cached.
        if (!transferFlags.TryGetValue(Normalize(lower, upper), out int flags)) return false;
        if ((flags & (int)medium) == 0) return false;
        return owner.Structure.HasSurface(lower) && owner.Structure.HasSurface(upper);
    }

    private bool EntryAllowed(IntVec3 cell,TraverseParms parms) =>
        cell.GetDoor(owner.map)==null || cell.GetRegion(owner.map,RegionType.Set_All)?.Allows(parms,isDestination:false)==true;

    public bool Validate(ConnectorEdge edge, TraverseParms parms) => edge.Shaft!=null?
        edge.Ticks==edge.CurrentTicks&&MovementEdges(parms).Any(e=>e.Stairs==edge.Stairs&&e.From==edge.From&&e.To==edge.To&&e.ConstructionAccess==edge.ConstructionAccess&&e.Shaft!=null&&e.Shaft.SequenceEqual(edge.Shaft)):
        edge.Ticks==edge.CurrentTicks&&
        stairs.Contains(edge.Stairs) && Usable(edge.Stairs, parms) && EntryAllowed(edge.To,parms) &&
        ((edge.From == edge.Stairs.Position && edge.To == edge.Stairs.UpperCell) ||
         (edge.To == edge.Stairs.Position && edge.From == edge.Stairs.UpperCell));
}

public sealed class ConnectorTransmission : DefModExtension
{
    public bool air;
    public bool heat;
    public bool sound;
    public bool fire;
    public bool liquid;
}
