using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimWorld;
using Verse;
using Verse.Noise;
using UnityEngine;

namespace RimLayers.Underground;

public sealed class UndergroundState : IExposable
{
    public HashSet<IntVec3> Floors = new();
    public HashSet<int> Generated = new();
    public void ExposeData()
    {
        Scribe_Collections.Look(ref Floors,"floors",LookMode.Value);
        Scribe_Collections.Look(ref Generated,"generated",LookMode.Value);
        if(Scribe.mode==LoadSaveMode.PostLoadInit){Floors??=new();Generated??=new();}
    }
    internal UndergroundState Copy()=>new(){Floors=new(Floors),Generated=new(Generated)};
}

/// <summary>Native geology algorithms, bounded to an append-only underground band.</summary>
public sealed class UndergroundService
{
    private readonly LayerMapComponent owner;
    private UndergroundState state=new();
    public UndergroundService(LayerMapComponent owner)=>this.owner=owner;
    public bool IsNaturalFloor(IntVec3 cell)=>state.Floors.Contains(cell);
    public bool Generated(int layer)=>state.Generated.Contains(layer);
    internal UndergroundState Snapshot()=>state.Copy();
    internal void Restore(UndergroundState previous)=>state=previous;
    public void ExposeData(){Scribe_Deep.Look(ref state,"underground");state??=new();}

    public static AcceptanceReport TerrainAllows(Map map,IntVec3 cell)
    {
        if(!LiveMapExpansion.CanExpand(map)||map.Tile.Layer.Def.isSpace)return "RL_DigMapUnsupported".Translate();
        if(map.Biome.defName is "IceSheet" or "SeaIce")return "RL_DigIceBiome".Translate();
        var o=LayerMapComponent.For(map);
        int elevation=o!=null&&o.TryDecode(cell,out var c)?o.Elevation(c.LayerId):0;
        if(elevation>0)return "RL_DigFromGround".Translate();
        if(elevation<=LayerLayout.MinElevation)return "RL_DigBottom".Translate();
        if(!cell.InBounds(map)||o!=null&&!o.Structure.HasSurface(cell))return "RL_DigBadTerrain".Translate();
        var grid=map.terrainGrid;
        // Covers and foundations never grant excavation rights over an unsuitable substrate.
        TerrainDef terrain=grid.UnderTerrainAt(cell)??grid.TerrainAt(cell);
        if(grid.FoundationAt(cell)!=null)return "RL_DigBadTerrain".Translate();
        if(!DryLand(terrain))return "RL_DigBadTerrain".Translate();
        return true;
    }
    private static HashSet<TerrainDef>? stoneFloors;
    private static bool StoneFloor(TerrainDef terrain)=>(stoneFloors??=DefDatabase<ThingDef>.AllDefsListForReading.Where(d=>d.building?.isNaturalRock==true).SelectMany(d=>new TerrainDef?[]{d.building.naturalTerrain,d.building.leaveTerrain,d.building.naturalTerrain?.smoothedTerrain,d.building.leaveTerrain?.smoothedTerrain}).OfType<TerrainDef>().ToHashSet()).Contains(terrain);
    internal static bool DryLand(TerrainDef terrain)=>!terrain.IsWater&&!terrain.IsRiver&&
        terrain!=TerrainDefOf.Ice&&terrain!=RimLayersDefOf.RL_OpenAir&&terrain!=RimLayersDefOf.RL_Isolation&&
        terrain.defName is not ("LavaDeep" or "Space" or "AncientMegastructure")&&
        !terrain.defName.StartsWith("Bridge",StringComparison.Ordinal)&&
        (terrain.natural||StoneFloor(terrain)||terrain.affordances.Contains(DefDatabase<TerrainAffordanceDef>.GetNamed("Diggable")));

    internal List<Action> InitializeAddedLayers(int first,List<Thing> spawned)
    {
        var afterRegions=new List<Action>();
        for(int id=first;id<owner.Layout!.LayerCount;id++)
            if(owner.Elevation(id)<0&&!state.Generated.Contains(id))Generate(id,spawned,afterRegions);
        return afterRegions;
    }
    internal void InitializeClimate(int first)
    {
        foreach(var room in owner.map.regionGrid.AllRooms)
            if(room.Cells.Any(c=>owner.TryDecode(c,out var q)&&q.LayerId>=first&&owner.Elevation(q.LayerId)<0))room.Temperature=15;
    }
    private sealed class OrePicker:GenStep_ScatterLumpsMineable
    {public ThingDef? Pick()=>ChooseThingDef();}
    private void Generate(int id,List<Thing> spawned,List<Action> afterRegions)
    {
        var map=owner.map;var layout=owner.Layout!;var rect=owner.LayerRect(id);
        var before=map.listerThings.AllThings.ToHashSet();
        var oldMap=MapGenerator.mapBeingGenerated;
        var field=AccessTools.Field(typeof(MapGenerator),"data");var oldData=field.GetValue(null);
        var data=new Dictionary<string,object>();
        var oldRocks=RockNoises.rockNoises;
        Rand.PushState(Gen.HashCombineInt(Gen.HashCombineInt(map.ConstantRandSeed,0x524c5547),owner.Elevation(id)));
        try
        {
            MapGenerator.mapBeingGenerated=map;field.SetValue(null,data);RockNoises.Init(map);
            var caves=MapGenerator.Caves;var elevation=MapGenerator.Elevation;
            foreach(var cell in rect)elevation[cell]=1f; // Native underground elevation.
            var parms=MapGenCavesUtility.CaveGenParms.Default;
            var worker=DefDatabase<TileMutatorDef>.GetNamed("UndergroundCave").Worker;
            float Property(string name)=>(float)AccessTools.PropertyGetter(typeof(TileMutatorWorker_UndergroundCave),name).Invoke(worker,null)!;
            parms.widthOffsetPerCell=Property("WidthOffsetPerCell");
            parms.minTunnelWidth=Property("MinTunnelWidth");parms.branchChance=Property("BranchChance");
            parms.allowBranchingAfterThisManyCells=10;
            var noise=new Perlin(0.002050000010058284,2,.5,4,Rand.Int,QualityMode.Medium);
            var group=rect.Cells.ToList();var visited=new HashSet<IntVec3>();
            bool Rock(IntVec3 at)=>rect.Contains(at);
            // Same closed-tunnel primitive as UndergroundCave; bound its target for small maps.
            int target=RimLayersMod.Settings.Caves?Math.Min(2000,rect.Area/3):0;
            for(int attempt=0;attempt<100&&visited.Count<target;attempt++)
                MapGenCavesUtility.Dig(group.RandomElement(),Rand.Range(0f,360f),Rand.Range(3.2f,4f),
                    group,map,true,noise,parms,Rock,visited);

            var ore=new Dictionary<IntVec3,ThingDef>();
            var profile=(GenStep_RocksFromGrid)DefDatabase<GenStepDef>.GetNamed("Underground_RocksFromGrid").genStep;
            float density=(float?)AccessTools.Field(typeof(GenStep_RocksFromGrid),"overrideBlotchesPer10kCells").GetValue(profile)??GenStep_RocksFromGrid.GetResourceBlotchesPer10KCellsForMap(map);
            if(!map.IsStartingMap)density*=Find.Storyteller.difficulty.nomadicMineableResourcesFactor;
            density*=RimLayersMod.Settings.Minerals;
            var picker=new OrePicker{maxValue=(float)AccessTools.Field(typeof(GenStep_RocksFromGrid),"maxMineableValue").GetValue(profile)!};
            int count=density<=0?0:Mathf.RoundToInt((float)rect.Area/Math.Max(1,Mathf.RoundToInt(10000f/density)));
            var centers=group.Where(c=>caves[c]<=0).ToList();centers.Shuffle();var chosen=new List<IntVec3>();
            foreach(var center in centers)
            {
                if(chosen.Count>=count)break;
                if(ore.ContainsKey(center)||chosen.Any(c=>(c-center).LengthHorizontalSquared<25))continue;
                var def=picker.Pick();if(def==null)break;
                foreach(var at in GridShapeMaker.IrregularLump(center,map,def.building.mineableScatterLumpSizeRange.RandomInRange,
                    c=>rect.Contains(c)&&caves[c]<=0&&!ore.ContainsKey(c)))ore[at]=def;
                chosen.Add(center);
            }
            var under=AccessTools.Field(typeof(TerrainGrid),"underGrid").GetValue(map.terrainGrid) as TerrainDef[];
            foreach(var cell in group)
            {
                owner.TryDecode(cell,out var local);
                var rock=GenStep_RocksFromGrid.RockDefAt(new(local.X,0,local.Z));
                state.Floors.Add(cell);
                map.terrainGrid.SetTerrain(cell,rock.building.naturalTerrain);under![map.cellIndices.CellToIndex(cell)]=null!;
                map.roofGrid.SetRoof(cell,RoofDefOf.RoofRockThick);
                if(caves[cell]<=0)GenSpawn.Spawn(ore.GetValueOrDefault(cell)??rock,cell,map);
                map.fogGrid.Refog(CellRect.SingleCell(cell));
            }
            var caveCells=group.Where(c=>caves[c]>0).ToList();
            int hiveSeed=Rand.Int;
            afterRegions.Add(()=>{Rand.PushState(hiveSeed);try{SpawnCaveHives(id,group,caveCells);}finally{Rand.PopState();}});
            state.Generated.Add(id);
            Log.Message($"[RimLayers] Underground {owner.Elevation(id)} generated: {rect.Area} cells, {visited.Count} cave cells, {ore.Count} mineral cells.");
        }
        finally
        {
            foreach(var thing in map.listerThings.AllThings.Where(t=>!before.Contains(t)).ToArray())spawned.Add(thing);
            foreach(var grid in data.Values.OfType<MapGenFloatGrid>())grid.Dispose();
            field.SetValue(null,oldData);MapGenerator.mapBeingGenerated=oldMap;RockNoises.rockNoises=oldRocks;
            Rand.PopState();
        }
    }
    private void SpawnCaveHives(int id,List<IntVec3> cells,List<IntVec3> caveCells)
    {
        if(!RimLayersMod.Settings.CaveHives||!Find.Storyteller.difficulty.allowCaveHives||Faction.OfInsects==null||
            Find.Scenario.AllParts.Any(p=>p.def.genStep?.defName=="CaveHives"))return;
        var step=new GenStep_CaveHives();
        var rect=owner.LayerRect(id);
        var candidates=caveCells.Where(c=>rect.ContractedBy(10).Contains(c)).ToList();
        var excluded=cells.Where(c=>c.GetThingList(owner.map).Any(t=>t.Faction!=null)).ToList();
        GenMorphology.Dilate(excluded,50,owner.map);
        candidates.RemoveAll(excluded.Contains);
        AccessTools.Field(typeof(GenStep_CaveHives),"possibleSpawnCells").SetValue(step,candidates);
        var spawn=AccessTools.Method(typeof(GenStep_CaveHives),"TrySpawnHive");
        int count=GenMath.RoundRandom(caveCells.Count/1000f);
        for(int i=0;i<count;i++)spawn.Invoke(step,new object[]{owner.map});
    }
}
