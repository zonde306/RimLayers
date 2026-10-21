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
    public long Version { get; private set; }
    public bool HasAny => stairs.Count != 0;
    public ConnectorGraph(LayerMapComponent owner) => this.owner = owner;
    public void Register(Building_Stairs stair) { if (stairs.Add(stair)) Invalidate(); }
    internal void TickExtensions()
    {foreach(var lift in stairs.OfType<Building_Elevator>())if(!lift.ConnectsAdjacent)lift.TickExtensions();}
    public void Remove(Building_Stairs stair) { if (stairs.Remove(stair)) Invalidate(); }
    public void Invalidate() { Version++; owner.map.reachability?.ClearCache(); }

    public List<ConnectorEdge> MovementEdges(TraverseParms parms)
    {
        var result = new List<ConnectorEdge>();
        foreach (var stair in stairs.OrderBy(s => s.thingIDNumber))
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
                foreach(var peer in stairs.OfType<Building_Elevator>().Where(p=>!p.ConnectsAdjacent&&p!=station&&p.StopEnabled))
                {
                    if(!owner.TryDecode(peer.Position,out var to)||from.X!=to.X||from.Z!=to.Z||!EntryAllowed(peer.Position,parms))continue;
                    var span=new List<Building_Elevator>();bool valid=true;
                    foreach(int layer in owner.Layout!.OrderedLayers.Where(l=>owner.Elevation(l)>=Math.Min(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId))&&owner.Elevation(l)<=Math.Max(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId))))
                    {
                        var at=owner.ToStorage(new(layer,from.X,from.Z));
                        var segment=stairs.OfType<Building_Elevator>().FirstOrDefault(p=>!p.ConnectsAdjacent&&p.Position==at);
                        if(segment==null||!StationUsable(segment,parms)){valid=false;break;}
                        if(owner.Elevation(layer)>Math.Min(owner.Elevation(from.LayerId),owner.Elevation(to.LayerId))&&!owner.Structure.HasRequiredRoof(at)){valid=false;break;}
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
        foreach(var stair in stairs.Where(s=>s.ConnectsAdjacent&&((s.Position==lower&&s.UpperCell==upper)||(s.Position==upper&&s.UpperCell==lower))))
        {
            var permission=stair.def.GetModExtension<ConnectorTransmission>();
            if(!stairs.Contains(stair) || !owner.Structure.HasSurface(lower) ||
                !owner.Structure.HasSurface(upper) || permission==null) continue;
            if(medium==LayerChangeKind.Air && permission.air || medium==LayerChangeKind.Heat && permission.heat ||
                medium==LayerChangeKind.Sound && permission.sound || medium==LayerChangeKind.Fire && permission.fire ||
                medium==LayerChangeKind.Liquid && permission.liquid) return true;
        }
        return false;
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
