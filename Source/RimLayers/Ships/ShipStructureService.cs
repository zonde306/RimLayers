using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Ships;

public sealed record ShipStructure(Building_GravEngine Engine,HashSet<IntVec3> Cells,bool Ambiguous,int Bottom,int Top);

/// <summary>Persistent cell identity plus raw topology never queries native ValidSubstructure or active facilities: no extender recursion.</summary>
public sealed class ShipStructureService
{
    private readonly LayerMapComponent owner;
    private readonly Dictionary<Building_GravEngine,ShipStructure> ships=new();
    // cell -> owning core, maintained alongside ships so OwnerAt is O(1) instead of a per-cell scan.
    private readonly Dictionary<IntVec3,Building_GravEngine> cellOwners=new();
    private bool dirty=true,flushing;
    private static readonly Action<CompAffectedByFacilities> Relink=AccessTools.MethodDelegate<Action<CompAffectedByFacilities>>(AccessTools.Method(typeof(CompAffectedByFacilities),"RelinkAll"));
    public ShipStructureService(LayerMapComponent owner){this.owner=owner;owner.Changed+=(_,kind)=>{if((kind&LayerChangeKind.Structure)!=0)MarkDirty();};}
    public void MarkDirty()=>dirty=true;
    public static bool Core(Building_GravEngine e)=>ModsConfig.OdysseyActive&&e.Spawned&&!e.Destroyed&&Compatibility.VanillaGravshipCompatibility.EngineDef(e.def)&&e.Faction==Faction.OfPlayer;
    private Dictionary<IntVec3,int> claims=new();
    internal int SelectedCore;
    private List<IntVec3>? claimKeys;private List<int>? claimValues;
    internal void ExposeData()
    {
        Scribe_Values.Look(ref SelectedCore,"shipBuildTarget");
        Scribe_Collections.Look(ref claims,"shipCellOwners",LookMode.Value,LookMode.Value,ref claimKeys,ref claimValues);
        if(Scribe.mode==LoadSaveMode.PostLoadInit){claims??=new();MarkDirty();}
    }
    internal int ClaimAt(IntVec3 c)=>claims.GetValueOrDefault(c);
    internal void Assign(IntVec3 c,int engineId){claims[c]=engineId;MarkDirty();}
    internal void Forget(IntVec3 c){claims.Remove(c);MarkDirty();}
    private bool Deck(IntVec3 c)=>!owner.Mountains.IsNaturalFloor(c)&&owner.TryDecode(c,out var l)&&
        owner.map.terrainGrid.FoundationAt(c)?.IsSubstructure==true&&(l.LayerId==0||owner.Structure.HasSurface(c));
    private void Refresh()
    {
        if(!dirty||!owner.Managed||!ModsConfig.OdysseyActive)return;
        dirty=false;ships.Clear();cellOwners.Clear();
        var engines=Compatibility.VanillaGravshipCompatibility.Engines(owner.map).Where(Core).ToArray();
        if(engines.Length==0)return;
        var decks=new Dictionary<LayerCell,int>();var barriers=new HashSet<LayerCell>();
        // Only above-ground bands can hold decks; scan per layer so the 17-row isolation padding and
        // every underground floor are never visited. AllCells visited every storage cell instead.
        var layout=owner.Layout!;
        foreach(int layer in layout.OrderedLayers)
        {
            if(owner.Elevation(layer)<0)continue;
            foreach(var c in owner.LayerRect(layer))
            {
                if(!owner.TryDecode(c,out var stored))continue;
                var l=new LayerCell(owner.Elevation(stored.LayerId),stored.X,stored.Z);
                if(Deck(c))decks[l]=claims.GetValueOrDefault(c);
                else if(owner.StructuralCells.Contains(c)||owner.Mountains.IsNaturalFloor(c)||
                    c.GetEdifice(owner.map)!=null)barriers.Add(l);
            }
        }
        var seeds=new Dictionary<int,HashSet<LayerCell>>();
        foreach(var engine in engines)
        {
            var footprint=engine.OccupiedRect().ToArray();
            if(!footprint.All(Deck))continue;
            var set=new HashSet<LayerCell>();
            foreach(var c in footprint)
            {
                owner.TryDecode(c,out var stored);
                var l=new LayerCell(owner.Elevation(stored.LayerId),stored.X,stored.Z);
                // The core's complete footprint is its explicit seed, not a proximity claim.
                if(decks[l]!=0&&decks[l]!=engine.thingIDNumber){set.Clear();break;}
                decks[l]=engine.thingIDNumber;set.Add(l);
            }
            if(set.Count>0)seeds[engine.thingIDNumber]=set;
        }
        var result=ShipTopology.Resolve(decks,barriers,seeds);
        var coreConflicts=engines.Where(e=>e.OccupiedRect().Any(c=>claims.TryGetValue(c,out int id)&&id!=e.thingIDNumber))
            .SelectMany(e=>e.OccupiedRect().Select(c=>claims.GetValueOrDefault(c))).ToHashSet();
        foreach(var engine in engines)
        {
            if(!result.Ships.TryGetValue(engine.thingIDNumber,out var logical)||logical.Count==0)continue;
            var cells=logical.Select(c=>owner.AtElevation(c.LayerId,c.X,c.Z)).ToHashSet();
            bool ambiguous=result.Conflicts.Contains(engine.thingIDNumber)||coreConflicts.Contains(engine.thingIDNumber);
            ships[engine]=new(engine,cells,ambiguous,logical.Min(c=>c.LayerId),logical.Max(c=>c.LayerId));
            // Reverse index: OwnerAt ran a LINQ scan over every ship for every cell, and it is called
            // per cell while roof surfaces are rebuilt. Ambiguous ships are not addressable.
            if(ambiguous)continue;
            foreach(var cell in cells)cellOwners[cell]=engine;
        }
    }
    public ShipStructure? Get(Building_GravEngine engine){Refresh();return ships.GetValueOrDefault(engine);}
    public Building_GravEngine? OwnerAt(IntVec3 cell)
    {
        Refresh();
        return cellOwners.GetValueOrDefault(cell);
    }
    public Building_GravEngine? BuildOwner(IntVec3 cell)
    {
        var current=OwnerAt(cell);if(current!=null)return current;
        if(!owner.TryDecode(cell,out var l)||owner.Elevation(l.LayerId)<0)return null;
        var candidates=new HashSet<Building_GravEngine>();
        foreach(var d in GenAdj.CardinalDirections)
            if(owner.TryDecode(cell+d,out var n)&&n.LayerId==l.LayerId&&OwnerAt(cell+d) is {} adjacent)candidates.Add(adjacent);
        foreach(int layer in owner.Layout!.Below(l.LayerId).Where(id=>owner.Elevation(id)>=0))
        {
            var below=owner.ToStorage(new(layer,l.X,l.Z));
            if(OwnerAt(below) is {} lower){candidates.Add(lower);break;}
            if(owner.StructuralCells.Contains(below)||owner.Mountains.IsNaturalFloor(below)||
                owner.map.terrainGrid.FoundationAt(below)?.IsSubstructure==true)break;
        }
        var selected=ships.Keys.FirstOrDefault(e=>e.thingIDNumber==SelectedCore)??Find.Selector.SelectedObjects.OfType<Building_GravEngine>().SingleOrDefault();
        if(claims.TryGetValue(cell,out int reserved)&&reserved!=0)
            return candidates.FirstOrDefault(e=>e.thingIDNumber==reserved);
        if(selected!=null&&candidates.Contains(selected))return selected;
        return candidates.Count==1?candidates.Single():null;
    }
    public float Distance(IntVec3 a,IntVec3 b)
    {if(!owner.TryDecode(a,out var x)||!owner.TryDecode(b,out var y))return float.PositiveInfinity;return MathF.Sqrt((x.X-y.X)*(x.X-y.X)+(x.Z-y.Z)*(x.Z-y.Z));}
    public bool Belongs(Building_GravEngine engine,Thing thing)=>Get(engine) is {Ambiguous:false} s&&thing.Spawned&&thing.Map==owner.map&&
        (thing.def.building?.isAttachment==true?GenConstruct.GetWallAttachedTo(thing) is {} wall&&s.Cells.Contains(wall.Position):thing.OccupiedRect().All(s.Cells.Contains));
    public bool InField(Building_GravEngine engine,IntVec3 cell)
    {
        var core=engine.GetComp<CompSubstructureFootprint>();
        if(core!=null&&Distance(cell,engine.Position)<=core.Props.radius)return true;
        // Extenders require loose ownership only. Native link list enforces global maxSimultaneous.
        foreach(var t in engine.AffectedByFacilities.LinkedFacilitiesListForReading)
            if(t.TryGetComp<CompSubstructureFootprint>() is {} f&&t.TryGetComp<CompGravshipFacility>() is {} c&&
               c.Props.onlyRequiresLooseConnection&&Belongs(engine,t)&&Distance(t.Position,engine.Position)<=c.Props.maxDistance&&
               (t.TryGetComp<CompPowerTrader>() is not {} power||power.PowerOn)&&Distance(cell,t.Position)<=f.Props.radius)return true;
        return false;
    }
    // NOTE: there used to be a `CanConnect(engine,thing,loose)` here built on InField. It was deleted
    // because it encoded the wrong rule: InField uses the engine footprint radius (VGE_GravjumperEngine
    // 7.9) while facilities allow maxDistance 200, so any facility more than a few cells from the engine
    // read "not connected". Facility link/active tests now live in Ships/ShipFacilityPatches.cs and use
    // logical distance plus the substructure under the footprint.
    public AcceptanceReport LaunchReport(Building_GravEngine engine)
    {
        var s=Get(engine);if(s==null)return true;
        if(s.Ambiguous)return "RL_ShipMultipleCores".Translate();
        if(s.Top-s.Bottom+1>6)return "RL_SettingsShipHeight".Translate();
        int capacity=(int)engine.GetStatValue(StatDefOf.SubstructureSupport);
        if(Compatibility.VanillaGravshipCompatibility.SupportCost(owner.map,s.Cells)>capacity)return "RL_ShipOverCapacity".Translate(Compatibility.VanillaGravshipCompatibility.SupportCost(owner.map,s.Cells),capacity);
        if(s.Cells.Any(c=>!InField(engine,c)))return "RL_ShipOutsideField".Translate();
        return true;
    }
    private void AdoptRebuiltCores()
    {
        var existing=Compatibility.VanillaGravshipCompatibility.Engines(owner.map).ToArray();
        foreach(var core in existing.Where(Core))
        {
            // A newly built replacement may inherit one orphan hull; an existing ship may not annex it.
            if(claims.Values.Contains(core.thingIDNumber)||!core.OccupiedRect().All(Deck))continue;
            var ids=core.OccupiedRect().Select(ClaimAt).Distinct().ToArray();
            if(ids.Length!=1||ids[0]==0||existing.Any(e=>e.thingIDNumber==ids[0]))continue;
            int oldId=ids[0];
            foreach(var cell in claims.Where(p=>p.Value==oldId).Select(p=>p.Key).ToArray())claims[cell]=core.thingIDNumber;
            owner.Underlays.RebindCore(oldId,core.thingIDNumber);
            if(SelectedCore==oldId)SelectedCore=core.thingIDNumber;
        }
    }
    public void Tick()
    {
        if(!dirty||flushing||!owner.Managed||!ModsConfig.OdysseyActive)return;
        flushing=true;
        try
        {
            foreach(var stale in claims.Keys.Where(c=>owner.map.terrainGrid.FoundationAt(c)?.IsSubstructure!=true&&
                !c.GetThingList(owner.map).Any(t=>t is Blueprint or Frame&&t.def.entityDefToBuild is TerrainDef {IsSubstructure:true})).ToArray())claims.Remove(stale);
            AdoptRebuiltCores();
            Refresh();
            foreach(var ship in ships.Values.Where(s=>!s.Ambiguous).ToArray())
            {
                foreach(var c in ship.Cells)claims[c]=ship.Engine.thingIDNumber;
                Relink(ship.Engine.AffectedByFacilities);ship.Engine.ForceSubstructureDirty();
            }
        }
        finally{flushing=false;}
    }
}

internal static class ShipDeckTerrain
{
    internal static TerrainDef Def=>DefDatabase<TerrainDef>.GetNamed("RL_ShipStructuralFloor");
    // Foundation setters reject an under terrain. Normalize only this cell, preserving its finish, cover and colour.
    internal static void Install(LayerMapComponent owner,IntVec3 cell,bool preserve)
    {
        var grid=owner.map.terrainGrid;var top=grid.TopTerrainAt(cell);var under=grid.UnderTerrainAt(cell);var temp=grid.TempTerrainAt(cell);var color=grid.ColorAt(cell);
        grid.SetTerrain(cell,RimLayersDefOf.RL_OpenAir);grid.SetFoundation(cell,Def);
        if(preserve&&under!=null&&top!=RimLayersDefOf.RL_StructuralFloor)
        {grid.SetTerrain(cell,top);grid.SetUnderTerrain(cell,under==RimLayersDefOf.RL_StructuralFloor?RimLayersDefOf.RL_OpenAir:under);}
        if(preserve&&temp!=null)grid.SetTempTerrain(cell,temp);
        if(preserve&&color!=null)grid.SetTerrainColor(cell,color);
        owner.StructuralCells.Add(cell);owner.Structure.RoofChanged(cell);
        owner.Invalidate(cell,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Light);
    }
    /// <summary>All deck cells of a layered ship, or null when the engine has no layer structure.</summary>
    internal static HashSet<IntVec3>? DeckCells(Building_GravEngine engine)=>
        LayerMapComponent.For(engine.Map)?.Ships.Get(engine) is {Ambiguous:false} ship&&ship.Cells.Count>0?ship.Cells:null;
}
