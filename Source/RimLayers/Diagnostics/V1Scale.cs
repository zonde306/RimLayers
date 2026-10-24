using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using UnityEngine;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static int scaleSize=120,scaleCount=20,scaleLayers=3,scaleElapsed,scaleLegs,scaleBase;
    private static bool scaleSampling,scaleSaved,scaleIdentity=true,scaleCellsValid=true;
    private static double scaleGenerationMs,scaleLoadMs,scaleSaveMs;
    private static Lord scaleLord=null!;
    private static readonly List<Pawn> scaleActors=new();
    private static readonly Dictionary<string,HashSet<int>> scaleVisits=new();
    private static readonly List<double> scaleTickMs=new(),scalePathMs=new();
    private static readonly Dictionary<string,List<double>> scaleDrawMs=new();
    private static object scaleMemoryStart=null!;
    private static int ScaleArgument(string name,int fallback,int min,int max)
    {
        if(!GenCommandLine.TryGetCommandLineArg(name,out var text))return fallback;
        if(!int.TryParse(text,out int value)||value<min||value>max)throw new ArgumentException($"Invalid {name}: {text}");
        return value;
    }
    private static void ConfigureScale(Harmony h)
    {
        scaleSize=ScaleArgument("rimlayersmapsize",120,120,325);
        scaleCount=ScaleArgument("rimlayerspawns",20,1,120);
        scaleLayers=ScaleArgument("rimlayerslayers",3,2,3);
        h.Patch(AccessTools.Method(typeof(MapGenerator),nameof(MapGenerator.GenerateMap)),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(ScaleMapGenerating)){priority=Priority.Last},
            postfix:new HarmonyMethod(typeof(V1Integration),nameof(ScaleMapGenerated)));
        h.Patch(AccessTools.Method(typeof(SavedGameLoaderNow),nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow)),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(ScaleLoadBeginning)),
            postfix:new HarmonyMethod(typeof(V1Integration),nameof(ScaleLoadEnded)));
        foreach(var method in new[]{AccessTools.Method(typeof(MapDrawer),nameof(MapDrawer.DrawMapMesh)),
            AccessTools.Method(typeof(DynamicDrawManager),nameof(DynamicDrawManager.DrawDynamicThings)),
            AccessTools.Method(typeof(MapInterface),nameof(MapInterface.MapInterfaceUpdate))})
            h.Patch(method,prefix:new HarmonyMethod(typeof(V1Integration),nameof(ScaleDrawBeginning)),
                postfix:new HarmonyMethod(typeof(V1Integration),nameof(ScaleDrawEnded)));
    }
    // Test-only override BEFORE allocation of a NEW managed map. No existing layout is mutated.
    private static void ScaleMapGenerating(ref IntVec3 mapSize,out long __state)
    {
        __state=0;
        if(GenerationScope.Active is not {Map:null} scope)return;
        scope.Layout=new LayerLayout(scope.Layout.Width,scope.Layout.Height,scaleLayers,scope.Layout.Stride);
        mapSize=scope.FullSize;__state=Stopwatch.GetTimestamp();
    }
    private static void ScaleMapGenerated(long __state){if(__state!=0)scaleGenerationMs=ScaleMilliseconds(__state);}
    private static void ScaleLoadBeginning(out long __state)=>__state=Stopwatch.GetTimestamp();
    private static void ScaleLoadEnded(long __state)=>scaleLoadMs=ScaleMilliseconds(__state);
    private static double ScaleMilliseconds(long start)=>(Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency;
    private static void ScaleDrawBeginning(out long __state)=>__state=scaleSampling&&Find.CurrentMap==map?Stopwatch.GetTimestamp():0;
    private static void ScaleDrawEnded(long __state,MethodBase __originalMethod)
    {
        if(__state==0)return;
        string key=__originalMethod.DeclaringType!.Name;
        if(!scaleDrawMs.TryGetValue(key,out var samples))scaleDrawMs[key]=samples=new();
        samples.Add(ScaleMilliseconds(__state));
    }
    private static object ScaleMemory()
    {
        using var p=Process.GetCurrentProcess();p.Refresh();
        return new {workingSetBytes=p.WorkingSet64,privateBytes=p.PrivateMemorySize64,managedBytes=GC.GetTotalMemory(false),
            allocatedBytes=GC.GetTotalAllocatedBytes(false),collections=new[]{GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)}};
    }
    private static void SeedScale()
    {
        Check("scale-test-process-runs-when-unfocused",Application.runInBackground);
        scaleBase=scaleSize/2-10;
        var colonists=map.mapPawns.FreeColonistsSpawned.ToList();
        var safe=new CellRect(scaleSize-25,scaleSize-25,20,20).Where(c=>c.Walkable(map)).Take(colonists.Count).ToArray();
        if(safe.Length!=colonists.Count)throw new InvalidOperationException("No valid test-only waiting area.");
        for(int i=0;i<colonists.Count;i++)
        {var p=colonists[i];p.jobs.StopAll();p.drafter.Drafted=true;p.Position=safe[i];p.Notify_Teleported();}
        var clearing=new CellRect(scaleBase-2,scaleBase-2,25,25);
        foreach(var c in clearing)
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn)
                {if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>clearing.Contains(c));
        var platform=new CellRect(scaleBase,scaleBase,21,21);
        for(int layer=0;layer<scaleLayers;layer++)
        {
            if(layer>0)foreach(var c in platform)map.terrainGrid.SetTerrain(At(layer,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);
            if(layer==scaleLayers-1)continue;
            for(int x=scaleBase;x<=scaleBase+18;x+=6)
                for(int z=scaleBase;z<=scaleBase+18;z+=6)
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,x,z),map);
            foreach(var c in platform)map.roofGrid.SetRoof(At(layer,c.x,c.z),RoofDefOf.RoofConstructed);
            GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(layer,scaleBase+3,scaleBase+3),map);
            GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(layer,scaleBase+17,scaleBase+17),map);
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var starts=platform.Where(c=>c.Walkable(map)).Take(scaleCount).ToArray();
        for(int i=0;i<scaleCount;i++)
        {
            var p=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("AncientSoldier"),Faction.OfAncients);
            GenSpawn.Spawn(p,starts[i],map);
            if(p.needs.food!=null)p.needs.food.CurLevelPercentage=1;
            if(p.needs.rest!=null)p.needs.rest.CurLevelPercentage=1;
            scaleActors.Add(p);scaleVisits[p.ThingID]=new();
        }
        scaleLord=LordMaker.MakeNewLord(Faction.OfAncients,new LordJob_Travel(ScaleGoal(scaleLayers-1)),map,scaleActors);
        destination=ScaleGoal(scaleLayers-1);scaleMemoryStart=ScaleMemory();
        Check("scale-new-map-has-requested-fixed-layout",owner.Layout!.Width==scaleSize&&owner.Layout.Height==scaleSize&&
            owner.Layout.LayerCount==scaleLayers&&owner.Layout.MatchesStorage(map.Size.x,map.Size.z));
        Check("scale-uses-real-native-cohort",scaleLord.ownedPawns.Count==scaleCount&&scaleActors.All(p=>p.GetLord()==scaleLord));
        CheckScalePaths();
        LayerView.Switch(owner,scaleLayers-1);Find.CameraDriver.SetRootPosAndSize(destination.ToVector3Shifted(),22);
        stage=240;ticks=0;
    }
    private static IntVec3 ScaleGoal(int floor)=>At(floor,scaleBase+10,scaleBase+10);
    private static void RestoreScale()
    {
        Check("scale-test-process-runs-when-unfocused",Application.runInBackground);
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"scale-baseline.json")));
        var b=baseline.RootElement;scaleBase=scaleSize/2-10;
        scaleLord=map.lordManager.lords.Single(l=>l.loadID==b.GetProperty("lord").GetInt32());
        foreach(var id in b.GetProperty("pawns").EnumerateArray())
        {
            var p=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==id.GetString());scaleActors.Add(p);scaleVisits[p.ThingID]=new();
        }
        destination=ScaleGoal(scaleLayers-1);scaleSaved=true;scaleMemoryStart=ScaleMemory();
        Check("scale-restart-preserves-exact-layout",owner.Layout!.Width==scaleSize&&owner.Layout.Height==scaleSize&&owner.Layout.LayerCount==scaleLayers&&owner.Layout.MatchesStorage(map.Size.x,map.Size.z));
        Check("scale-restart-preserves-single-native-lord-and-cohort",scaleActors.Count==scaleCount&&scaleLord.ownedPawns.Count==scaleCount&&
            scaleActors.All(p=>p.GetLord()==scaleLord)&&map.lordManager.lords.Count(l=>l.loadID==scaleLord.loadID)==1);
        Check("scale-restart-preserves-final-duty-and-mid-route-state",scaleActors.All(p=>p.mindState.duty?.focus.Cell==destination)&&
            scaleActors.Any(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId>0));
        CheckScalePaths();stage=240;ticks=0;
    }
    private static void CheckScalePaths()
    {
        bool valid=true;
        // Two unreported warm-up paths, then one measured complete query per real Pawn.
        for(int i=-2;i<scaleActors.Count;i++)
        {
            var actor=scaleActors[Math.Max(0,i)];
            var from=At(0,scaleBase+5+(Math.Max(0,i)%10),scaleBase+5+(Math.Max(0,i)/10));
            // Columns occupy a few candidate starts; use a nearby real open platform cell.
            if(!from.Walkable(map))from=At(0,scaleBase+1,scaleBase+1);
            long stamp=Stopwatch.GetTimestamp();
            using var path=map.pathFinder.FindPathNow(from,ScaleGoal(scaleLayers-1),actor);
            double ms=ScaleMilliseconds(stamp);
            if(i<0)continue;
            scalePathMs.Add(ms);
            valid&=path.Found&&path.LastNode==ScaleGoal(scaleLayers-1)&&path.NodesReversed.All(c=>owner.TryDecode(c,out _));
        }
        Check("scale-all-measured-routes-are-complete-and-avoid-gaps",valid,$"queries={scalePathMs.Count}");
    }
    private static void ScaleStep()
    {
        scaleElapsed++;scaleSampling=scaleElapsed>=120;
        ScaleProgress("before-native-tick");
        scaleIdentity&=scaleLord.Map==map&&scaleLord.ownedPawns.Count==scaleCount&&scaleLord.numPawnsLostViolently==0&&
            scaleActors.All(p=>p.Spawned&&!p.Dead&&p.GetLord()==scaleLord);
        foreach(var p in scaleActors)
            if(owner.TryDecode(p.Position,out var c))scaleVisits[p.ThingID].Add(c.LayerId);else scaleCellsValid=false;
        if(!scaleSaved&&scaleActors.Any(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId>0))
        {
            long stamp=Stopwatch.GetTimestamp();GameDataSaveLoader.SaveGame("RimLayers_V1Scale");scaleSaveMs=ScaleMilliseconds(stamp);
            File.WriteAllText(Path.Combine(directory,"scale-baseline.json"),JsonSerializer.Serialize(new {lord=scaleLord.loadID,pawns=scaleActors.Select(p=>p.ThingID).ToArray()}));
            scaleSaved=true;Check("scale-saves-real-cohort-in-flight",true);
        }
        if(scaleLegs<4&&scaleActors.All(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId==
            (scaleLegs%2==0?scaleLayers-1:0)&&p.Position.InHorDistOf(destination,8)))
        {
            scaleLegs++;
            if(scaleLegs<4)
            {
                destination=ScaleGoal(scaleLegs%2==0?scaleLayers-1:0);
                scaleLord.SetJob(new LordJob_Travel(destination));
            }
            else scaleLord.SetJob(new LordJob_DefendPoint(destination,wanderRadius:6,defendRadius:8,addFleeToil:false));
            scaleLord.GotoToil(scaleLord.Graph.StartingToil);
        }
        if(scaleLegs>=4&&scaleElapsed>=12000)
        {
            scaleSampling=false;
            Check("scale-native-ai-completes-four-floor-trips",scaleLegs==4);
            Check("scale-cohort-identity-survives-long-simulation",scaleIdentity&&scaleActors.Distinct().Count()==scaleCount);
            Check("scale-every-actor-actually-visits-all-floors",scaleVisits.Values.All(v=>v.Count==scaleLayers));
            Check("scale-no-actor-enters-storage-isolation",scaleCellsValid);
            Check("scale-native-cohort-ids-are-unique-on-map",scaleActors.All(p=>map.mapPawns.AllPawnsSpawned.Count(q=>q.ThingID==p.ThingID)==1));
            Check("scale-measurements-cover-simulation-and-native-drawing",scaleTickMs.Count>10000&&
                new[]{nameof(MapDrawer),nameof(DynamicDrawManager),nameof(MapInterface)}.All(n=>scaleDrawMs.TryGetValue(n,out var a)&&a.Count>100));
            WriteScaleMetrics();
            LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.Selector.Select(scaleActors[0]);
            Find.CameraDriver.SetRootPosAndSize(destination.ToVector3Shifted(),22);
            pendingScreenshot=Path.Combine(directory,mode+"-scale.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(scaleElapsed>22000)
        {
            scaleSampling=false;WriteScaleMetrics();
            Check("scale-native-cohort-finishes-within-test-budget",false,$"legs={scaleLegs},ticks={scaleElapsed},pawns="+
                string.Join(";",scaleActors.Select(p=>$"{p.Position}/{p.CurJob}")));Finish();
        }
    }
    private static void ScaleProgress(string phase)
    {
        if(scaleElapsed!=1&&scaleElapsed!=15&&scaleElapsed%1000!=0)return;
        File.WriteAllText(Path.Combine(directory,mode+"-scale-progress.json"),JsonSerializer.Serialize(new {
            phase,elapsedTicks=scaleElapsed,completedTrips=scaleLegs,engineFrames=frame,nativeTick=Find.TickManager.TicksGame,
            firstActors=scaleActors.Take(3).Select(p=>new {id=p.ThingID,position=p.Position.ToString(),job=p.CurJob?.ToString(),next=p.pather.nextCell.ToString()}).ToArray()}));
    }
    private static object ScaleStats(List<double> samples)
    {
        var a=samples.OrderBy(v=>v).ToArray();
        double Q(double q)=>a.Length==0?0:a[Math.Clamp((int)Math.Ceiling(q*a.Length)-1,0,a.Length-1)];
        return new {count=a.Length,meanMs=a.Length==0?0:a.Average(),p50Ms=Q(.5),p95Ms=Q(.95),p99Ms=Q(.99),maxMs=a.Length==0?0:a[^1]};
    }
    private static void WriteScaleMetrics()
    {
        File.WriteAllText(Path.Combine(directory,mode+"-scale-metrics.json"),JsonSerializer.Serialize(new {
            mode,screenSize=new[]{UI.screenWidth,UI.screenHeight},runInBackground=Application.runInBackground,logicalSize=scaleSize,layers=scaleLayers,cohortCount=scaleCount,storageSize=new[]{map.Size.x,map.Size.z},
            storageCells=map.cellIndices.NumGridCells,allPawns=map.mapPawns.AllPawnsSpawned.Count,things=map.listerThings.AllThings.Count,
            elapsedTicks=scaleElapsed,completedTrips=scaleLegs,
            generationMs=mode=="generate"?(double?)scaleGenerationMs:null,loadMs=mode=="load"?(double?)scaleLoadMs:null,saveMs=mode=="generate"?(double?)scaleSaveMs:null,
            path=ScaleStats(scalePathMs),tick=ScaleStats(scaleTickMs),draw=scaleDrawMs.ToDictionary(p=>p.Key,p=>ScaleStats(p.Value)),
            memoryStart=scaleMemoryStart,memoryEnd=ScaleMemory(),
            // Counters prove the optimisations actually engaged rather than merely not crashing.
            perf=new {
                forCacheHitRatio=PerfCounters.ForCacheHitRatio(),
                forCalls=Core.LayerMapComponent.ForCalls,forScans=Core.LayerMapComponent.ForScans,
                routeRequests=Navigation.LayerRouter.Requests,routeLegPathfinds=Navigation.LayerRouter.LegPathfinds,
                routeDirectShortCircuits=Navigation.LayerRouter.DirectShortCircuits,
                heatExchanges=owner!.Heat.Exchanges,heatRebuilds=owner.Heat.Rebuilds,heatColumnEvaluations=owner.Heat.ColumnEvaluations,
                skyRoomInvalidations=owner.Sky.RoomInvalidations,environmentTopologyBuilds=owner.Environment.TopologyBuilds,
                connectorVersion=owner.Connectors.Version,
                legPathfindsPerRequest=Navigation.LayerRouter.Requests==0?0d:(double)Navigation.LayerRouter.LegPathfinds/Navigation.LayerRouter.Requests
            },
            methodology="Native neutral NPC Lord cohort; four complete trips, then native defend AI. 120 warm-up ticks; direct DoSingleTick CPU timing excludes fixture/control/save IO. Native draw methods timed separately: CPU submission, not GPU or end-to-end frame time. Paths are complete ground-to-top queries with two excluded warm-ups. Process memory includes engine/assets/caches; no forced GC. Counter fields record cache hit ratios and rebuild counts for the performance work; legPathfindsPerRequest is the primary router metric.",
            rawPathMs=scalePathMs,rawTickMs=scaleTickMs,rawDrawMs=scaleDrawMs
        }));
    }
}
