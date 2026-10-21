using System.Diagnostics;
using System.Text.Json;
using RimLayers.Core;
using RimLayers.Environment;
using RimLayers.Generation;
using RimLayers.Presentation;
using RimLayers.Underground;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static readonly List<object> environmentPerfResults=new();
    private static readonly List<double> environmentPerfTicks=new();
    private static int environmentPerfPhase=-1,environmentPerfTick;
    private static long environmentPerfStamp;
    private static readonly bool[] environmentPerfFlags={false,true,true,false};
    private static void BeginEnvironmentPerf()
    {
        Check("performance-fixture-is-250-square-five-layers",owner.Layout!.Width==250&&owner.Layout.Height==250&&owner.Layout.LayerCount==5);
        foreach(int elevation in new[]{-2,-1,0,1,2})EnvBox(elevation,50,50);
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var actors=map.mapPawns.FreeColonistsSpawned.Take(20).ToList();
        while(actors.Count<20){var p=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);GenSpawn.Spawn(p,EnvAt(0,52,52),map);actors.Add(p);}
        for(int i=0;i<actors.Count;i++)
        {
            var actor=actors[i];actor.jobs.StopAll();actor.drafter.Drafted=true;
            actor.Position=EnvAt(new[]{-2,-1,0,1,2}[i%5],51+(i/5)%4,52);actor.Notify_Teleported();
            if(actor.needs.food!=null)actor.needs.food.CurLevelPercentage=1;
            if(actor.needs.rest!=null)actor.needs.rest.CurLevelPercentage=1;
        }
        owner.Heat.Exchange();environmentPerfPhase=-1;environmentPerfTick=0;stage=561;
        LayerView.Switch(owner,owner.Layout.LayerAt(-1));Find.CameraDriver.SetRootPosAndSize(EnvAt(-1,53,53).ToVector3Shifted(),18);
        pendingScreenshot=Path.Combine(directory,"environment-performance-scene.png");screenshotHold=4;
    }
    private static void EnvironmentPerfBeforeTick()=>environmentPerfStamp=Stopwatch.GetTimestamp();
    private static void EnvironmentPerfAfterTick()
    {
        if(environmentPerfPhase>=0)environmentPerfTicks.Add(Stopwatch.GetElapsedTime(environmentPerfStamp).TotalMilliseconds);
    }
    private static void EnvironmentPerfStep()
    {
        environmentPerfTick++;
        if(environmentPerfPhase<0)
        {
            if(environmentPerfTick<600)return;
            environmentPerfPhase=0;environmentPerfTick=0;
            Current.Game.GetComponent<LayerEnvironmentGame>().Enabled=environmentPerfFlags[0];return;
        }
        if(environmentPerfTick<1200)return;
        environmentPerfResults.Add(new{phase=environmentPerfPhase,enabled=environmentPerfFlags[environmentPerfPhase],ticks=environmentPerfTicks.ToArray(),mean=environmentPerfTicks.Average()});
        environmentPerfTicks.Clear();environmentPerfPhase++;environmentPerfTick=0;
        if(environmentPerfPhase<environmentPerfFlags.Length){Current.Game.GetComponent<LayerEnvironmentGame>().Enabled=environmentPerfFlags[environmentPerfPhase];return;}
        var json=JsonSerializer.SerializeToElement(environmentPerfResults);
        double disabled=json.EnumerateArray().Where(p=>!p.GetProperty("enabled").GetBoolean()).Average(p=>p.GetProperty("mean").GetDouble());
        double enabled=json.EnumerateArray().Where(p=>p.GetProperty("enabled").GetBoolean()).Average(p=>p.GetProperty("mean").GetDouble());
        double overhead=enabled/disabled-1;
        File.WriteAllText(Path.Combine(directory,"environment-performance.json"),JsonSerializer.Serialize(new{
            method="Same 250x250 map, five real floors, 20 drafted native colonists; 600 warm-up ticks; ABBA 4 x 1200 native ticks, default clear weather. Generation/rendering excluded; full raw tick CPU samples retained.",
            disabledMeanMs=disabled,enabledMeanMs=enabled,overhead,processMemory=Process.GetCurrentProcess().WorkingSet64,phases=environmentPerfResults},new JsonSerializerOptions{WriteIndented=true}));
        Check("default-added-tick-cost-at-most-five-percent",overhead<=.05,$"disabled={disabled:F4}ms enabled={enabled:F4}ms overhead={overhead:P2}");
        Current.Game.GetComponent<LayerEnvironmentGame>().Enabled=true;Finish();
    }
    private static void EnvironmentGeologyBatch()
    {
        LiveMapExpansion.EnsureElevation(map,-9);
        var production=owner.Underground.GenerationRecords.ToArray();
        Check("normal-lava-begins-at-B6",production.All(r=>r.LavaCells==0||owner.Elevation(r.Layer)<=-6)&&production.Where(r=>owner.Elevation(r.Layer)<=-6).Any(r=>r.LavaCells>0));
        Check("lava-never-exceeds-coverage-budget",production.All(r=>r.LavaCells<=owner.Layout!.Width*owner.Layout.Height*EnvironmentMath.LavaFraction(-owner.Elevation(r.Layer),6,.005f,2,.04f)+.01));
        var entities=map.listerThings.AllThings.OfType<Mineable>().Where(t=>t.def.building.isResourceRock&&owner.TryDecode(t.Position,out var q)&&owner.Elevation(q.LayerId)<0).ToArray();
        Check("production-statistics-count-real-native-mineables",production.Sum(r=>r.OreCells)==entities.Length);
        Check("real-ore-keeps-native-hp-and-yield",entities.All(t=>t.HitPoints==t.MaxHitPoints&&t.def.building.mineableYield>0));
        var reports=new List<object>();
        var signatures=new Dictionary<int,List<KeyValuePair<IntVec3,ThingDef>>>();
        // Fixed RNG seeds plus the recorded immutable map/band seed. No live entities are generated by the batch planner.
        int layer=owner.Layout!.LayerAt(-3);
        foreach(var thing in map.listerThings.AllThings.Where(t=>owner.TryDecode(t.Position,out var q)&&q.LayerId==layer).ToArray())
            if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
        foreach(int depth in new[]{1,5,9})
        {
            var cells=owner.LayerRect(layer).Cells.ToList();
            using var caves=new MapGenFloatGrid(map);
            foreach(var c in cells)if((c.x%31<4)&&(c.z%23<9))caves[c]=1;
            ThingDef Host(IntVec3 c)=>DefDatabase<ThingDef>.GetNamed(c.x%73<35?"Granite":"Limestone");
            for(int n=0;n<20;n++)
            {
                int rngSeed=731000+n;UndergroundGeology plan;
                Rand.PushState(rngSeed);
                try{plan=UndergroundGeology.Build(owner,layer,depth,cells,caves,Host,10,float.MaxValue);}
                finally{Rand.PopState();}
                reports.Add(new{depth,rngSeed,record=plan.Record,ore=plan.Record.Cells,clusters=plan.Record.ClusterCounts,
                    yields=plan.Record.ExpectedYield,values=plan.Record.Value,lava=plan.Lava.Count});
                Check($"batch-mask-B{depth}-seed{n}",plan.Ore.Keys.All(c=>caves[c]<=0&&!plan.Lava.Contains(c)&&!plan.Shore.Contains(c)));
                Check($"batch-count-only-multiplier-B{depth}-seed{n}",plan.Record.Budget==(int)Math.Round(cells.Count/10000f*10*EnvironmentMath.MineralBudgetFactor(depth,.05f,1.4f),MidpointRounding.ToEven));
                if(n==0)
                {
                    Rand.PushState(rngSeed);UndergroundGeology repeated;
                    try{repeated=UndergroundGeology.Build(owner,layer,depth,cells,caves,Host,10,float.MaxValue);}
                    finally{Rand.PopState();}
                    var first=plan.Ore.OrderBy(p=>p.Key.x).ThenBy(p=>p.Key.z).ToList();
                    Check("same-seed-identical-ore-B"+depth,first.SequenceEqual(repeated.Ore.OrderBy(p=>p.Key.x).ThenBy(p=>p.Key.z)));
                    signatures[depth]=first;
                    if(depth==9)
                    {
                        Rand.PushState(rngSeed);
                        try{var zero=UndergroundGeology.Build(owner,layer,depth,cells,caves,Host,0,float.MaxValue);Check("zero-mineral-budget-has-no-geothermal-bonus-ore",zero.Record.Budget==0&&zero.Ore.Count==0);}
                        finally{Rand.PopState();}
                    }
                }
            }
        }
        EnvironmentGeologySettingsChecks(layer);
        var data=JsonSerializer.SerializeToElement(reports);
        double Share(int depth,string ore)
        {
            var list=data.EnumerateArray().Where(p=>p.GetProperty("depth").GetInt32()==depth).ToArray();
            double count=list.Sum(p=>p.GetProperty("clusters").TryGetProperty(ore,out var value)?value.GetInt32():0);
            double total=list.Sum(p=>p.GetProperty("clusters").EnumerateObject().Sum(x=>x.Value.GetInt32()));
            return count/total;
        }
        Check("deep-gold-cluster-share-increases",Share(9,"MineableGold")>Share(1,"MineableGold"));
        Check("deep-uranium-cluster-share-increases",Share(9,"MineableUranium")>Share(1,"MineableUranium"));
        Check("deep-industrial-components-cluster-share-falls",Share(9,"MineableComponentsIndustrial")<Share(1,"MineableComponentsIndustrial"));
        File.WriteAllText(Path.Combine(directory,"mineral-distribution.json"),JsonSerializer.Serialize(new{
            mapSeed=map.ConstantRandSeed,size=owner.Layout!.Width,production,legalPool=UndergroundGeology.LegalPool(float.MaxValue).Select(d=>new{d.defName,d.building.mineableScatterCommonality,d.building.mineableYield}),
            method="20 fixed RNG seeds 731000..731019 at each B1/B5/B9, identical synthetic cave/host mask, native irregular lumps, normal lava thresholds. Production entity counts are verified before planner-only batches.",
            batches=reports},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        int deepest=owner.Layout!.LayerAt(-9);LayerView.Switch(owner,deepest);
        foreach(var at in owner.LayerRect(deepest))map.fogGrid.Unfog(at);
        var lava=owner.LayerRect(deepest).FirstOrDefault(c=>c.GetTerrain(map).defName=="RL_LavaDeep");
        if(lava.IsValid)Find.CameraDriver.SetRootPosAndSize(lava.ToVector3Shifted(),18);
        pendingScreenshot=Path.Combine(directory,"deep-magma-geology.png");screenshotHold=4;stage=11;ticks=0;
    }
}
