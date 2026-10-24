using RimLayers.Core;
using RimLayers.Environment;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Underground;

/// <summary>What one underground layer received, so a save can be audited without rescanning the map.</summary>
public sealed class EcologyGenerationRecord : IExposable
{
    public int Version=1, Layer, Seed, SoilCells, Geysers, Flora;
    public Dictionary<string,int> Plants=new();
    public void ExposeData()
    {
        Scribe_Values.Look(ref Version,"version",1);
        Scribe_Values.Look(ref Layer,"layer");
        Scribe_Values.Look(ref Seed,"seed");
        Scribe_Values.Look(ref SoilCells,"soilCells");
        Scribe_Values.Look(ref Geysers,"geysers");
        Scribe_Values.Look(ref Flora,"flora");
        Scribe_Collections.Look(ref Plants,"plants",LookMode.Value,LookMode.Value);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)Plants??=new();
    }
    internal EcologyGenerationRecord Copy()=>new()
    {
        Version=Version,Layer=Layer,Seed=Seed,SoilCells=SoilCells,Geysers=Geysers,Flora=Flora,Plants=new(Plants)
    };
}

/// <summary>
/// Underground soil, steam geysers and cave flora, built from vanilla definitions only.
/// No XML is added or patched: a missing DLC or mod simply drops its plants from the pool.
/// </summary>
internal static class UndergroundEcology
{
    // Absolute selection weights. 0 removes a plant; the mod settings override these.
    private static readonly (string DefName,float Weight)[] BasePool=
    {
        ("Glowstool",5f),          // RawFungus, glow radius 5.9
        ("Agarilux",2f),           // RawFungus x35, glow radius 10
        ("Bryolux",2f),            // light only
        ("Plant_Timbershroom",3f), // WoodLog x25
        ("Plant_Psilocap",1.5f),   // Odyssey: Drug_Psilocap x5
        ("Boomshroom",1f),         // Odyssey: Chemfuel x8
        ("Plant_Willowgill",1.5f)  // Odyssey: light only
    };

    internal static float BaseWeight(ThingDef def)
    {
        foreach(var entry in BasePool)if(entry.DefName==def.defName)return entry.Weight;
        return 1f;
    }

    /// <summary>Loaded vanilla cave plants, in a stable order so the menu does not shuffle.</summary>
    internal static List<ThingDef> PlantPool()
    {
        var pool=new List<ThingDef>();
        foreach(var (name,weight) in BasePool)
        {
            if(weight<=0)continue;
            var def=DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if(def?.plant==null)continue; // Absent DLC/mod: silently skipped.
            pool.Add(def);
        }
        return pool;
    }

    /// <summary>
    /// Gravel patches that make vanilla cave plants legitimately sowable. Only cave floor qualifies,
    /// so solid rock keeps its mineable and lava keeps its heat.
    /// </summary>
    internal static HashSet<IntVec3> PlanSoil(LayerMapComponent owner,int layer,List<IntVec3> cells,
        MapGenFloatGrid caves,UndergroundGeology? geology,float coverage,out int seed)
    {
        seed=Gen.HashCombineInt(Gen.HashCombineInt(owner.map.ConstantRandSeed,0x524c534f),owner.Elevation(layer));
        var soil=new HashSet<IntVec3>();
        if(coverage<=0)return soil;
        int depth=Math.Abs(owner.Elevation(layer));
        var candidates=new List<IntVec3>();
        var values=new List<double>();
        foreach(var cell in cells)
        {
            if(caves[cell]<=0)continue;
            if(geology!=null&&(geology.Lava.Contains(cell)||geology.Shore.Contains(cell)||geology.Pillars.Contains(cell)))continue;
            owner.TryDecode(cell,out var local);
            candidates.Add(cell);
            values.Add(EnvironmentMath.Band(seed,local.X,local.Z,depth));
        }
        if(candidates.Count==0)return soil;
        double threshold=EnvironmentMath.QuantileThreshold(values,coverage);
        for(int i=0;i<candidates.Count;i++)if(values[i]>=threshold)soil.Add(candidates[i]);
        return soil;
    }

    /// <summary>Steam geysers on open cave floor. Returns every occupied cell so hives avoid them.</summary>
    internal static List<IntVec3> SpawnGeysers(LayerMapComponent owner,int layer,HashSet<IntVec3> caveCells,int seed,float per10k,out int spawned)
    {
        spawned=0;
        var occupied=new List<IntVec3>();
        int count=EnvironmentMath.GeyserCount(owner.LayerRect(layer).Area,per10k);
        if(count<=0||caveCells.Count==0)return occupied;
        var map=owner.map;
        var rect=owner.LayerRect(layer).ContractedBy(2);
        Rand.PushState(Gen.HashCombineInt(seed,0x524c4745));
        try
        {
            var centers=caveCells.ToList();
            centers.Shuffle();
            foreach(var center in centers)
            {
                if(occupied.Count/4>=count)break;
                if(!rect.Contains(center))continue;
                if(occupied.Count>0&&occupied.Any(c=>(c-center).LengthHorizontalSquared<400))continue; // 20 cells apart.
                if(!FootprintFree(map,center,ThingDefOf.SteamGeyser,caveCells,occupied))continue;
                var geyser=(Building_SteamGeyser)GenSpawn.Spawn(ThingDefOf.SteamGeyser,center,map);
                occupied.AddRange(geyser.OccupiedRect());
                spawned++;
            }
        }
        finally{Rand.PopState();}
        return occupied;
    }

    /// <summary>Every cell of the footprint must be open, empty cave floor.</summary>
    private static bool FootprintFree(Map map,IntVec3 center,ThingDef def,HashSet<IntVec3> caveCells,List<IntVec3> occupied)
    {
        foreach(var cell in GenAdj.OccupiedRect(center,Rot4.North,def.Size))
        {
            if(!cell.InBounds(map)||!caveCells.Contains(cell))return false;
            if(occupied.Contains(cell))return false;
            if(cell.GetThingList(map).Count>0)return false;
        }
        return GenSpawn.CanSpawnAt(def,center,map);
    }

    /// <summary>
    /// Vanilla cave flora placed with the native spawn helper, so every plant is a normal planted,
    /// growing, harvestable Plant. Runs after regions exist, which native SpawnPlant requires.
    /// </summary>
    internal static Dictionary<string,int> SpawnFlora(LayerMapComponent owner,int layer,HashSet<IntVec3> soilCells,
        int seed,EnvironmentSettings settings,int target,TerrainDef gravel)
    {
        var counts=new Dictionary<string,int>();
        if(target<=0||soilCells.Count==0||gravel==null)return counts;
        var map=owner.map;
        var pool=PlantPool().Where(d=>settings.PlantWeight(d.defName)>0).ToList();
        if(pool.Count==0)return counts;
        // Hives may already have repainted part of the band with InsectSludge; only true
        // gravel still counts as plantable ground.
        var free=new HashSet<IntVec3>();
        foreach(var cell in soilCells)
            if(cell.InBounds(map)&&cell.GetTerrain(map)==gravel&&cell.GetThingList(map).Count==0)free.Add(cell);
        int placed=0;
        Rand.PushState(Gen.HashCombineInt(seed,0x524c464c));
        try
        {
            for(int attempt=0;attempt<target*8&&placed<target&&free.Count>0;attempt++)
            {
                var def=pool.RandomElementByWeight(d=>settings.PlantWeight(d.defName));
                var center=free.RandomElement();
                int size=1,radius=1;
                if(def.plant.GrowsInClusters)
                {
                    radius=Mathf.Clamp(def.plant.wildClusterRadius,2,6);
                    size=Rand.RangeInclusive(1,radius);
                }
                for(int i=0;i<size&&placed<target;i++)
                {
                    IntVec3 cell=center;
                    if(i>0)
                    {
                        var near=GenRadial.RadialCellsAround(center,radius,true).Where(free.Contains).ToList();
                        if(near.Count==0)break;
                        cell=near.RandomElement();
                    }
                    if(!free.Remove(cell))continue;
                    if(WildPlantSpawner.SpawnPlant(def,map,cell,setRandomGrowth:true)==null)continue;
                    placed++;
                    counts[def.defName]=counts.GetValueOrDefault(def.defName)+1;
                }
            }
        }
        finally{Rand.PopState();}
        return counts;
    }
}
