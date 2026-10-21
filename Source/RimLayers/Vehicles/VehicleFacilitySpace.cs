using RimLayers.Core;
using RimWorld;
using Verse;
namespace RimLayers.Vehicles;

/// <summary>A lift's own retractable platform, not a replacement for an existing slab.
/// Upper stations are built over deliberately cleared openings and supported at the four corners.</summary>
internal static class VehicleFacilitySpace
{
    internal static bool FitsCabin(LayerMapComponent owner,Pawn vehicle,Building_VehicleConnector station,double heading)
    {
        var adapter=VehicleCompatibility.Adapter!;
        if(!VehicleGeometry.FitsLift(adapter.Hull(vehicle),heading))return false;
        var rect=station.OccupiedRect();
        if(adapter.OccupiedCells(vehicle,station.Position,heading).Any(c=>!rect.Contains(c)))return false;
        owner.TryDecode(station.Position,out var at);
        return VehicleGeometry.Footprint(adapter.Hull(vehicle),VehicleSpace.Pose(owner,vehicle,station.Position,heading))
            .All(c=>rect.Contains(owner.ToStorage(new(at.LayerId,c.X,c.Z))));
    }
    internal static bool IsConnector(BuildableDef def)=>def is ThingDef t&&t.thingClass==typeof(Building_VehicleConnector);
    internal static bool NativeSurface(LayerMapComponent owner,IntVec3 cell)
    {
        if(!owner.TryDecode(cell,out var at))return false;
        var terrain=owner.map.terrainGrid.TerrainAt(cell);
        // The open shaft bypasses the native terrain-affordance test only inside its cabin.
        // The source and supporting platform still obey BuildingBase's Light foundation rule.
        return terrain.defName!="RL_VehicleDeck"&&!terrain.IsWater&&terrain.passability!=Traversability.Impassable&&terrain.affordances.Any(a=>a.defName=="Light")&&
            (at.LayerId==0||owner.Mountains.IsNaturalFloor(cell)||owner.StructuralCells.Contains(cell)&&owner.Structure.HasRequiredRoof(cell));
    }
    internal static bool DeckSupported(LayerMapComponent owner,CellRect rect)
    {
        // Every corner must bear on the existing surrounding structure; no recursive support through another deck.
        foreach(var c in new[]{new IntVec3(rect.minX-1,0,rect.minZ),new IntVec3(rect.maxX+1,0,rect.minZ),
                                new IntVec3(rect.minX-1,0,rect.maxZ),new IntVec3(rect.maxX+1,0,rect.maxZ)})
            if(!NativeSurface(owner,c))return false;
        return true;
    }
    internal static bool HasDeck(LayerMapComponent owner,IntVec3 cell)=>
        cell.InBounds(owner.map)&&owner.map.terrainGrid.TerrainAt(cell)?.defName=="RL_VehicleDeck"&&
        cell.GetThingList(owner.map).OfType<Building_VehicleConnector>()
            .Any(b=>b.IsLift&&b.Spawned&&DeckSupported(owner,b.OccupiedRect()));
    internal static AcceptanceReport Validate(LayerMapComponent owner,ThingDef def,IntVec3 center,Rot4 rotation)
    {
        if(VehicleCompatibility.Adapter==null)return "RL_VehicleUnavailable".Translate();
        if(!owner.TryDecode(center,out var at))return "RL_VehicleNoSurface".Translate();
        var rect=GenAdj.OccupiedRect(center,rotation,def.size);
        if(rect.Any(c=>!owner.TryDecode(c,out var part)||part.LayerId!=at.LayerId))return "RL_VehicleNoSurface".Translate();
        if(def.GetModExtension<VehicleConnectorProperties>()?.lift!=true)
        {
            if(rect.Any(c=>!NativeSurface(owner,c)))return "RL_VehicleNoSurface".Translate();
            if(!owner.Layout!.TryGetAdjacentLayer(at.LayerId,1,out var upper))return "RL_VehicleRampUpper".Translate();
            foreach(var c in rect)
            {
                var above=owner.Adjacent(c,1);
                if(owner.map.roofGrid.RoofAt(c)!=null||owner.Structure.HasSurface(above)||above.GetEdifice(owner.map) is {def.passability:Traversability.Impassable})
                    return "RL_VehicleRampOpening".Translate();
                var front=c+rotation.FacingCell;
                if(!rect.Contains(front))
                {
                    var landing=owner.Adjacent(front,1);
                    if(!landing.IsValid||!owner.Structure.HasSurface(landing))return "RL_VehicleRampLanding".Translate();
                }
            }
            foreach(var other in owner.map.GetComponent<VehicleTraffic>().Connectors.Where(b=>b.Spawned&&!b.IsLift))
            {
                if(other.Position==center&&other.Rotation==rotation)continue;
                if(!rect.ExpandedBy(1).Overlaps(other.OccupiedRect()))continue;
                var delta=other.Position-center;int along=delta.x*rotation.FacingCell.x+delta.z*rotation.FacingCell.z;
                if(other.Rotation!=rotation||along!=0)return "RL_VehicleRampJoin".Translate();
            }
            return true;
        }
        if(owner.Elevation(at.LayerId)>0)
        {
            if(!DeckSupported(owner,rect))return "RL_VehicleDeckSupport".Translate();
            foreach(var c in rect)
            {
                var terrain=owner.map.terrainGrid.TerrainAt(c);
                // The existing station / completed frame may be revalidated. Never remove player-built slabs.
                if(terrain!=RimLayersDefOf.RL_OpenAir&&terrain.defName!="RL_VehicleDeck")return "RL_VehicleShaftClear".Translate();
                var lower=owner.Adjacent(c,-1);
                if(lower.IsValid&&owner.map.roofGrid.RoofAt(lower)!=null)return "RL_VehicleShaftClear".Translate();
            }
        }
        else
        {
            if(!DeckSupported(owner,rect))return "RL_VehicleDeckSupport".Translate();
            foreach(var c in rect)
            {
                if(NativeSurface(owner,c))continue;
                var lower=owner.Adjacent(c,-1);
                var terrain=owner.map.terrainGrid.TerrainAt(c);
                if(!lower.IsValid||owner.map.roofGrid.RoofAt(lower)!=null||
                    terrain!=RimLayersDefOf.RL_OpenAir&&terrain.defName!="RL_VehicleDeck")return "RL_VehicleNoSurface".Translate();
            }
        }
        return true;
    }
    internal static void InstallDeck(Building_VehicleConnector station)
    {
        if(!station.IsLift||LayerMapComponent.For(station.Map) is not {} owner||
            !owner.TryDecode(station.Position,out _))return;
        var deck=DefDatabase<TerrainDef>.GetNamed("RL_VehicleDeck");
        foreach(var c in station.OccupiedRect())
            if(station.Map.terrainGrid.TerrainAt(c)==RimLayersDefOf.RL_OpenAir)station.Map.terrainGrid.SetTerrain(c,deck);
    }
    internal static void RemoveDeck(Map map,IEnumerable<IntVec3> cells)
    {
        foreach(var c in cells)
            if(map.terrainGrid.TerrainAt(c).defName=="RL_VehicleDeck")map.terrainGrid.SetTerrain(c,RimLayersDefOf.RL_OpenAir);
    }
    internal static bool ShaftClear(LayerMapComponent owner,Building_VehicleConnector station,bool ceiling)
    {
        if(!owner.TryDecode(station.Position,out var at)||!DeckSupported(owner,station.OccupiedRect()))return false;
        foreach(var c in station.OccupiedRect())
        {
            if(owner.Elevation(at.LayerId)>0&&owner.map.terrainGrid.TerrainAt(c).defName!="RL_VehicleDeck")return false;
            if(ceiling&&owner.map.roofGrid.RoofAt(c)!=null)return false;
            if(c.GetThingList(owner.map).Any(t=>t!=station&&t is not Pawn&&t.def.passability==Traversability.Impassable))return false;
        }
        return true;
    }
}
