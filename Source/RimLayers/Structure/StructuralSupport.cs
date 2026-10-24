using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Structure;

public sealed class StructuralSupport
{
    private readonly LayerMapComponent owner;
    public StructuralSupport(LayerMapComponent owner) => this.owner = owner;
    public bool HasRequiredRoof(IntVec3 upper)
    {
        if (!owner.TryDecode(upper, out var cell)) return false;
        if (cell.LayerId == 0 || owner.Mountains.IsNaturalFloor(upper)) return true;
        IntVec3 lower = owner.Adjacent(upper,-1);
        if(!lower.IsValid)return false;
        return SupportedRoof(owner.map.roofGrid.RoofAt(lower)) &&
            (owner.Elevation(cell.LayerId) == 1 || owner.map.terrainGrid.FoundationAt(upper)?.IsSubstructure==true || HasSurface(lower));
    }

    internal bool HasShipRoof(IntVec3 upper)
    {
        if(!owner.TryDecode(upper,out var at)||owner.Elevation(at.LayerId)<=0)return false;
        return SupportedRoof(owner.map.roofGrid.RoofAt(owner.Adjacent(upper,-1)));
    }
    public bool HasSurface(IntVec3 cell) =>
        owner.TryDecode(cell, out var logical) && owner.map.terrainGrid.TerrainAt(cell)!=RimLayersDefOf.RL_OpenAir &&
        (logical.LayerId == 0 || owner.Mountains.IsNaturalFloor(cell) || owner.StructuralCells.Contains(cell) && HasRequiredRoof(cell) ||
         RimLayers.Vehicles.VehicleFacilitySpace.HasDeck(owner,cell));


    internal static bool SupportedRoof(RoofDef? roof)=>roof==RoofDefOf.RoofConstructed||roof==RoofDefOf.RoofRockThin;
    internal static bool ConnectorRoof(RoofDef? roof)=>SupportedRoof(roof)||roof==RoofDefOf.RoofRockThick;

    public TerrainDef SlabAt(IntVec3 cell)=>owner.map.terrainGrid.FoundationAt(cell) is {IsSubstructure:true} foundation?
        foundation:RimLayersDefOf.RL_StructuralFloor;
    public bool IsBareSlab(IntVec3 cell) => owner.StructuralCells.Contains(cell) &&
        owner.map.terrainGrid.TempTerrainAt(cell) == null &&
        (owner.map.terrainGrid.TerrainAt(cell) == RimLayersDefOf.RL_StructuralFloor ||
         owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure==true&&owner.map.terrainGrid.UnderTerrainAt(cell)==null);

    public bool CanDismantleSlab(IntVec3 cell)
    {
        if (!IsBareSlab(cell) || !owner.TryDecode(cell,out var logical) ||
            owner.map.roofGrid.RoofAt(cell)!=null) return false;
        foreach (int layer in owner.Layout!.Above(logical.LayerId))
        {
            var upper=owner.ToStorage(new LayerCell(layer,logical.X,logical.Z));
            if (owner.StructuralCells.Contains(upper) || owner.map.roofGrid.RoofAt(upper)!=null) return false;
        }
        if (cell.GetThingList(owner.map).Any(t=>t is Pawn || t is Building || t is Blueprint ||
            t.def.category==ThingCategory.Item)) return false;
        return GenAdj.AdjacentCells.Any(offset=>HasSurface(cell+offset) && (cell+offset).Standable(owner.map));
    }

    public AcceptanceReport Validate(BuildableDef buildable, IntVec3 center, Rot4 rotation, bool planning = false)
    {
        // Deep drills and ground-penetrating scanners read the cell's own band, so an upper floor
        // would bury resources under a floor that has no substrate at all.
        if (Compatibility.DeepResourceScope.PlacementReport(buildable, center, owner.map) is { } deepResource) return deepResource;
        if(RimLayers.Vehicles.VehicleFacilitySpace.IsConnector(buildable))
        {
            var report=RimLayers.Vehicles.VehicleFacilitySpace.Validate(owner,(ThingDef)buildable,center,rotation);
            if(!report.Accepted)return report;
            if(((ThingDef)buildable).GetModExtension<RimLayers.Vehicles.VehicleConnectorProperties>()?.lift==true)return true;
        }
        if(ConnectorDefs.IsAdjacent(buildable)&&owner.TryDecode(center,out var origin))
        {
            var quota=LayerExpansionQuota.For(owner.map,owner.Elevation(origin.LayerId)+ConnectorDefs.Direction(buildable));
            if(!quota.Accepted)return quota;
        }
        bool shipSlab=buildable.defName=="RL_ShipStructuralFloor";
        bool slab = buildable == RimLayersDefOf.RL_StructuralFloor||shipSlab;
        bool stair = ConnectorDefs.IsUp(buildable);
        if(ConnectorDefs.IsDown(buildable))
        {
            foreach(var c in GenAdj.OccupiedRect(center,rotation,buildable.Size))
            {
                var report=RimLayers.Underground.UndergroundService.TerrainAllows(owner.map,c);if(!report.Accepted)return report;
                var target=owner.Adjacent(c,-1);
                if(target.IsValid)
                {
                    if(!RimLayers.Underground.UndergroundService.DryLand(owner.map.terrainGrid.TerrainAt(target)))return "RL_DigBadExit".Translate();
                    if(target.GetEdifice(owner.map) is {} e&&e is not Mineable)return "RL_BlockedLanding".Translate();
                }
            }
            return true;
        }
        foreach (var cell in GenAdj.OccupiedRect(center, rotation, buildable.Size))
        {
            if (!owner.TryDecode(cell, out var logical)) return "RL_NoSurface".Translate();
            if(owner.Elevation(logical.LayerId)<0&&buildable is ThingDef engineDef&&Compatibility.VanillaGravshipCompatibility.EngineDef(engineDef))return "RL_DigShipUnsupported".Translate();
            if(owner.Elevation(logical.LayerId)<0&&buildable is TerrainDef {IsSubstructure:true})return "RL_DigShipUnsupported".Translate();
            if(owner.Elevation(logical.LayerId)>0&&buildable==TerrainDefOf.Substructure)
            {
                if(!owner.Underlays.CanHost(cell))return "RL_ShipBaseNeedsColonyFloor".Translate();
                continue;
            }
            if (slab)
            {
                if(owner.Mountains.IsNaturalFloor(cell))return "RL_NaturalFloorImmutable".Translate();
                if (logical.LayerId == 0) return "RL_UpperOnly".Translate();
                if (!(shipSlab?HasShipRoof(cell):HasRequiredRoof(cell))) return "RL_NeedsRoof".Translate();
                var core=owner.Ships.BuildOwner(cell);
                bool ship=core!=null&&owner.Ships.Get(core) is {} hull&&owner.Elevation(logical.LayerId)>hull.Bottom;
                if(shipSlab&&!ship)return "RL_ShipFloorOnly".Translate();
                if(!shipSlab&&owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure==true)return "RL_StandardFloorOnly".Translate();
                if(shipSlab&&HasSurface(cell)&&owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure!=true)return "RL_ShipDeckOccupied".Translate();
                if (!planning && !HasSurface(cell) && !GenAdj.AdjacentCells.Any(offset => HasSurface(cell + offset)))
                    return "RL_NeedsPlatform".Translate();
            }
            else if (!HasSurface(cell)) return "RL_NoSurface".Translate();
            if (stair)
            {
                if (owner.Elevation(logical.LayerId)>=LayerLayout.MaxElevation) return "RL_TopLayer".Translate();
                var upper=owner.Adjacent(cell,1);
                if (!ConnectorRoof(owner.map.roofGrid.RoofAt(cell)) && !(upper.IsValid&&owner.Mountains.IsNaturalFloor(upper)))return "RL_NeedsRoof".Translate();
                if(!upper.IsValid)continue;
                if (!HasRequiredRoof(upper)) return "RL_NeedsRoof".Translate();
                if (upper.GetEdifice(owner.map) is {} obstruction && obstruction is not Mineable) return "RL_BlockedLanding".Translate();
            }
        }
        return true;
    }

    internal void EnsureLanding(IntVec3 cell)
    {
        if (!HasRequiredRoof(cell)) throw new InvalidOperationException("Stair landing lost its supporting roof.");
        if (!HasSurface(cell))
        {if(owner.Ships.BuildOwner(cell)!=null)RimLayers.Ships.ShipDeckTerrain.Install(owner,cell,false);
         else owner.map.terrainGrid.SetTerrain(cell, RimLayersDefOf.RL_StructuralFloor);}
    }

    internal void TerrainChanged(IntVec3 cell, TerrainDef terrain)
    {
        if (!owner.TryDecode(cell, out var logical) || logical.LayerId == 0) return;
        bool changed = terrain == RimLayersDefOf.RL_StructuralFloor || owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure==true ? owner.StructuralCells.Add(cell) :
            (terrain == RimLayersDefOf.RL_OpenAir || terrain == RimLayersDefOf.RL_Isolation) && owner.StructuralCells.Remove(cell);
        if (changed)
        {
            owner.map.pathing.RecalculatePerceivedPathCostAt(cell);
            RoofChanged(cell);
        }
        // Connector edges read only their own columns; unrelated terrain writes must not
        // invalidate the graph, which would rebuild heat/air/environment topology wholesale.
        if(owner.Connectors.TouchesColumn(cell)) owner.Connectors.Invalidate();
        if(RimLayersMod.Settings.AutoRecovery)
            foreach(var thing in cell.GetThingList(owner.map).ToArray())RimLayers.Recovery.EntityRecoveryService.Current?.Notice(thing,owner.map,"Structure");
    }

    internal void RoofChanged(IntVec3 cell)
    {
        if (!owner.TryDecode(cell, out var logical)) return;
        // Invalidation is immediate; destructive cascade processing is an explicit simulation operation.
        bool affected=false;
        foreach (int layer in owner.Layout!.Above(logical.LayerId))
        {
            var upper = owner.ToStorage(new LayerCell(layer, logical.X, logical.Z));
            if (owner.StructuralCells.Contains(upper))
            {
                affected=true;
                if (!HasRequiredRoof(upper)) owner.PendingCollapse.Add(upper);
                owner.map.pathing.RecalculatePerceivedPathCostAt(upper);
            }
        }
        if(affected||owner.Connectors.TouchesColumn(cell)) owner.Connectors.Invalidate();
    }
}
