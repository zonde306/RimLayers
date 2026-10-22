using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Environment;
using RimLayers.Recovery;
using RimLayers.Ships;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly List<object> collapsePerfLegs=new();
    private static bool collapseScoreUnavailable;
    private static int collapsePerfPathJobs,collapsePerfPathsThisTick,collapsePerfTraces;
    private static int collapsePerfPhase=-1,collapsePerfTicks;
    private static long collapsePerfStamp;
    private static readonly Dictionary<string,double> collapsePerfCosts=new();
    private static readonly List<object> collapsePerfSamples=new();
    private static readonly List<object> collapsePerfReports=new();
    private static readonly List<Thing> collapsePerfBuildings=new(),collapsePerfItems=new();
    private static readonly Dictionary<IntVec3,int> collapsePerfCommits=new();
    private static readonly List<double> collapsePerfTimes=new();
    private static readonly string[] collapsePerfNames={"intact","collapsed","reroofed"};
    private static readonly CellRect collapsePerfArea=new(54,54,8,8);

    private static void ConfigureCollapsePerf(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(PathFinder),nameof(PathFinder.FindPathNow),new[]{typeof(IntVec3),typeof(LocalTargetInfo),typeof(TraverseParms),typeof(PathFinderCostTuning?),typeof(PathEndMode),typeof(PathRequest.IPathGridCustomizer)}),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(CollapseScoreUnavailable)){priority=Priority.First});
        h.Patch(AccessTools.Method(typeof(CollapseProcessor),"Collapse"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfCommit)));
        foreach(var type in new[]{typeof(CollapseProcessor),typeof(MaterialReturns),typeof(ShipRecovery),typeof(ShipStructureService),
            typeof(LayerEnvironment),typeof(AirLinks),typeof(DangerWarnings)})
            h.Patch(AccessTools.Method(type,"Tick"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfEnter)),
                postfix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfExit)));
        foreach(var method in new[]{AccessTools.Method(typeof(PathFinderJob),nameof(PathFinderJob.Execute)),AccessTools.Method(typeof(HeatLinks),nameof(HeatLinks.Exchange)),
            AccessTools.Method(typeof(HeatLinks),nameof(HeatLinks.OutdoorCoverage)),
            AccessTools.Method(typeof(LayerEnvironment),"EnsureTopology"),
            AccessTools.Method(typeof(RimLayers.Navigation.LayerRouter),"Find"),
            AccessTools.Method(typeof(MapTemperature),nameof(MapTemperature.MapTemperatureTick))})
            h.Patch(method,prefix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfEnter)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfExit)));
        h.Patch(AccessTools.Method(typeof(RimLayers.Navigation.LayerRouter),"Leg"),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfEnter)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfLegExit)));
        h.Patch(AccessTools.Method(typeof(EntityRecoveryService),nameof(EntityRecoveryService.GameComponentTick)),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfEnter)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(CollapsePerfExit)));
    }
    private static bool CollapseScoreUnavailable(ref PawnPath __result)
    {
        if(!collapseScoreUnavailable)return true;
        __result=PawnPath.NotFound;return false;
    }
    private static void CollapsePerfCommit(IntVec3 cell)=>collapsePerfCommits[cell]=collapsePerfCommits.GetValueOrDefault(cell)+1;
    private static void CollapsePerfEnter(out long __state)=>__state=Stopwatch.GetTimestamp();
    private static void CollapsePerfExit(MethodBase __originalMethod,long __state)
    {
        if(__originalMethod.DeclaringType==typeof(PathFinderJob))collapsePerfPathJobs++;
        if(__originalMethod.DeclaringType==typeof(RimLayers.Navigation.LayerRouter)&&stage==562&&++collapsePerfPathsThisTick==128&&collapsePerfTraces++<3)
            File.AppendAllText(Path.Combine(directory,"collapse-heavy-paths.txt"),$"Tick {Find.TickManager.TicksGame}\n{System.Environment.StackTrace}\n");
        string key=__originalMethod.DeclaringType!.Name+"."+__originalMethod.Name;
        collapsePerfCosts[key]=collapsePerfCosts.GetValueOrDefault(key)+Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
    }
    private static void CollapsePerfLegExit(IntVec3 from,LocalTargetInfo to,TraverseParms parms,Verse.AI.PathEndMode endMode,Verse.AI.PawnPath? __result,long __state)
    {
        double ms=Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
        if(ms<50||collapsePerfLegs.Count>=80)return;
        collapsePerfLegs.Add(new{tick=Find.TickManager.TicksGame,ms,from=from.ToString(),to=to.ToString(),
            pawn=parms.pawn?.ThingID,pawnAt=parms.pawn?.Position.ToString(),mode=parms.mode.ToString(),end=endMode.ToString(),
            found=__result?.Found,nodes=__result?.NodesReversed.Count,trace=collapsePerfLegs.Count<3?System.Environment.StackTrace:null});
    }
    private static void RestoreCollapsePerf()
    {
        pawn=map.mapPawns.FreeColonistsSpawned.First();
        Check("replayed-existing-collapsed-checkpoint",collapsePerfArea.All(c=>map.terrainGrid.TerrainAt(At(1,c.x,c.z))==RimLayersDefOf.RL_OpenAir));
        CheckCollapseStorageSelection();collapsePerfPhase=1;collapsePerfTicks=0;stage=562;
    }
    private static void BeginCollapsePerf()
    {
        int count=GenCommandLine.TryGetCommandLineArg("rimlayerspawns",out var requested)?int.Parse(requested):20;
        while(map.mapPawns.FreeColonistsSpawned.Count<count)
            GenSpawn.Spawn(PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer),At(0,49,49),map);
        foreach(var actor in map.mapPawns.FreeColonistsSpawned){actor.jobs.StopAll();actor.drafter.Drafted=false;}
        var zone=new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager);map.zoneManager.RegisterZone(zone);
        foreach(var c in collapsePerfArea)if(At(1,c.x,c.z).GetThingList(map).All(t=>t is not Building))zone.AddCell(At(1,c.x,c.z));zone.GetStoreSettings().Priority=StoragePriority.Critical;
        pawn.Position=At(0,49,49);pawn.Notify_Teleported();
        for(int layer=1;layer<=2;layer++)
        for(int x=54;x<62;x+=2)
            for(int z=54;z<62;z+=2)
            {
                var stool=ThingMaker.MakeThing(ThingDefOf.Stool,ThingDefOf.WoodLog);
                GenSpawn.Spawn(stool,At(layer,x,z),map);stool.SetFaction(Faction.OfPlayer);collapsePerfBuildings.Add(stool);
            }
        // Exercise multi-cell destruction and its native material leavings as well.
        var stove=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("FueledStove"));
        GenSpawn.Spawn(stove,At(1,55,61),map);stove.SetFaction(Faction.OfPlayer);collapsePerfBuildings.Add(stove);
        Check("collapse-performance-fixture-has-supported-buildings",collapsePerfBuildings.All(t=>t.Spawned&&owner.Structure.HasSurface(t.Position)));
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        CheckCollapseStorageSelection();collapsePerfPhase=-1;collapsePerfTicks=0;stage=562;
    }
    // Reference the previous scoring order, including paths for destinations that
    // are subsequently rejected. Used only in this explicitly enabled test profile.
    private static IHaulDestination? LegacyCollapseStorage(Thing item)
    {
        var start=item.SpawnedOrAnyParentSpawned?item.PositionHeld:pawn.PositionHeld;
        float best=float.MaxValue;var found=StoragePriority.Unstored;IHaulDestination? result=null;
        foreach(var d in map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder)
        {
            if(d is ISlotGroupParent||d is Building_Grave&&!item.CanBeBuried()||!d.HaulDestinationEnabled)continue;
            var priority=d.GetStoreSettings().Priority;if(priority<found||priority<=StoragePriority.Unstored)break;
            int score;
            using(var path=map.pathFinder.FindPathNow(start,d.Position,TraverseParms.For(pawn),null,Verse.AI.PathEndMode.ClosestTouch))
                score=path.Found?(int)Math.Min(int.MaxValue-1d,(double)path.TotalCost*path.TotalCost):int.MaxValue;
            if(score>best||!d.Accepts(item))continue;
            var thing=d as Thing;
            if(thing!=null&&(thing.Faction!=Faction.OfPlayer||thing.IsForbidden(pawn)))continue;
            if(thing!=null)
            {
                if(thing is IHaulEnroute enroute)
                {if(!map.reservationManager.OnlyReservationsForJobDef(thing,JobDefOf.HaulToContainer)||enroute.GetSpaceRemainingWithEnroute(item.def)<=0)continue;}
                else if(!pawn.CanReserveNew(thing))continue;
            }
            LocalTargetInfo target=thing!=null?thing:d.Position;
            if(!map.reachability.CanReach(start,target,Verse.AI.PathEndMode.ClosestTouch,TraverseParms.For(pawn)))continue;
            best=score;found=priority;result=d;
        }
        return result;
    }
    private static void CheckCollapseStorageSelection()
    {
        var frames=new List<Frame>();
        for(int i=0;i<3;i++)
        {
            var f=(Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef,i==1?ThingDefOf.WoodLog:ThingDefOf.Steel);
            f.SetFaction(i==2?Faction.OfAncients:Faction.OfPlayer);GenSpawn.Spawn(f,At(0,48,50+i),map);frames.Add(f);
        }
        var reports=new List<object>();int savedJobs=0;
        foreach(var def in new[]{ThingDefOf.Steel,ThingDefOf.WoodLog,ThingDefOf.Silver})
        {
            var item=ThingMaker.MakeThing(def);
            int before=collapsePerfPathJobs;long stamp=Stopwatch.GetTimestamp();var expected=LegacyCollapseStorage(item);
            int oldJobs=collapsePerfPathJobs-before;double oldMs=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            before=collapsePerfPathJobs;stamp=Stopwatch.GetTimestamp();
            bool found=StoreUtility.TryFindBestBetterNonSlotGroupStorageFor(item,pawn,map,StoragePriority.Unstored,Faction.OfPlayer,out var actual);
            int jobs=collapsePerfPathJobs-before;double newMs=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            Check("storage-preserves-exhaustive-winner-"+def.defName,actual==expected&&found==(actual!=null),$"expected={expected}, actual={actual}, oldJobs={oldJobs}, jobs={jobs}");
            Check("storage-never-adds-path-jobs-"+def.defName,jobs<=oldJobs);
            savedJobs+=oldJobs-jobs;reports.Add(new{item=def.defName,oldJobs,jobs,oldMs,newMs,selected=actual?.ToString()});
            item.Destroy(DestroyMode.Vanish);
        }
        Check("rejecting-containers-avoid-real-path-jobs",savedJobs>0,savedJobs.ToString());
        foreach(var f in frames)f.Destroy(DestroyMode.Vanish);
        var shelves=new List<Thing>();
        for(int i=0;i<2;i++)
        {
            var shelf=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("BookcaseSmall"),ThingDefOf.WoodLog);
            shelf.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(shelf,At(0,48,50+i*2),map);shelves.Add(shelf);
            ((IHaulDestination)shelf).GetStoreSettings().Priority=StoragePriority.Important;
        }
        var book=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TextBook"));
        for(int trial=0;trial<3;trial++)
        {
            shelves[0].SetFaction(trial==1?Faction.OfAncients:Faction.OfPlayer);
            ((IHaulDestination)shelves[0]).GetStoreSettings().Priority=trial==2?StoragePriority.Critical:StoragePriority.Important;
            var expected=LegacyCollapseStorage(book);
            bool found=StoreUtility.TryFindBestBetterNonSlotGroupStorageFor(book,pawn,map,StoragePriority.Unstored,Faction.OfPlayer,out var actual);
            Check("eligible-bookcase-keeps-cost-priority-faction-rules-"+trial,found&&actual==expected&&actual!=null&&(trial!=1||actual==(IHaulDestination)shelves[1])&&(trial!=2||actual==(IHaulDestination)shelves[0]),$"expected={expected}, actual={actual}");
        }
        collapseScoreUnavailable=true;
        try
        {
            var expected=LegacyCollapseStorage(book);
            bool found=StoreUtility.TryFindBestBetterNonSlotGroupStorageFor(book,pawn,map,StoragePriority.Unstored,Faction.OfPlayer,out var actual);
            Check("native-reachable-container-keeps-legacy-unavailable-score-contract",found&&actual!=null&&actual==expected,$"expected={expected}, actual={actual}");
        }
        finally{collapseScoreUnavailable=false;}
        book.Destroy(DestroyMode.Vanish);foreach(var shelf in shelves)shelf.Destroy(DestroyMode.Vanish);
        File.WriteAllText(Path.Combine(directory,mode+"-storage-comparison.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void CollapsePerfBeforeTick()
    {
        collapsePerfPathsThisTick=0;collapsePerfCosts.Clear();collapsePerfStamp=Stopwatch.GetTimestamp();
    }
    private static void CollapsePerfAfterTick()
    {
        if(collapsePerfPhase<0)return;
        double ms=Stopwatch.GetElapsedTime(collapsePerfStamp).TotalMilliseconds;
        collapsePerfTimes.Add(ms);
        collapsePerfSamples.Add(new {tick=Find.TickManager.TicksGame,ms,costs=new Dictionary<string,double>(collapsePerfCosts),
            paths=collapsePerfPathsThisTick,pending=owner.PendingCollapse.Count,materials=owner.Returns.PendingCount,stored=EntityRecoveryService.Current?.StoredCount??0,
            records=EntityRecoveryService.Current?.Records.Count??0});
    }
    private static void CollapsePerfStep()
    {
        if(++collapsePerfTicks<(collapsePerfPhase<0?600:1800))return;
        collapsePerfTicks=0;
        if(collapsePerfPhase>=0)
        {
            var ordered=collapsePerfTimes.OrderBy(t=>t).ToArray();
            collapsePerfReports.Add(new {phase=collapsePerfNames[collapsePerfPhase],mean=ordered.Average(),p99=ordered[(int)(ordered.Length*.99)],
                max=ordered[^1],legs=collapsePerfLegs.ToArray(),samples=collapsePerfSamples.ToArray()});
            File.WriteAllText(Path.Combine(directory,"collapse-performance.json"),JsonSerializer.Serialize(collapsePerfReports,new JsonSerializerOptions{WriteIndented=true}));
            Check("collapse-performance-phase-"+collapsePerfNames[collapsePerfPhase],true,
                $"ticks={ordered.Length}, mean={ordered.Average():F3}ms p99={ordered[(int)(ordered.Length*.99)]:F3}ms max={ordered[^1]:F3}ms pending={owner.PendingCollapse.Count} materials={owner.Returns.PendingCount} records={EntityRecoveryService.Current?.Records.Count}");
        }
        if(collapsePerfPhase>=1)
        {
            var steady=collapsePerfTimes.Skip(120).OrderBy(t=>t).ToArray();
            Check("no-recurring-long-ticks-"+collapsePerfNames[collapsePerfPhase],steady[(int)(steady.Length*.99)]<100,
                $"steadyP99={steady[(int)(steady.Length*.99)]:F3}ms (100ms limit; excludes first 120 transition ticks)");
        }
        collapsePerfTimes.Clear();collapsePerfSamples.Clear();collapsePerfLegs.Clear();
        if(++collapsePerfPhase==1)
        {
            var frame=FrameFor(ThingDefOf.Wall,At(2,57,59),5);collapsePerfBuildings.Add(frame);
            for(int layer=1;layer<=2;layer++)
            {
                var item=ThingMaker.MakeThing(ThingDefOf.Silver);item.stackCount=11+layer;
                GenSpawn.Spawn(item,At(layer,55,55),map);collapsePerfItems.Add(item);
            }
            foreach(var c in collapsePerfArea)map.roofGrid.SetRoof(At(0,c.x,c.z),null);
            Check("roof-removal-queues-both-second-and-third-floors",collapsePerfArea.All(c=>owner.PendingCollapse.Contains(At(1,c.x,c.z))&&owner.PendingCollapse.Contains(At(2,c.x,c.z))));
        }
        else if(collapsePerfPhase==2)
        {
            Check("collapse-destroys-test-buildings",collapsePerfBuildings.All(t=>t.Destroyed));
            Check("collapse-drains-pending-cells",owner.PendingCollapse.Count==0,owner.PendingCollapse.Count.ToString());
            Check("cascade-removes-both-floors-and-their-roofs",collapsePerfArea.All(c=>new[]{1,2}.All(l=>map.terrainGrid.TerrainAt(At(l,c.x,c.z))==RimLayersDefOf.RL_OpenAir&&map.roofGrid.RoofAt(At(l,c.x,c.z))==null)));
            if(mode=="generate")
            {
                Check("cascade-commits-each-floor-cell-once",collapsePerfArea.All(c=>new[]{1,2}.All(l=>collapsePerfCommits.GetValueOrDefault(At(l,c.x,c.z))==1)));
                Check("cascade-retains-both-original-stacks-on-ground",collapsePerfItems.All(t=>!t.Destroyed&&t.MapHeld==map&&owner.IsGroundCell(t.PositionHeld))&&collapsePerfItems.Select(t=>t.stackCount).SequenceEqual(new[]{12,13}));
            }
            if(mode=="generate")GameDataSaveLoader.SaveGame("RimLayers_CollapsePerf");
            foreach(var c in collapsePerfArea)map.roofGrid.SetRoof(At(0,c.x,c.z),RoofDefOf.RoofConstructed);
        }
        else if(collapsePerfPhase==3)
        {
            Check("reroofing-does-not-recreate-collapsed-slabs",collapsePerfArea.All(c=>map.terrainGrid.TerrainAt(At(1,c.x,c.z))==RimLayersDefOf.RL_OpenAir));
            Check("no-repeating-recovery-work-after-reroof",owner.PendingCollapse.Count==0&&owner.Returns.PendingCount==0&&EntityRecoveryService.Current?.Records.Count==0);
            Finish();
        }
    }
}
