using System.Diagnostics;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static bool legacyReach;
    private static int reachJobs;
    private static Thing reachMeal=null!;
    private static readonly Func<Pawn,IntVec3,List<Thing>,PathEndMode,TraverseParms,float,Predicate<Thing>,Thing> FoodScan=
        AccessTools.MethodDelegate<Func<Pawn,IntVec3,List<Thing>,PathEndMode,TraverseParms,float,Predicate<Thing>,Thing>>(AccessTools.Method(typeof(FoodUtility),"SpawnedFoodSearchInnerScan"));
    private static void ConfigureReachability(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(LayerReachability),nameof(LayerReachability.CanReach)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(LegacyReachProbe)));
        h.Patch(AccessTools.Method(typeof(PathFinderJob),nameof(PathFinderJob.Execute)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(ReachJobObserved)));
    }
    private static void ReachJobObserved()=>reachJobs++;
    private static bool LegacyReachProbe(LayerMapComponent owner,IntVec3 start,LocalTargetInfo target,PathEndMode mode,TraverseParms parms,ref bool __result)
    {
        if(!legacyReach)return true;
        using var path=LayerRouter.Find(owner,start,target,parms,null,mode,null);__result=path.Found;return false;
    }
    private static void BeginReachability()
    {
        pawn.drafter.Drafted=true;
        // Many real meals in one connected ground corridor, plus reachable and
        // disconnected upper-floor food. Order far-to-near so native optimality
        // checks reachability repeatedly, just like the captured food scan.
        for(int x=80;x<208;x++)
        {
            var cell=At(0,x,55);
            foreach(var t in cell.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);
            GenSpawn.Spawn(ThingDefOf.MealSimple,cell,map);
        }
        reachMeal=GenSpawn.Spawn(ThingDefOf.MealSimple,At(2,62,62),map);
        foreach(var local in new CellRect(90,90,5,5))
        {
            map.roofGrid.SetRoof(At(0,local.x,local.z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,local.x,local.z),RimLayersDefOf.RL_StructuralFloor);
            map.roofGrid.SetRoof(At(1,local.x,local.z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(2,local.x,local.z),RimLayersDefOf.RL_StructuralFloor);
        }
        foreach(int layer in new[]{0,1})GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,92,92),map);
        GenSpawn.Spawn(ThingDefOf.MealSimple,At(2,91,91),map);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        CheckReachabilityAndMeasure();
        // Prove the cheap candidate query still feeds the ordinary native ingest job.
        foreach(var meal in map.listerThings.ThingsOfDef(ThingDefOf.MealSimple))meal.SetForbidden(meal!=reachMeal);
        pawn.drafter.Drafted=false;pawn.needs.food.CurLevelPercentage=0;
        var job=JobMaker.MakeJob(JobDefOf.Ingest,reachMeal);job.count=1;
        pawn.jobs.TryTakeOrderedJob(job);expectedJob=pawn.CurJob;expectedJobId=expectedJob.loadID;
        File.WriteAllText(Path.Combine(directory,"reach-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,meal=reachMeal.ThingID,job=expectedJobId}));
        GameDataSaveLoader.SaveGame("RimLayers_V1Reachability");
        stage=300;ticks=0;visited.Clear();
    }
    private static void CheckReachabilityAndMeasure()
    {
        var parms=TraverseParms.For(pawn,Danger.Deadly);
        var meals=map.listerThings.ThingsOfDef(ThingDefOf.MealSimple)
            .Where(t=>t==reachMeal||t.Position.z==55&&t.Position.x>=80&&t.Position.x<208||t.Position==At(2,91,91))
            .OrderByDescending(t=>RimLayers.Work.QueryGeometry.DistanceSquared(owner,pawn.Position,t.Position)).ToList();
        Check("food-fixture-has-130-real-candidates-and-disconnected-upper-food",meals.Count==130&&!map.reachability.CanReach(pawn.Position,At(2,91,91),PathEndMode.OnCell,parms),$"candidates={meals.Count}");
        bool[] expected;
        try{legacyReach=true;expected=meals.Select(t=>map.reachability.CanReach(pawn.Position,t,PathEndMode.ClosestTouch,parms)).ToArray();}
        finally{legacyReach=false;}
        reachJobs=0;
        var actual=meals.Select(t=>map.reachability.CanReach(pawn.Position,t,PathEndMode.ClosestTouch,parms)).ToArray();
        var comparison=meals.Select((t,i)=>new{thing=t.ThingID,cell=t.Position.ToString(),legacy=expected[i],current=actual[i]}).ToArray();
        File.WriteAllText(Path.Combine(directory,mode+"-reach-candidates.json"),JsonSerializer.Serialize(comparison));
        Check("all-candidate-booleans-match-legacy-complete-path-query",actual.SequenceEqual(expected),string.Join(";",comparison.Where(c=>c.legacy!=c.current)));
        Check("boolean-candidate-query-does-not-run-any-pathfinder-job",reachJobs==0,$"pathJobs={reachJobs}");
        // Warm the native food code and both implementations before timing the same input.
        FoodScan(pawn,pawn.Position,meals.Take(2).ToList(),PathEndMode.ClosestTouch,parms,9999,_=>true);
        Thing before;long oldStart;double oldMs;int oldJobs;
        try
        {
            legacyReach=true;reachJobs=0;oldStart=Stopwatch.GetTimestamp();
            before=FoodScan(pawn,pawn.Position,meals,PathEndMode.ClosestTouch,parms,9999,_=>true);
            oldMs=Stopwatch.GetElapsedTime(oldStart).TotalMilliseconds;oldJobs=reachJobs;
        }
        finally{legacyReach=false;}
        reachJobs=0;var stamp=Stopwatch.GetTimestamp();
        var after=FoodScan(pawn,pawn.Position,meals,PathEndMode.ClosestTouch,parms,9999,_=>true);
        double newMs=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        Check("native-food-scan-keeps-exact-selected-real-meal",before==reachMeal&&after==before,$"old={before?.ThingID},new={after?.ThingID}");
        Check("native-food-scan-removes-full-path-generation",oldJobs>0&&reachJobs==0,$"oldJobs={oldJobs},newJobs={reachJobs}");
        Check("same-input-native-food-scan-is-faster",newMs<oldMs,$"legacyMs={oldMs:F3},newMs={newMs:F3}");
        File.WriteAllText(Path.Combine(directory,mode+"-reach-metrics.json"),JsonSerializer.Serialize(new{candidates=meals.Count,storageCells=map.cellIndices.NumGridCells,legacyMs=oldMs,newMs,legacyPathJobs=oldJobs,newPathJobs=reachJobs}));
        var stair=map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<Building_Stairs>().Single(s=>owner.TryDecode(s.Position,out var c)&&c.LayerId==1);
        bool drafted=pawn.Drafted;pawn.drafter.Drafted=false;
        stair.SetForbidden(true);
        Check("forbidden-stair-invalidates-cross-floor-connectivity",!pawn.CanReach(reachMeal,PathEndMode.ClosestTouch,Danger.Deadly));stair.SetForbidden(false);pawn.drafter.Drafted=drafted;
        var wall=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),stair.UpperCell,map);
        Check("blocked-landing-invalidates-cross-floor-connectivity",!pawn.CanReach(reachMeal,PathEndMode.ClosestTouch,Danger.Deadly));wall.Destroy(DestroyMode.Vanish);
        var door=(Building_Door)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Door,ThingDefOf.Steel),stair.UpperCell,map);door.SetFaction(Faction.OfPlayer);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("boolean-query-honors-no-pass-closed-door-mode",!map.reachability.CanReach(pawn.Position,reachMeal,PathEndMode.ClosestTouch,TraverseParms.For(TraverseMode.NoPassClosedDoors)));
        Check("boolean-query-honors-explicit-pass-doors-mode",map.reachability.CanReach(pawn.Position,reachMeal,PathEndMode.ClosestTouch,TraverseParms.For(TraverseMode.PassDoors)));
        door.Destroy(DestroyMode.Vanish);
        Check("dynamic-recovery-needs-no-stale-cross-query-cache",pawn.CanReach(reachMeal,PathEndMode.ClosestTouch,Danger.Deadly));
        Check("invalid-storage-gap-is-not-reachable",!map.reachability.CanReach(pawn.Position,new IntVec3(54,0,owner.Layout!.Height),PathEndMode.OnCell,parms));
    }
    private static void RestoreReachability()
    {
        using var b=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"reach-baseline.json")));var d=b.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==d.GetProperty("pawn").GetString());
        reachMeal=map.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty("meal").GetString());
        Check("native-ingest-job-and-real-meal-survive-restart",pawn.CurJob?.loadID==d.GetProperty("job").GetInt32()&&pawn.CurJob?.targetA.Thing==reachMeal);
        if(GenCommandLine.CommandLineArgPassed("rimlayersreachaudit"))CheckReachabilityAndMeasure();
        expectedJob=pawn.CurJob;expectedJobId=expectedJob!.loadID;stage=300;ticks=0;visited.Clear();
        reachJobs=0;Check("restart-rebuilds-boolean-connectivity-without-path-job",pawn.CanReach(reachMeal,PathEndMode.ClosestTouch,Danger.Deadly)&&reachJobs==0);
    }
    private static void ReachabilityStep()
    {
        owner.TryDecode(pawn.Position,out var cell);visited.Add(cell.LayerId);
        if(pawn.needs.food.CurLevelPercentage>.3f)
        {
            Check("pawn-actually-eats-on-third-floor-via-second-floor",cell.LayerId==2&&visited.Contains(1));
            Check("ingest-used-one-original-job",pawn.CurJob?.loadID==expectedJobId||reachMeal.Destroyed);
            LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(pawn.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,mode+"-reach-ingest.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(ticks>6500){Check("cross-floor-ingest-completes",false,DescribePawn());Finish();}
    }
}
