using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Structure;

public sealed class StructuralSupport
{
    private readonly LayerMapComponent owner;
    public StructuralSupport(LayerMapComponent owner) => this.owner = owner;

    // A third-party ship framework keeps its own hull model and must never be folded into RimLayers'
    // Odyssey gravship path (Ships/ShipStructureService owns that model exclusively). What it can
    // share are the two predicates that decide whether a cell above it is buildable: "is this cell a
    // deck?" and "does this roof count as a floor for the deck above?". Registering them here keeps
    // ownership untouched while letting an upper deck sit on a foreign hull.
    private static readonly List<Func<LayerMapComponent, IntVec3, bool>> deckSources = new();
    private static readonly List<Func<RoofDef?, bool>> supportRoofs = new();

    /// <summary>Register an extra "this cell is a walkable deck" source. Called per cell; keep it cheap.</summary>
    public static void RegisterDeckSource(Func<LayerMapComponent, IntVec3, bool> source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (!deckSources.Contains(source)) deckSources.Add(source);
    }

    /// <summary>Register a roof that may support the deck above it, in addition to the native ones.</summary>
    public static void RegisterSupportRoof(Func<RoofDef?, bool> supported)
    {
        if (supported == null) throw new ArgumentNullException(nameof(supported));
        if (!supportRoofs.Contains(supported)) supportRoofs.Add(supported);
    }

    private bool HasDeckSource(IntVec3 cell)
    {
        for (int i = 0; i < deckSources.Count; i++) if (deckSources[i](owner, cell)) return true;
        return false;
    }

    internal static bool IsSupportRoof(RoofDef? roof)
    {
        if (SupportedRoof(roof)) return true;
        for (int i = 0; i < supportRoofs.Count; i++) if (supportRoofs[i](roof)) return true;
        return false;
    }

    public bool HasRequiredRoof(IntVec3 upper)
    {
        if (!owner.TryDecode(upper, out var cell)) return false;
        if (cell.LayerId == 0 || owner.Mountains.IsNaturalFloor(upper)) return true;
        IntVec3 lower = owner.Adjacent(upper,-1);
        if(!lower.IsValid)return false;
        bool supported = IsSupportRoof(owner.map.roofGrid.RoofAt(lower)) || HasDeckSource(lower);
        return supported &&
            (owner.Elevation(cell.LayerId) == 1 || owner.map.terrainGrid.FoundationAt(upper)?.IsSubstructure==true || HasSurface(lower));
    }

    internal bool HasShipRoof(IntVec3 upper)
    {
        if(!owner.TryDecode(upper,out var at)||owner.Elevation(at.LayerId)<=0)return false;
        return IsSupportRoof(owner.map.roofGrid.RoofAt(owner.Adjacent(upper,-1)));
    }
    public bool HasSurface(IntVec3 cell) =>
        owner.TryDecode(cell, out var logical) && owner.map.terrainGrid.TerrainAt(cell)!=RimLayersDefOf.RL_OpenAir &&
        (logical.LayerId == 0 || owner.Mountains.IsNaturalFloor(cell) || owner.StructuralCells.Contains(cell) && HasRequiredRoof(cell) ||
         RimLayers.Vehicles.VehicleFacilitySpace.HasDeck(owner,cell) || HasDeckSource(cell));


    /// <summary>
    /// The SOS2 deck floor, resolved by name and cached, including the miss.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="RimLayersDefOf"/> field. The def is injected by a
    /// <c>PatchOperationConditional</c> that only fires when SOS2's own floors exist, and
    /// <c>DefOfHelper.EnsureInitializedInCtor</c> throws for a field whose def is absent - which would
    /// take the whole mod down on every configuration without SOS2.
    /// </remarks>
    internal static TerrainDef? Sos2Deck=>sos2Deck??=DefDatabase<TerrainDef>.GetNamedSilentFail("RL_Sos2Deck");
    private static TerrainDef? sos2Deck;

    /// <summary>
    /// True for every floor this mod treats as a structural deck: the plain upper floor, the Odyssey
    /// gravship deck, and the SOS2 hull deck. Identity is by def, not by the <c>Substructure</c> tag,
    /// because that tag makes SOS2 detach its own hull plating (CompShipEngineBreakdownable) and is
    /// also false whenever Odyssey is not loaded.
    /// </summary>
    internal static bool IsDeckTerrain(TerrainDef? terrain)
    {
        if (terrain == null) return false;
        return terrain == RimLayersDefOf.RL_StructuralFloor || IsSos2DeckTerrain(terrain);
    }

    /// <summary>True only for the Save Our Ship 2 deck, whose ship is owned by SOS2 rather than RimLayers.</summary>
    internal static bool IsSos2DeckTerrain(TerrainDef? terrain) => terrain != null && terrain == Sos2Deck;

    internal static bool SupportedRoof(RoofDef? roof)=>roof==RoofDefOf.RoofConstructed||roof==RoofDefOf.RoofRockThin;
    // A connector roof decides whether a stair or lift may break through the ceiling above it, so it
    // has to accept exactly what the deck above would accept as support. Keeping this on SupportedRoof
    // made a registered support roof (a third-party ship's RoofShip) open a deck above but never allow
    // the stair that reaches it, which is the one case that deck is built for.
    internal static bool ConnectorRoof(RoofDef? roof)=>IsSupportRoof(roof)||roof==RoofDefOf.RoofRockThick;

    public TerrainDef SlabAt(IntVec3 cell)=>owner.map.terrainGrid.FoundationAt(cell) is {} foundation&&
        (IsDeckTerrain(foundation)||foundation.IsSubstructure)?
        foundation:RimLayersDefOf.RL_StructuralFloor;
    public bool IsBareSlab(IntVec3 cell) => owner.StructuralCells.Contains(cell) &&
        owner.map.terrainGrid.TempTerrainAt(cell) == null &&
        (IsDeckTerrain(owner.map.terrainGrid.TerrainAt(cell)) ||
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
        bool sos2Slab=buildable.defName=="RL_Sos2Deck";
        bool slab = buildable == RimLayersDefOf.RL_StructuralFloor||shipSlab||sos2Slab;
        // SOS2's own parts are not floors: a hull hardpoint on an upper deck hangs off the vessel
        // exactly like SOS2 allows on the ground band, and in space there is no air to stand on.
        // Handing the decision back to SOS2 here keeps vacuum rules inside the mod that owns them.
        if(!slab&&Compatibility.Sos2Compatibility.AllowsHullPartInVacuum(owner,buildable,center,rotation))return true;
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
                if (sos2Slab)
                {
                    // An SOS2 deck belongs to an SOS2 hull and nothing else. Both halves are checked
                    // explicitly instead of leaning on HasRequiredRoof: that predicate accepts any
                    // registered support roof, while a ship deck has to sit under the ship's own
                    // ceiling and above a cell SOS2 itself counts as part of the ship.
                    IntVec3 hullCell=owner.Adjacent(cell,-1);
                    if(!hullCell.IsValid||!Compatibility.Sos2Compatibility.IsShipDeck(owner,hullCell))return "RL_Sos2DeckNeedsHull".Translate();
                    if(!Compatibility.Sos2Compatibility.IsShipCeiling(owner.map.roofGrid.RoofAt(hullCell)))return "RL_Sos2DeckNeedsShipRoof".Translate();
                }
                else if (!(shipSlab?HasShipRoof(cell):HasRequiredRoof(cell))) return "RL_NeedsRoof".Translate();
                var core=owner.Ships.BuildOwner(cell);
                bool ship=core!=null&&owner.Ships.Get(core) is {} hull&&owner.Elevation(logical.LayerId)>hull.Bottom;
                if(shipSlab&&!ship)return "RL_ShipFloorOnly".Translate();
                if(!shipSlab&&!sos2Slab&&owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure==true)return "RL_StandardFloorOnly".Translate();
                if(shipSlab&&HasSurface(cell)&&owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure!=true)return "RL_ShipDeckOccupied".Translate();
                if(sos2Slab&&owner.map.terrainGrid.FoundationAt(cell) is {} existingFloor&&!IsDeckTerrain(existingFloor))return "RL_ShipDeckOccupied".Translate();
                if (!planning && !HasSurface(cell) && !GenAdj.AdjacentCells.Any(offset => HasSurface(cell + offset)))
                {
                    // A foreign deck source (an SOS2 hull) is not an RL_StructuralFloor and owns no
                    // RimLayers engine, so neither the surface test nor the engine-core test can see
                    // it. The deck directly below is the authority for "is there a platform here".
                    IntVec3 under=owner.Adjacent(cell,-1);
                    if(!under.IsValid||!HasDeckSource(under))return "RL_NeedsPlatform".Translate();
                }
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
         // A landing opened over an SOS2 hull is that hull's deck, not an Odyssey gravship floor:
         // it reports to no engine and must not claim substructure capacity.
         else if(Sos2Deck is {} deck&&Compatibility.Sos2Compatibility.IsShipDeck(owner,owner.Adjacent(cell,-1)))RimLayers.Ships.ShipDeckTerrain.InstallDeck(owner,cell,deck);
         else owner.map.terrainGrid.SetTerrain(cell, RimLayersDefOf.RL_StructuralFloor);}
    }

    /// <summary>
    /// Runs <paramref name="action"/>, and while it runs per-cell path-cost refreshes become bookkeeping
    /// only. The structural sets are still updated; what is skipped is the write into the map's path
    /// grid, which is the single operation in this class that reaches into other mods.
    /// </summary>
    /// <remarks>
    /// This exists for moves. A hull leaving a map rewrites every deck cell, and each write would
    /// otherwise recost that cell - thousands of refreshes on a grid the map is about to abandon. It is
    /// also the only way to make the source map safe: Vehicle Framework postfixes
    /// <c>Pathing.RecalculatePerceivedPathCostAt</c> and reads per-map vehicle pathing context, which a
    /// departing map never had, so the call throws <c>AccessViolationException</c> out of
    /// <c>PathGrid.RecalcuateCellDirect</c> and aborts the ship move halfway through.
    /// </remarks>
    internal static IDisposable Settling() => new SettleScope();

    private static int settling;

    /// <summary>
    /// True while moves are rewriting structure. Every patch that would refresh a cell's path cost has
    /// to consult this, not just the ones in this class: <c>ShipFoundationRemovalCommit</c> postfixes
    /// <c>TerrainGrid.RemoveFoundation</c> directly and never passes through here.
    /// </summary>
    internal static bool SettlingNow => settling > 0;

    private sealed class SettleScope : IDisposable
    {
        internal SettleScope() => settling++;
        public void Dispose() => settling--;
    }

    private void Recost(IntVec3 cell)
    {
        if (!SettlingNow) owner.map.pathing.RecalculatePerceivedPathCostAt(cell);
    }

    internal void TerrainChanged(IntVec3 cell, TerrainDef? terrain)
    {
        if (!owner.TryDecode(cell, out var logical) || logical.LayerId == 0) return;
        // Skipping vanilla's DoTerrainChangedEffects skips its mesh invalidation too, and that is the
        // first thing the method does. Without this the old floor keeps its baked texture: a hull that
        // moves away leaves its outline painted on the map, and the outline fills in when that band is
        // rebuilt later. Callers that suppress the chain rely on this to keep the screen honest.
        owner.map.mapDrawer?.MapMeshDirty(cell, MapMeshFlagDefOf.Terrain | MapMeshFlagDefOf.Things, true, true);
        bool changed = IsDeckTerrain(terrain) || owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure==true ? owner.StructuralCells.Add(cell) :
            (terrain == RimLayersDefOf.RL_OpenAir || terrain == RimLayersDefOf.RL_Isolation) && owner.StructuralCells.Remove(cell);
        if (changed)
        {
            Recost(cell);
            RoofChanged(cell);
        }
        // Connector edges read only their own columns; unrelated terrain writes must not
        // invalidate the graph, which would rebuild heat/air/environment topology wholesale.
        if(owner.Connectors.TouchesColumn(cell)) owner.Connectors.Invalidate();
        // The deck above a hull belongs to that hull. SOS2 derives ship membership from buildings, so
        // a bare deck cell never enters its cache on its own; see Sos2DeckRegistry.
        if (IsSos2DeckTerrain(terrain) || IsSos2DeckTerrain(owner.map.terrainGrid.TerrainAt(cell)) ||
            IsSos2DeckTerrain(owner.map.terrainGrid.FoundationAt(cell)))
            Compatibility.Sos2DeckRegistry.SyncCell(owner.map, cell);
        // SettlingNow means another system is already moving this grid's contents: an SOS2 hull is
        // being carried to another map, or a deck is being torn out from under a ship. Recording
        // recovery entries there is wrong twice over - the hull parts belong to the vessel that is
        // leaving, and the ones we did not carry are about to be deleted along with the map. Left in,
        // they fall back onto the source band and get stranded where the ship used to be.
        if(RimLayersMod.Settings.AutoRecovery&&!SettlingNow)
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
                Recost(upper);
            }
        }
        if(affected||owner.Connectors.TouchesColumn(cell)) owner.Connectors.Invalidate();
    }
}
