using System.Text.Json;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Mountains;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static int mountainPhase;
    private static Frame mountainFrame=null!;
    private static Mineable mountainRock=null!;
    private static Thing mountainMined=null!;
    private static int mountainInitialHp,mountainFrameMaterials,mountainOreBefore;
    private static ThingDef mountainOreProduct=null!;
    private static readonly List<(int Tick,int Delay,bool NativeSpeed)> mountainOreDelays=new();
    private static readonly List<(int Delta,int Hp)> mountainOreWork=new();
    private static IEnumerable<Toil> ObserveMountainWorkTicks(IEnumerable<Toil> __result)
    {
        foreach(var toil in __result)
        {
            var action=toil.tickIntervalAction;
            if(action!=null&&toil.activeSkill?.Invoke()==SkillDefOf.Mining)
                toil.tickIntervalAction=delta=>{bool ore=mountainPhase==4;var target=mountainRock;action(delta);if(ore)mountainOreWork.Add((delta,target.HitPoints));};
            yield return toil;
        }
    }
    private static void ObserveMountainPickDelay(JobDriver_VerticalMine __instance,int ___ticksToHit)
    {
        if(mountainPhase==4&&__instance.pawn==pawn)
            mountainOreDelays.Add((Find.TickManager.TicksGame,___ticksToHit,___ticksToHit==(int)Math.Round(100f/pawn.GetStatValue(StatDefOf.MiningSpeed))));
    }
    private static readonly IntVec3 shaftSite=new(65,0,50);
    private static readonly CellRect mountainShape=new(60,30,40,60);
    private static Frame MountainFrame(IntVec3 at)
    {
        var f=(Frame)ThingMaker.MakeThing(BootstrapDef.frameDef);f.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(f,at,map);
        foreach(var cost in BootstrapDef.CostListAdjusted(null)){var t=ThingMaker.MakeThing(cost.thingDef);t.stackCount=cost.count;f.resourceContainer.TryAdd(t);}
        f.workDone=f.WorkToBuild;return f;
    }
    private static void BeginMountains()
    {
        Check("mountain-map-starts-with-only-ground",owner==null&&map.Size.z==120);
        foreach(var p in map.mapPawns.FreeColonistsSpawned){p.jobs.StopAll();p.drafter.Drafted=true;p.Position=new(24,0,24);p.Notify_Teleported();}
        pawn=map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Mining"))&&!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Construction")))!;
        if(pawn==null)throw new Exception("Fixture requires a miner-builder.");
        foreach(var c in new CellRect(20,20,86,78))
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        foreach(var c in mountainShape)
        {GenSpawn.Spawn(ThingDefOf.Sandstone,c,map);map.terrainGrid.SetTerrain(c,ThingDefOf.Sandstone.building.naturalTerrain);map.roofGrid.SetRoof(c,RoofDefOf.RoofRockThick);}
        for(int x=30;x<60;x++)for(int z=43;z<48;z++)map.roofGrid.SetRoof(new(x,0,z),x>=50?RoofDefOf.RoofRockThin:RoofDefOf.RoofConstructed);
        for(int x=30;x<60;x+=5)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),new(x,0,43),map);
        // A real ground-level tunnel, kept supported by surrounding native rocks.
        for(int x=59;x<=65;x++)new IntVec3(x,0,50).GetEdifice(map)?.Destroy(DestroyMode.Vanish);
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        var bp=GenConstruct.PlaceBlueprintForBuild(RimLayersDefOf.RL_Stairs,new(35,0,45),map,Rot4.North,Faction.OfPlayer,null);
        Check("mountain-blueprint-does-not-expand",LayerMapComponent.For(map)==null);bp.Destroy(DestroyMode.Cancel);
        pawn.Position=new(34,0,45);pawn.Notify_Teleported();
        var f=MountainFrame(new(35,0,45));f.CompleteConstruction(pawn);owner=LayerMapComponent.For(map)!;
        Check("first-stair-generates-only-second-layer",f.Destroyed&&owner.Layout!.LayerCount==2);
        Check("thick-roof-generates-native-floor-and-rock",owner.Mountains.IsNaturalFloor(At(1,70,55))&&At(1,70,55).GetEdifice(map) is Mineable&&owner.Structure.HasSurface(At(1,70,55)));
        Check("thin-roof-is-empty-and-needs-manual-floor",!owner.Mountains.IsNaturalFloor(At(1,52,45))&&!owner.Structure.HasSurface(At(1,52,45))&&At(1,52,45).GetEdifice(map)==null&&owner.Structure.Validate(RimLayersDefOf.RL_StructuralFloor,At(1,52,45),Rot4.North,true).Accepted);
        Check("natural-floor-rejects-artificial-replacement",!owner.Structure.Validate(RimLayersDefOf.RL_StructuralFloor,At(1,70,55),Rot4.North,true).Accepted);
        Check("mountain-interior-remains-fogged",At(1,80,60).Fogged(map)&&!At(1,60,45).Fogged(map));
        var minerals=map.listerThings.AllThings.Where(t=>t is Mineable&&owner.TryDecode(t.Position,out var c)&&c.LayerId==1&&t.def.building.isResourceRock).ToArray();
        Check("native-mineral-veins-generated-in-upper-mountain",minerals.Length>0,string.Join(",",minerals.GroupBy(t=>t.def.defName).Select(g=>$"{g.Key}:{g.Count()}")));
        Check("all-minerals-stay-inside-mountain",minerals.All(t=>owner.Mountains.Columns.ContainsKey(new(t.Position.x,0,t.Position.z-owner.Layout!.Stride))));
        int floors=owner.Mountains.FloorCount,rocks=map.listerThings.AllThings.Count(t=>t is Mineable);var size=map.Size;
        var sw=System.Diagnostics.Stopwatch.StartNew();
        try{LiveMapExpansion.EnsureLayerCount(map,3,()=>throw new InvalidOperationException("mountain-test-before-commit"));}
        catch(InvalidOperationException e)when(e.Message=="mountain-test-before-commit"){}
        Check("append-failure-restores-layout-and-mountain-facts",owner.Layout!.LayerCount==2&&map.Size==size&&owner.Mountains.FloorCount==floors);
        Check("append-failure-removes-new-real-mineables",map.listerThings.AllThings.Count(t=>t is Mineable)==rocks&&map.listerThings.AllThings.All(t=>t.Position.InBounds(map)));
        File.WriteAllText(Path.Combine(directory,"mountain-timing.json"),JsonSerializer.Serialize(new{rollbackPreparationMs=sw.Elapsed.TotalMilliseconds}));
        // A roof-supported outside route meets the native mountain edge on the SAME floor.
        for(int x=35;x<60;x++)map.terrainGrid.SetTerrain(At(1,x,45),RimLayersDefOf.RL_StructuralFloor);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("native-blueprint-entry-accepts-thin-roof-floor",GenConstruct.CanPlaceBlueprintAt(RimLayersDefOf.RL_StructuralFloor,At(1,52,46),Rot4.North,map).Accepted);
        map.terrainGrid.SetTerrain(At(1,45,45),RimLayersDefOf.RL_OpenAir);map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("missing-outside-platform-segment-blocks-mountain-access",!pawn.CanReach(At(1,60,45).GetEdifice(map),PathEndMode.Touch,Danger.Deadly));
        map.terrainGrid.SetTerrain(At(1,45,45),RimLayersDefOf.RL_StructuralFloor);map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        // Native GatherData intentionally samples at most once per game tick. End this synthetic edit batch.
        Find.TickManager.DoSingleTick();
        var isolated=At(1,99,88).GetEdifice(map)!;
        map.designationManager.AddDesignation(new Designation(isolated,DesignationDefOf.Mine));
        Check("interior-rock-with-no-touch-position-is-not-mineable-yet",MineAIUtility.JobOnThing(pawn,isolated,true)==null);
        mountainMined=At(1,60,45).GetEdifice(map)!;map.designationManager.AddDesignation(new Designation(mountainMined,DesignationDefOf.Mine));
        var job=MineAIUtility.JobOnThing(pawn,mountainMined,true);
        Check("native-mining-job-accepts-outside-platform-approach",job?.def==JobDefOf.Mine&&pawn.CanReach(mountainMined,PathEndMode.Touch,Danger.Deadly));
        pawn.drafter.Drafted=false;pawn.skills.GetSkill(SkillDefOf.Mining).Level=20;pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;
        foreach(var w in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(w,0);
        using(var tracePath=map.pathFinder.FindPathNow(pawn.Position,mountainMined,TraverseParms.For(pawn),null,PathEndMode.Touch))
            Check("full-path-to-side-mine-target-found",tracePath.Found,$"nodes={tracePath.NodesReversed.Count},connectors={owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Count},surfaces={Enumerable.Range(35,25).All(x=>At(1,x,45).Walkable(map))}");
        pawn.jobs.TryTakeOrderedJob(job);expectedJob=pawn.CurJob;expectedJobId=expectedJob!.loadID;retainedJob=true;
        mountainPhase=0;stage=370;ticks=0;
    }
    private static void MountainStep()
    {
        if(mountainPhase==0)
        {
            if(!mountainMined.Destroyed)
            {if(!ExpectedJobMatches()){retainedJob=false;throw new Exception("Side mining job interrupted: "+DescribePawn());}if(ticks>9000)throw new Exception("Side mining failed: "+DescribePawn());return;}
            Check("same-native-mine-job-crossed-floor-and-mined-sidewall",retainedJob);
            var entry=At(1,60,45);
            Check("mined-entry-retains-walkable-natural-floor",owner.Structure.HasSurface(entry)&&entry.Walkable(map)&&owner.Mountains.IsNaturalFloor(entry));
            Check("natural-floor-cannot-be-removed",!map.terrainGrid.CanRemoveTopLayerAt(entry));
            var nativeBase=entry.GetTerrain(map);map.terrainGrid.RemoveTopLayer(entry,false);
            Check("direct-removal-cannot-delete-natural-base",entry.GetTerrain(map)==nativeBase&&owner.Structure.HasSurface(entry));
            map.roofGrid.SetRoof(new(60,0,45),null);owner.Collapses.Tick();
            Check("natural-base-survives-lower-roof-loss-and-blocks-sky-and-bullets",owner.Structure.HasSurface(entry)&&!owner.PendingCollapse.Contains(entry)&&!owner.Sky.IsExposed(new(60,0,45))&&new RimLayers.Combat.LayerShotScene(owner).Floor(1,60,45));
            map.roofGrid.SetRoof(new(60,0,45),RoofDefOf.RoofRockThick);
            var minedStoneSurface=entry.GetTerrain(map);
            map.terrainGrid.SetTerrain(entry,TerrainDefOf.WoodPlankFloor);map.terrainGrid.RemoveTopLayer(entry,false);
            Check("decoration-removal-restores-native-rock-floor",owner.Mountains.IsNaturalFloor(entry)&&entry.GetTerrain(map)==minedStoneSurface&&entry.Walkable(map),$"top={entry.GetTerrain(map).defName},under={map.terrainGrid.UnderTerrainAt(entry)?.defName}");
            mountainFrame=MountainFrame(shaftSite);mountainFrame.workDone=mountainFrame.WorkToBuild-1;
            mountainFrameMaterials=mountainFrame.resourceContainer.TotalStackCount;
            var builder=(WorkGiver_ConstructFinishFrames)DefDatabase<WorkGiverDef>.GetNamed("ConstructFinishFrames").Worker;
            var build=builder.JobOnThing(pawn,mountainFrame,true);
            Check("native-builder-accepts-shaft-with-unexcavated-upper-rock",build!=null);
            pawn.jobs.TryTakeOrderedJob(build);mountainPhase=3;ticks=0;return;
        }
        if(mountainPhase==3)
        {
            if(!VerticalMining.Ready(mountainFrame))
            {if(mountainFrame.Destroyed||ticks>9000)throw new Exception("Native shaft construction did not enter excavation phase: "+DescribePawn());return;}
            mountainRock=(Mineable)At(1,65,50).GetEdifice(map)!;
            Check("shaft-frame-retains-materials-until-excavated",mountainFrame.Spawned&&!mountainFrame.Destroyed&&mountainFrame.resourceContainer.TotalStackCount==mountainFrameMaterials&&mountainRock.Spawned);
            var giver=(WorkGiver_VerticalMine)DefDatabase<WorkGiverDef>.GetNamed("RL_MineVertical").Worker;
            Check("automatic-mining-candidate-index-includes-pending-frame",giver.PotentialWorkThingsGlobal(pawn).Contains(mountainFrame));
            var job=giver.JobOnThing(pawn,mountainFrame,true);
            Check("vertical-mining-has-real-anchor-and-rock-targets",job?.targetA.Thing==mountainRock&&job.targetB.Thing==mountainFrame);
            pawn.jobs.TryTakeOrderedJob(job);
            var rival=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);
            Check("second-miner-cannot-reserve-active-shaft",!giver.HasJobOnThing(rival,mountainFrame));
            mountainInitialHp=mountainRock.HitPoints;mountainPhase=1;ticks=0;return;
        }
        if(mountainPhase==1)
        {
            if(mountainRock.HitPoints==mountainInitialHp){if(ticks>6000)throw new Exception("Vertical mining failed: "+DescribePawn());return;}
            Check("vertical-mining-damages-real-rock-from-lower-floor",mountainRock.HitPoints>0&&mountainRock.HitPoints<mountainInitialHp&&owner.TryDecode(pawn.Position,out var q)&&q.LayerId==0);
            int hp=mountainRock.HitPoints;pawn.jobs.StopAll();
            var resume=((WorkGiver_VerticalMine)DefDatabase<WorkGiverDef>.GetNamed("RL_MineVertical").Worker).JobOnThing(pawn,mountainFrame,true);
            Check("interrupting-excavation-preserves-rock-progress-and-frame",mountainRock.HitPoints==hp&&mountainFrame.Spawned&&resume!=null);
            pawn.jobs.TryTakeOrderedJob(resume);
            File.WriteAllText(Path.Combine(directory,"mountain-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,frame=mountainFrame.ThingID,rock=mountainRock.ThingID,hp=mountainRock.HitPoints,floors=owner.Mountains.FloorCount,job=pawn.CurJob!.loadID}));
            GameDataSaveLoader.SaveGame("RimLayers_V1Mountains");
            CameraJumper.TryJump(pawn,CameraJumper.MovementMode.Cut);LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,60,48).ToVector3Shifted(),18);
            pendingScreenshot=Path.Combine(directory,"mountain-side-entry.png");screenshotHold=3;stage=11;ticks=0;return;
        }
        if(mountainPhase==2)
        {
            if(!mountainRock.Destroyed){if(ticks>9000)throw new Exception("Reloaded mining failed: "+DescribePawn());return;}
            Check("reloaded-vertical-mining-clears-target-once",At(1,65,50).GetEdifice(map)==null&&owner.Mountains.IsNaturalFloor(At(1,65,50)));
            mountainFrame.CompleteConstruction(pawn);
            Check("shaft-completes-after-real-excavation",mountainFrame.Destroyed&&shaftSite.GetEdifice(map) is Building_Stairs&&owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==shaftSite));
            BeginMountainOreMining();return;
        }
        if(mountainPhase==4)
        {
            if(!mountainRock.Destroyed){if(ticks>9000)throw new Exception("Ore shaft mining did not complete: "+DescribePawn());return;}
            int delta=map.listerThings.ThingsOfDef(mountainOreProduct).Where(t=>owner.TryDecode(t.Position,out var q)&&q.LayerId==1).Sum(t=>t.stackCount)-mountainOreBefore;
            Check("vertical-ore-mining-produces-real-native-resource-on-upper-floor",delta>0&&owner.Mountains.IsNaturalFloor(mountainRock.Position),$"ore={mountainRock.def.defName},product={mountainOreProduct.defName},yield={delta},ticks={ticks}");
            int hits=(int)Math.Ceiling((double)mountainInitialHp/(mountainRock.def.building.isNaturalRock?80:40));
            bool cadence=mountainOreDelays.Count==hits&&mountainOreDelays.All(d=>d.NativeSpeed);
            int expectedHp=mountainInitialHp,remaining=mountainOreDelays[0].Delay,nextDelay=1;
            foreach(var sample in mountainOreWork)
            {
                remaining-=sample.Delta;
                if(remaining<=0){expectedHp=Math.Max(0,expectedHp-(mountainRock.def.building.isNaturalRock?80:40));if(expectedHp>0)remaining=mountainOreDelays[nextDelay++].Delay;}
                cadence&=expectedHp==sample.Hp;
            }
            cadence&=expectedHp==0&&nextDelay==hits;
            Check("vertical-ore-work-scales-with-native-hitpoints-and-mining-speed",cadence,$"hp={mountainInitialHp},hits={hits},observed={mountainOreDelays.Count},delays={string.Join(",",mountainOreDelays.Select(d=>d.Delay))},deltas={string.Join(",",mountainOreWork.Select(d=>d.Delta).Distinct())},activeWork={mountainOreWork.Sum(d=>d.Delta)},elapsed={ticks}");
            mountainFrame.CompleteConstruction(pawn);
            Check("mineral-exit-finishes-without-deleting-natural-floor",mountainFrame.Destroyed&&owner.Structure.HasSurface(mountainRock.Position));
            var result=LiveMapExpansion.EnsureLayerCount(map,3);
            Check("third-layer-extends-same-geology-with-new-minerals",owner.Mountains.IsNaturalFloor(At(2,65,50))&&At(2,65,50).GetEdifice(map) is Mineable&&At(1,65,50).GetEdifice(map)==null);
            File.WriteAllText(Path.Combine(directory,"mountain-append-phases.json"),JsonSerializer.Serialize(new{before=result.Before.ToString(),after=result.After.ToString(),result.Milliseconds,result.Phases}));
            CheckMountainMigration();
            pendingScreenshot=Path.Combine(directory,"mountain-shaft-complete.png");screenshotHold=3;stage=11;ticks=0;
        }
    }
    private static void BeginMountainOreMining()
    {
        pawn.jobs.StopAll();
        mountainRock=map.listerThings.AllThings.OfType<Mineable>().First(t=>t.def.building.isResourceRock&&owner.TryDecode(t.Position,out var q)&&q.LayerId==1);
        owner.TryDecode(mountainRock.Position,out var logical);var root=new IntVec3(logical.X,0,logical.Z);var stand=root+IntVec3.West;
        foreach(var c in new[]{root,stand})
        {foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}}
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c==root||c==stand);
        pawn.Position=stand;pawn.Notify_Teleported();
        mountainFrame=MountainFrame(root);mountainFrame.CompleteConstruction(pawn);
        mountainOreProduct=mountainRock.def.building.mineableThing;
        mountainOreBefore=map.listerThings.ThingsOfDef(mountainOreProduct).Where(t=>owner.TryDecode(t.Position,out var q)&&q.LayerId==1).Sum(t=>t.stackCount);
        mountainInitialHp=mountainRock.HitPoints;
        var job=((WorkGiver_VerticalMine)DefDatabase<WorkGiverDef>.GetNamed("RL_MineVertical").Worker).JobOnThing(pawn,mountainFrame,true);
        Check("vertical-excavation-also-targets-real-minerals",job?.targetA.Thing==mountainRock&&mountainFrame.Spawned);
        mountainOreDelays.Clear();mountainOreWork.Clear();mountainPhase=4;ticks=0;pawn.jobs.TryTakeOrderedJob(job);
    }
    private static void RestoreMountains()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"mountain-baseline.json")));var b=doc.RootElement;
        pawn=map.mapPawns.FreeColonistsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        mountainFrame=map.listerThings.AllThings.OfType<Frame>().Single(f=>f.ThingID==b.GetProperty("frame").GetString());
        mountainRock=map.listerThings.AllThings.OfType<Mineable>().Single(f=>f.ThingID==b.GetProperty("rock").GetString());
        Check("reload-preserves-rock-health-floor-and-same-mining-job",mountainRock.HitPoints==b.GetProperty("hp").GetInt32()&&owner.Mountains.FloorCount==b.GetProperty("floors").GetInt32()&&pawn.CurJob?.loadID==b.GetProperty("job").GetInt32()&&pawn.jobs.curDriver is JobDriver_VerticalMine);
        Check("reload-does-not-regrow-side-entry",At(1,60,45).GetEdifice(map)==null&&owner.Mountains.IsNaturalFloor(At(1,60,45)));
        mountainPhase=2;stage=370;ticks=0;
    }
    private static void CheckMountainMigration()
    {
        // A separate old layered map with no mountain facts: explicit migration, never auto-overwrite.
        var saved=owner.Mountains.Snapshot();
        var empty=new MountainState();owner.Mountains.Restore(empty);
        var rock=At(2,61,35).GetEdifice(map)!;rock.Destroy(DestroyMode.Vanish);map.roofGrid.SetRoof(At(2,61,35),null);map.terrainGrid.SetTerrain(At(2,61,35),RimLayersDefOf.RL_OpenAir);
        var preview=owner.Mountains.PreviewMigration(2);
        Check("legacy-preview-only-offers-unused-air",preview.Eligible.Contains(At(2,61,35))&&!preview.Eligible.Contains(At(2,65,50))&&preview.Skipped.Count>0);
        int things=map.listerThings.AllThings.Count;bool rolled=false;
        try{owner.Mountains.CommitMigration(preview,()=>throw new InvalidOperationException("migration-test-fault"));}
        catch(InvalidOperationException e)when(e.Message=="migration-test-fault"){rolled=true;}
        Check("migration-fault-rolls-back-real-entities-and-geology",rolled&&map.listerThings.AllThings.Count==things&&!owner.Mountains.Initialized&&At(2,61,35).GetTerrain(map)==RimLayersDefOf.RL_OpenAir);
        Check("confirmed-migration-fills-once",owner.Mountains.CommitMigration(preview)==1&&owner.Mountains.IsNaturalFloor(At(2,61,35)));
        At(2,61,35).GetEdifice(map)!.Destroy(DestroyMode.Vanish);
        Check("repeating-old-preview-does-not-refresh-mined-cell",owner.Mountains.CommitMigration(preview)==0&&At(2,61,35).GetEdifice(map)==null);
        owner.Mountains.Restore(saved);owner.Sky.Rebuild();map.pathing.RecalculateAllPerceivedPathCosts();owner.Connectors.Invalidate(); // Restore fixture ownership and derivatives.
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(At(2,60,48).ToVector3Shifted(),18);
        var dialog=new Dialog_MountainMigration(owner);Find.WindowStack.Add(dialog);
        Check("migration-preview-opens-native-window-without-mutating-map",Find.WindowStack.Windows.Contains(dialog)&&owner.Mountains.FloorCount==saved.Floors.Count);
        // Leave the native confirmation window visible for the screenshot; no commit is requested.
    }
}
