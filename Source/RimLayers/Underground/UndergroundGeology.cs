using RimLayers.Core;
using RimLayers.Environment;
using RimLayers.Structure;
using RimWorld;
using Verse;
using UnityEngine;

namespace RimLayers.Underground;

/// <summary>Optional metadata for eligible naturally scattered mod ores. Never enables random generation by itself.</summary>
public sealed class MineralDepthExtension : DefModExtension
{
    public bool industrial;
    public float b5=1, b9=1, geothermalMaximum=1;
    public List<string>? hostRocks;
}
public sealed class MineralGenerationRecord : IExposable
{
    public int Version=1,Seed,Layer,Depth,Budget,Clusters,OreCells,LavaCells;
    public Dictionary<string,int> Cells=new(),ClusterCounts=new();
    public Dictionary<string,float> ExpectedYield=new(),Value=new();
    public void ExposeData()
    {
        Scribe_Values.Look(ref Depth,"depth");Scribe_Values.Look(ref Version,"version",1);Scribe_Values.Look(ref Seed,"seed");Scribe_Values.Look(ref Layer,"layer");
        Scribe_Values.Look(ref Budget,"budget");Scribe_Values.Look(ref Clusters,"clusters");Scribe_Values.Look(ref OreCells,"oreCells");Scribe_Values.Look(ref LavaCells,"lavaCells");
        Scribe_Collections.Look(ref Cells,"cells",LookMode.Value,LookMode.Value);
        Scribe_Collections.Look(ref ClusterCounts,"clustersByOre",LookMode.Value,LookMode.Value);
        Scribe_Collections.Look(ref ExpectedYield,"expectedYield",LookMode.Value,LookMode.Value);
        Scribe_Collections.Look(ref Value,"value",LookMode.Value,LookMode.Value);
    }
}
internal sealed class UndergroundGeology
{
    internal readonly Dictionary<IntVec3,ThingDef> Ore=new();
    internal readonly HashSet<IntVec3> Lava=new(),Shallows=new(),Shore=new(),Pillars=new();
    internal readonly MineralGenerationRecord Record=new();
    private static readonly Dictionary<string,(float B5,float B9,float Geo)> Profiles=new(StringComparer.Ordinal)
    {
        ["MineableSteel"]=(.85f,.65f,1),["MineableComponentsIndustrial"]=(.70f,.35f,1),
        ["MineableSilver"]=(1.10f,1.40f,2),["MineableGold"]=(1.40f,2.40f,2),
        ["MineableUranium"]=(1.60f,3,1.5f),["MineablePlasteel"]=(1.50f,3,1),["MineableJade"]=(1.20f,1.60f,1.5f)
    };
    internal static bool Eligible(ThingDef d,float maxValue)=>typeof(Mineable).IsAssignableFrom(d.thingClass)&&d.building?.mineableThing!=null&&
        d.building.mineableScatterCommonality>0&&d.building.mineableThing.BaseMarketValue<=maxValue;
    internal static IEnumerable<ThingDef> LegalPool(float maxValue)=>DefDatabase<ThingDef>.AllDefsListForReading
        .Where(d=>Eligible(d,maxValue)).OrderBy(d=>d.defName,StringComparer.Ordinal);
    internal static IEnumerable<ThingDef> ConfigurablePool()
    {
        var pool=LegalPool(float.MaxValue);
        var obsidian=ModsConfig.OdysseyActive?DefDatabase<ThingDef>.GetNamedSilentFail("MineableObsidian"):null;
        return obsidian==null?pool:pool.Append(obsidian).Distinct().OrderBy(d=>d.defName,StringComparer.Ordinal);
    }
    internal static float DepthWeight(ThingDef ore,int depth,EnvironmentSettings settings)
    {
        var extension=ore.GetModExtension<MineralDepthExtension>();
        var profile=Profiles.GetValueOrDefault(ore.defName,(1f,1f,1f));
        float weight=EnvironmentMath.DepthWeight(depth,extension?.b5??profile.Item1,extension?.b9??profile.Item2);
        return Math.Max(0,1+(weight-1)*settings.MineralDepth)*settings.Weight(ore.defName);
    }
    internal static UndergroundGeology Build(LayerMapComponent owner,int id,int depth,List<IntVec3> cells,MapGenFloatGrid caves,
        Func<IntVec3,ThingDef> host,float density,float maxValue)
    {
        var plan=new UndergroundGeology();var map=owner.map;var settings=LayerEnvironment.Settings;
        int seed=Gen.HashCombineInt(map.ConstantRandSeed,0x524c4745);
        var record=plan.Record;record.Layer=id;record.Depth=depth;record.Seed=Gen.HashCombineInt(seed,-depth);
        var rect=owner.LayerRect(id);
        bool InRock(IntVec3 c)=>rect.Contains(c)&&caves[c]<=0&&c.GetEdifice(map)==null;
        // Actual connector columns and construction footprints are protected before the map is populated.
        var entrances=map.listerThings.AllThings.Where(t=>t is Building_Stairs||t is Frame||t is Blueprint)
            .Where(t=>ConnectorDefs.IsDown(t.def.entityDefToBuild??t.def))
            .SelectMany(t=>t.OccupiedRect().Cells).Select(c=>owner.TryDecode(c,out var q)?new IntVec3(q.X,0,q.Z):c).ToArray();
        bool ClearEntrance(IntVec3 at)
        {
            owner.TryDecode(at,out var q);
            return entrances.All(e=>(new IntVec3(q.X,0,q.Z)-e).LengthHorizontalSquared>64);
        }
        // Tile mutators have stable native Def names; a nonvolcanic biome is not reclassified by weather alone.
        bool volcanic=map.Biome.defName=="LavaField"||map.TileInfo.Mutators.Any(m=>m.defName is "LavaLake" or "LavaCrater" or "LavaFlow" or "LavaCaves");
        int start=volcanic?settings.VolcanicLavaStart:settings.LavaStart;
        float fraction=settings.LavaEnabled?EnvironmentMath.LavaFraction(depth,start,settings.LavaCoverage,settings.LavaGrowth,settings.LavaCap):0;
        int target=Mathf.FloorToInt(rect.Area*fraction);
        var lavaCandidates=cells.Where(c=>rect.ContractedBy(4).Contains(c)&&ClearEntrance(c)).ToList();
        lavaCandidates.Shuffle();
        foreach(var center in lavaCandidates)
        {
            if(plan.Lava.Count>=target)break;
            if(plan.Lava.Contains(center)||plan.Lava.Any(c=>(c-center).LengthHorizontalSquared<36))continue;
            int size=Math.Min(target-plan.Lava.Count,Rand.RangeInclusive(settings.LavaMinSize,settings.LavaMaxSize));
            if(size<settings.LavaMinSize)break;
            var lump=GridShapeMaker.IrregularLump(center,map,size,c=>rect.ContractedBy(2).Contains(c)&&ClearEntrance(c)&&!plan.Lava.Contains(c)).ToArray();
            foreach(var c in lump)
            {
                // Leave load-bearing native rock islands instead of an unsupported cavern-sized ceiling.
                if((c.x%7==0)&&(c.z%7==0)){plan.Pillars.Add(c);continue;}
                plan.Lava.Add(c);
            }
        }
        foreach(var c in plan.Lava)
            foreach(var offset in GenAdj.AdjacentCells)
            {
                var at=c+offset;
                if(rect.Contains(at)&&!plan.Lava.Contains(at)&&!plan.Pillars.Contains(at)&&ClearEntrance(at))plan.Shore.Add(at);
            }
        foreach(var c in plan.Lava)
            if(GenAdj.CardinalDirections.Any(d=>!plan.Lava.Contains(c+d)))plan.Shallows.Add(c);
        // One budget. Geothermal cells only change the destination/ore odds of these clusters.
        record.Budget=Mathf.RoundToInt(rect.Area/10000f*density*EnvironmentMath.MineralBudgetFactor(depth,settings.MineralGrowth,settings.MineralCap));
        var pool=LegalPool(maxValue).Where(d=>DepthWeight(d,depth,settings)>0).ToList();
        var obsidian=ModsConfig.OdysseyActive?DefDatabase<ThingDef>.GetNamedSilentFail("MineableObsidian"):null;
        var centers=cells.Where(c=>InRock(c)&&!plan.Lava.Contains(c)&&!plan.Shore.Contains(c)).ToList();
        var geo=new Dictionary<IntVec3,float>();
        foreach(var c in plan.Lava)
            foreach(var offset in GenRadial.RadialCellsAround(IntVec3.Zero,12,true))
            {
                int distance=offset.LengthHorizontalSquared;if(distance<16||distance>144)continue;
                var at=c+offset;if(!InRock(at)||plan.Lava.Contains(at)||plan.Shore.Contains(at))continue;
                geo[at]=Math.Max(geo.GetValueOrDefault(at),1-Math.Abs(MathF.Sqrt(distance)-8)/4);
            }
        var chosen=new List<IntVec3>();
        for(int n=0;n<record.Budget&&centers.Count>0;n++)
        {
            // Select eligible resource, then its own smooth host-conditioned ore band.
            var options=pool.ToList();
            if(obsidian?.building?.mineableThing!=null&&settings.Weight(obsidian.defName)>0&&obsidian.building.mineableThing.BaseMarketValue<=maxValue&&geo.Count>0)options.Add(obsidian);
            ThingDef? def=options.RandomElementByWeightWithFallback(d=>d==null?0:d==obsidian? .03f*settings.Weight(d.defName):d.building!.mineableScatterCommonality*DepthWeight(d,depth,settings));
            if(def==null)break;
            var extension=def.GetModExtension<MineralDepthExtension>();
            var profile=Profiles.GetValueOrDefault(def.defName,(1f,1f,1f));
            float enrichment=extension?.industrial==true?1:extension?.geothermalMaximum??profile.Item3;
            bool Legal(IntVec3 c)=>InRock(c)&&!plan.Lava.Contains(c)&&!plan.Shore.Contains(c)&&!plan.Ore.ContainsKey(c)&&
                (extension?.hostRocks==null||extension.hostRocks.Contains(host(c).defName))&&(def!=obsidian||geo.ContainsKey(c));
            var candidates=centers.Where(c=>Legal(c)&&chosen.All(p=>(p-c).LengthHorizontalSquared>=25)).ToList();
            if(candidates.Count==0) {pool.Remove(def);if(pool.Count==0)break;continue;}
            int bandSeed=Gen.HashCombineInt(seed,EnvironmentMath.StableHash(def.defName));
            var center=candidates.RandomElementByWeight(c=>
            {
                owner.TryDecode(c,out var q);
                int hostSeed=Gen.HashCombineInt(bandSeed,EnvironmentMath.StableHash(host(c).defName));
                return (float)EnvironmentMath.Band(hostSeed,q.X,q.Z,depth)*
                    (1+geo.GetValueOrDefault(c)*Math.Max(0,enrichment-1)*settings.GeothermalEnrichment);
            });
            int size=Math.Max(1,Mathf.RoundToInt(def.building.mineableScatterLumpSizeRange.RandomInRange*settings.LumpSize));
            var cluster=GridShapeMaker.IrregularLump(center,map,size,Legal).ToArray();
            if(cluster.Length==0)continue;
            foreach(var c in cluster)plan.Ore.Add(c,def);
            chosen.Add(center);record.Clusters++;
            record.ClusterCounts[def.defName]=record.ClusterCounts.GetValueOrDefault(def.defName)+1;
        }
        foreach(var group in plan.Ore.GroupBy(p=>p.Value))
        {
            record.Cells[group.Key.defName]=group.Count();
            float yield=group.Count()*group.Key.building.mineableYield;
            record.ExpectedYield[group.Key.defName]=yield;
            record.Value[group.Key.defName]=yield*group.Key.building.mineableThing.BaseMarketValue;
        }
        record.OreCells=plan.Ore.Count;record.LavaCells=plan.Lava.Count;
        return plan;
    }
}
