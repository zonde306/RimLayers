using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Mountains;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimLayers.Underground;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static readonly List<double> undergroundTickMs=new();
    private static Pawn undergroundInsect=null!;
    private static Lord? undergroundInsectLord;
    private static int undergroundPhase,undergroundHp,undergroundMaterials;
    private static Frame undergroundFrame=null!;
    private static Mineable undergroundRock=null!;
    private static IntVec3 undergroundEntrance;
    private static ThingDef DownDef=>DefDatabase<ThingDef>.GetNamed(GenCommandLine.CommandLineArgPassed("rimlayersbootlift")?"RL_LiftSingleDown":"RL_StairsDown");
    private static WorkGiver_VerticalMine UndergroundMiner=>(WorkGiver_VerticalMine)DefDatabase<WorkGiverDef>.GetNamed("RL_MineVertical").Worker;
    private static IntVec3 UndergroundAt(int elevation,int x,int z)=>owner.AtElevation(elevation,x,z);
    private static Frame UndergroundFrame(IntVec3 at)
    {
        var f=(Frame)ThingMaker.MakeThing(DownDef.frameDef);f.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(f,at,map);
        foreach(var cost in DownDef.CostListAdjusted(null)){var item=ThingMaker.MakeThing(cost.thingDef);item.stackCount=cost.count;f.resourceContainer.TryAdd(item);}
        f.workDone=f.WorkToBuild;return f;
    }
    private static void BeginUnderground()
    {
        int requested=GenCommandLine.TryGetCommandLineArg("rimlayersmapsize",out var value)?int.Parse(value):120;
        Check("underground-starts-with-original-single-map",owner==null&&map.Size.z==requested);
        foreach(var p in map.mapPawns.FreeColonistsSpawned){p.jobs.StopAll();p.drafter.Drafted=true;}
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Mining"))&&!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Construction")));
        pawn.skills.GetSkill(SkillDefOf.Mining).Level=20;pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        foreach(var cell in new CellRect(15,15,90,90))
        {
            foreach(var thing in cell.GetThingList(map).ToList())if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
            map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Soil);map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        var tileBiome=map.TileInfo.PrimaryBiome;
        foreach(string name in new[]{"IceSheet","SeaIce"})
        {
            map.TileInfo.PrimaryBiome=DefDatabase<BiomeDef>.GetNamed(name);
            Check(name+"-rejects-whole-map",!UndergroundService.TerrainAllows(map,new(60,0,60)).Accepted);
        }
        map.TileInfo.PrimaryBiome=tileBiome;
        foreach(string name in new[]{"Ice","WaterShallow","WaterDeep"})
        {
            map.terrainGrid.SetTerrain(new(60,0,60),DefDatabase<TerrainDef>.GetNamed(name));
            Check(name+"-rejects-excavation",!UndergroundService.TerrainAllows(map,new(60,0,60)).Accepted);
        }
        map.terrainGrid.SetTerrain(new(60,0,60),TerrainDefOf.Soil);
        var blueprint=GenConstruct.PlaceBlueprintForBuild(DownDef,new(60,0,60),map,Rot4.North,Faction.OfPlayer,null);
        Check("down-blueprint-does-not-generate",LayerMapComponent.For(map)==null);blueprint.Destroy(DestroyMode.Cancel);
        pawn.Position=new(59,0,60);pawn.Notify_Teleported();pawn.drafter.Drafted=false;
        var originalPawn=pawn.Position;var originalId=pawn.ThingID;
        var initial=UndergroundFrame(new(60,0,60));initial.CompleteConstruction(pawn);
        owner=LayerMapComponent.For(map)!;
        Check("first-down-completion-prepares-only-B1",owner.Layout!.LayerCount==2&&owner.Elevation(1)==-1&&!owner.Layout.TryGetLayerAtElevation(1,out _));
        Check("generation-preserves-surface-pawn-and-terrain",pawn.Position==originalPawn&&pawn.ThingID==originalId&&new IntVec3(55,0,55).GetTerrain(map)==TerrainDefOf.Soil);
        Check("B1-has-natural-floor-roof-caves-and-minerals",owner.LayerRect(1).All(c=>owner.Mountains.IsNaturalFloor(c)&&map.roofGrid.RoofAt(c)==RoofDefOf.RoofRockThick)&&owner.LayerRect(1).Any(c=>c.GetEdifice(map)==null)&&map.listerThings.AllThings.OfType<Mineable>().Any(t=>owner.TryDecode(t.Position,out var q)&&q.LayerId==1&&t.def.building.isResourceRock));
        Check("B1-remains-fogged-and-not-exposed-to-sky",owner.LayerRect(1).Count(c=>c.Fogged(map))>owner.Layout.Width*owner.Layout.Height/2&&!owner.Sky.IsExposed(UndergroundAt(-1,60,60)));
        int oldThings=map.listerThings.AllThings.Count;int oldLords=map.lordManager.lords.Count;var oldSize=map.Size;
        bool fault=false;
        try{LiveMapExpansion.EnsureElevation(map,-2,()=>throw new InvalidOperationException("underground-test-fault"));}
        catch(InvalidOperationException e)when(e.Message=="underground-test-fault"){fault=true;}
        Check("failed-B2-generation-rolls-back-entities-layout-and-lords",fault&&map.Size==oldSize&&owner.Layout.LayerCount==2&&map.listerThings.AllThings.Count==oldThings&&map.lordManager.lords.Count==oldLords);
        var forbidden=new IntVec3(35,0,35);map.terrainGrid.SetTerrain(forbidden,TerrainDefOf.WaterShallow);
        var blocked=(Building_Stairs)GenSpawn.Spawn(DownDef,forbidden,map);
        Check("direct-spawn-on-water-cannot-open-or-mine-a-shaft",!owner.Connectors.Usable(blocked,TraverseParms.For(pawn))&&!VerticalMining.Ready(blocked));
        blocked.Destroy(DestroyMode.Vanish);map.terrainGrid.SetTerrain(forbidden,TerrainDefOf.Soil);
        if(initial.Spawned)initial.Destroy(DestroyMode.Cancel);
        else new IntVec3(60,0,60).GetEdifice(map)?.Destroy(DestroyMode.Vanish);
        undergroundRock=map.listerThings.AllThings.OfType<Mineable>().First(t=>owner.TryDecode(t.Position,out var q)&&owner.Elevation(q.LayerId)==-1&&t.def.building.isResourceRock&&q.X>20&&q.X<100&&q.Z>20&&q.Z<100);
        owner.TryDecode(undergroundRock.Position,out var target);undergroundEntrance=new(target.X,0,target.Z);
        pawn.Position=undergroundEntrance+IntVec3.West;pawn.Notify_Teleported();
        undergroundFrame=UndergroundFrame(undergroundEntrance);undergroundMaterials=undergroundFrame.resourceContainer.TotalStackCount;
        undergroundFrame.CompleteConstruction(pawn);
        Check("down-frame-waits-for-real-mineral-and-keeps-materials",undergroundFrame.Spawned&&undergroundFrame.resourceContainer.TotalStackCount==undergroundMaterials&&VerticalMining.Rock(undergroundFrame)==undergroundRock);
        undergroundHp=undergroundRock.HitPoints;stage=430;undergroundPhase=0;ticks=0;
    }
    private static void UndergroundStep()
    {
        if(undergroundPhase==5)
        {
            if(ticks<600)return;
            File.WriteAllText(Path.Combine(directory,"underground-simulation.json"),JsonSerializer.Serialize(new{ticks=undergroundTickMs.Count,nativeTickMilliseconds=undergroundTickMs.Sum(),meanTickMilliseconds=undergroundTickMs.Average(),pawns=map.mapPawns.AllPawnsSpawned.Count,layers=owner.Layout!.Elevations}));
            Check("five-layer-native-simulation-advances",undergroundTickMs.Count>=599);stage=11;ticks=0;return;
        }
        if(undergroundPhase==0)
        {
            if(ticks<2)return;
            var job=UndergroundMiner.JobOnThing(pawn,undergroundFrame,true);
            Check("native-mining-work-finds-downward-shaft",job?.targetA.Thing==undergroundRock&&job.targetB.Thing==undergroundFrame);
            if(job==null){Finish();return;}pawn.jobs.TryTakeOrderedJob(job);undergroundPhase=1;ticks=0;return;
        }
        if(undergroundPhase==1)
        {
            if(undergroundRock.HitPoints==undergroundHp){if(ticks>6000)throw new Exception("Downward mining did not start: "+DescribePawn());return;}
            Check("downward-mining-damages-real-rock-from-surface",undergroundRock.HitPoints>0&&undergroundRock.HitPoints<undergroundHp&&owner.IsGroundCell(pawn.Position));
            pawn.jobs.StopAll();int hp=undergroundRock.HitPoints;
            pawn.jobs.TryTakeOrderedJob(UndergroundMiner.JobOnThing(pawn,undergroundFrame,true));
            Check("interruption-preserves-downward-progress",undergroundRock.HitPoints==hp&&undergroundFrame.resourceContainer.TotalStackCount==undergroundMaterials);
            File.WriteAllText(Path.Combine(directory,"underground-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,frame=undergroundFrame.ThingID,rock=undergroundRock.ThingID,hp,job=pawn.CurJob!.loadID,hives=map.listerThings.ThingsOfDef(ThingDefOf.Hive).Count,materials=undergroundMaterials}));
            GameDataSaveLoader.SaveGame("RimLayers_V1Underground");
            LayerView.Switch(owner,owner.Layout!.LayerAt(-1));CameraJumper.TryJump(undergroundRock,CameraJumper.MovementMode.Cut);
            pendingScreenshot=Path.Combine(directory,"underground-unexplored.png");screenshotHold=3;stage=11;ticks=0;return;
        }
        if(undergroundPhase==2)
        {
            if(!undergroundRock.Destroyed){if(ticks>10000)throw new Exception("Restored downward mining did not finish: "+DescribePawn());return;}
            owner.TryDecode(undergroundEntrance,out var entrance);
            var product=undergroundRock.def.building.mineableThing;
            Check("downward-mining-produces-native-resource-on-B1",map.listerThings.ThingsOfDef(product).Any(t=>owner.TryDecode(t.Position,out var q)&&owner.Elevation(q.LayerId)==-1&&t.stackCount>0));
            undergroundFrame.CompleteConstruction(pawn);
            Check("downward-connector-completes-after-mining",undergroundFrame.Destroyed&&undergroundEntrance.GetEdifice(map) is Building_Stairs&&owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==undergroundEntrance));
            destination=owner.Adjacent(undergroundEntrance,-1);expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;
            retainedJob=true;undergroundPhase=3;ticks=0;return;
        }
        if(undergroundPhase==3)
        {
            if(pawn.Position!=destination){retainedJob&=ExpectedJobMatches();if(ticks>5000)throw new Exception("B1 entry failed: "+DescribePawn());return;}
            Check("same-native-job-walks-down-to-B1",retainedJob&&owner.Elevation(1)==-1);
            undergroundEntrance=destination;undergroundFrame=UndergroundFrame(undergroundEntrance);undergroundFrame.CompleteConstruction(pawn);
            undergroundRock=VerticalMining.Rock(undergroundFrame)!;
            Check("B1-can-prepare-B2-without-allocating-upper-floors",owner.Layout!.LayerCount==3&&owner.Layout.HighestElevation==0&&owner.Underground.Generated(owner.Layout.LayerAt(-2)));
            if(undergroundRock!=null){undergroundPhase=4;ticks=0;}
            else{BeginUndergroundTrip();}return;
        }
        if(undergroundPhase==4)
        {
            if(ticks==2){var job=UndergroundMiner.JobOnThing(pawn,undergroundFrame,true);Check("B2-excavation-uses-real-miner",job!=null);if(job==null){Finish();return;}pawn.jobs.TryTakeOrderedJob(job);}
            if(!undergroundRock.Destroyed){if(ticks>10000)throw new Exception("B2 excavation failed: "+DescribePawn());return;}
            undergroundFrame.CompleteConstruction(pawn);
            Check("second-downward-connector-completes",undergroundFrame.Destroyed,$"validation={owner.Structure.Validate(DownDef,undergroundEntrance,Rot4.North).Reason}; sourceTerrain={undergroundEntrance.GetTerrain(map).defName}; exitTerrain={owner.Adjacent(undergroundEntrance,-1).GetTerrain(map).defName}; exitEdifice={owner.Adjacent(undergroundEntrance,-1).GetEdifice(map)?.def.defName}; materials={undergroundFrame.resourceContainer.TotalStackCount}");
            BeginUndergroundTrip();return;
        }
        if(undergroundPhase is 6 or 7)
        {
            retainedJob&=ExpectedJobMatches()||pawn.Position==destination;
            if(pawn.Position!=destination){if(ticks>6000)throw new Exception("Underground round-trip failed: "+DescribePawn());return;}
            if(undergroundPhase==6)
            {
                Check("native-job-enters-B2",retainedJob&&owner.TryDecode(pawn.Position,out var q)&&owner.Elevation(q.LayerId)==-2);
                owner.TryDecode(undergroundEntrance,out var entry);destination=new(entry.X,0,entry.Z);
                expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;undergroundPhase=7;ticks=0;return;
            }
            Check("native-job-returns-through-B1-to-surface",retainedJob&&owner.IsGroundCell(pawn.Position));
            undergroundInsect=map.mapPawns.AllPawnsSpawned.First(p=>p.Faction==Faction.OfInsects);
            undergroundInsect.jobs.StopAll();undergroundInsect.Position=owner.Adjacent(undergroundEntrance,-1);undergroundInsect.Notify_Teleported();
            undergroundInsectLord=undergroundInsect.GetLord();
            pawn.drafter.Drafted=true;frozenCombatPawn=pawn;
            undergroundInsect.mindState.enemyTarget=pawn;undergroundInsect.mindState.lastEngageTargetTick=Find.TickManager.TicksGame;
            var giver=new JobGiver_AIFightEnemies();AccessTools.Field(typeof(JobGiver_AIFightEnemy),"chaseTarget").SetValue(giver,true);
            var fight=giver.TryIssueJobPackage(undergroundInsect,default).Job;
            Check("native-insect-AI-issues-cross-layer-pursuit",fight!=null,fight?.def.defName??"none");
            if(fight==null){Finish();return;}undergroundInsect.jobs.TryTakeOrderedJob(fight);undergroundPhase=8;ticks=0;return;
        }
        if(undergroundPhase==8)
        {
            if(owner.IsGroundCell(undergroundInsect.Position))
            {
                Check("native-insect-pursues-from-B2-through-B1-to-surface",undergroundInsect.GetLord()==undergroundInsectLord);
                undergroundInsect.jobs.StopAll();undergroundInsect.Position=owner.Adjacent(undergroundEntrance,-1);undergroundInsect.Notify_Teleported();frozenCombatPawn=null;
                UndergroundFinalChecks();return;
            }
            if(ticks>6000){Check("native-insect-pursues-from-B2-through-B1-to-surface",false,$"{undergroundInsect.Position}, job={undergroundInsect.CurJob}, enemy={undergroundInsect.mindState.enemyTarget}");Finish();}
        }
    }
    private static void BeginUndergroundTrip()
    {
        destination=owner.Adjacent(undergroundEntrance,-1);expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);
        pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;retainedJob=true;undergroundPhase=6;ticks=0;
    }
    private static void RestoreUnderground()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"underground-baseline.json")));var b=doc.RootElement;
        pawn=map.mapPawns.FreeColonistsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        undergroundFrame=map.listerThings.AllThings.OfType<Frame>().Single(t=>t.ThingID==b.GetProperty("frame").GetString());
        undergroundRock=map.listerThings.AllThings.OfType<Mineable>().Single(t=>t.ThingID==b.GetProperty("rock").GetString());
        undergroundEntrance=undergroundFrame.Position;undergroundMaterials=b.GetProperty("materials").GetInt32();
        Check("reload-keeps-mapping-mining-job-damage-and-materials",owner.Layout!.Elevations.SequenceEqual(new[]{0,-1})&&undergroundRock.HitPoints==b.GetProperty("hp").GetInt32()&&pawn.CurJob?.loadID==b.GetProperty("job").GetInt32()&&pawn.jobs.curDriver is JobDriver_VerticalMine&&undergroundFrame.resourceContainer.TotalStackCount==undergroundMaterials);
        Check("reload-does-not-refresh-exploration-hives",map.listerThings.ThingsOfDef(ThingDefOf.Hive).Count==b.GetProperty("hives").GetInt32());
        undergroundPhase=2;stage=430;ticks=0;
    }

    private static void CheckUndergroundMixedShip()
    {
        var area=new CellRect(76,72,9,7);
        foreach(var c in area)
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.roofGrid.SetRoof(c,null);
            if(map.terrainGrid.UnderTerrainAt(c)!=null)map.terrainGrid.RemoveTopLayer(c,false);
            map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);
        }
        var engine=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new(79,0,74),map);
        engine.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(ThingDefOf.PilotConsole,new(82,0,74),map).SetFaction(Faction.OfPlayer);
        foreach(var p in new[]{new IntVec3(76,0,72),new IntVec3(84,0,72),new IntVec3(76,0,78),new IntVec3(84,0,78)})
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),p,map).SetFaction(Faction.OfPlayer);
        foreach(var c in area)
        {
            map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
            var upper=UndergroundAt(1,c.x,c.z);
            RimLayers.Ships.ShipDeckTerrain.Install(owner,upper,false);map.fogGrid.Unfog(upper);
        }
        var cargo=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Silver),UndergroundAt(1,82,76),map);
        cargo.stackCount=17;owner.Ships.Tick();
        var structure=owner.Ships.Get(engine);
        Check("mixed-layout-ship-topology-uses-height-not-band-gap",structure is {Bottom:0,Top:1}&&structure.Cells.Count==126);
        int undergroundThings=map.listerThings.AllThings.OfType<Mineable>().Count(t=>owner.TryDecode(t.Position,out var q)&&owner.Elevation(q.LayerId)<0);
        engine.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
        var ship=GravshipUtility.GenerateGravship(engine);
        Check("native-ship-capture-encodes-real-upper-deck-not-B1",RimLayers.Ships.ShipLayers.TryData(ship,out var payload)&&payload.layerCount==2&&payload.floors.Any(f=>f.local.y==1)&&ship.Things.Contains(cargo));
        var landing=new IntVec3(30,0,80);
        // Wildlife can enter the old open pad while the mining test runs. Prepare an actual
        // empty native landing footprint, without weakening production collision checks.
        var pad=new CellRect(18,68,30,30);ClearRecoveryColumn(map,pad);
        foreach(var visitor in map.mapPawns.AllPawnsSpawned.Where(p=>owner.TryDecode(p.Position,out var at)&&pad.Contains(new IntVec3(at.X,0,at.Z))).ToArray())
        {var safe=GenRadial.RadialCellsAround(new IntVec3(110,0,110),8,true).First(c=>c.InBounds(map)&&c.Standable(map)&&c.GetFirstPawn(map)==null);visitor.Position=safe;visitor.Notify_Teleported();}
        var report=RimLayers.Ships.ShipLanding.Check(ship,landing,map);
        Check("mixed-layout-native-ship-landing-preview-accepts-surface",report.Accepted,report.Reason);
        if(!report.Accepted)return;
        GravshipPlacementUtility.PlaceGravshipInMap(ship,landing,map,out _);Current.Game.Gravship=null;
        Check("native-ship-landing-restores-cargo-to-physical-upper-floor",cargo.Spawned&&cargo.stackCount==17&&owner.TryDecode(cargo.Position,out var at)&&at.LayerId==owner.Layout!.LayerAt(1)&&owner.Elevation(at.LayerId)==1);
        Check("native-ship-does-not-capture-or-overwrite-underground-rock",undergroundThings==map.listerThings.AllThings.OfType<Mineable>().Count(t=>owner.TryDecode(t.Position,out var q)&&owner.Elevation(q.LayerId)<0));
    }

    private static void UndergroundFinalChecks()
    {
        var b2=owner.Layout!.LayerAt(-2);
        Check("default-underground-limit-rejects-B3-allocation",!LayerExpansionQuota.For(map,-3).Accepted);
        var saved=owner.Layout;
        var timing=LiveMapExpansion.EnsureLayerCount(map,3);
        Check("existing-upper-expansion-contract-preserved-after-underground",owner.Layout!.LayerCount==5&&owner.Layout.Extends(saved)&&owner.Layout.LayerAt(1)==3&&owner.Layout.LayerAt(2)==4);
        LayerView.Switch(owner,owner.Layout.LayerAt(-1));
        Check("hotkeys-follow-physical-order-not-band-number",LayerView.SwitchRelative(owner,1)&&owner.Elevation(owner.ViewedLayer)==0);
        Check("underground-cannot-receive-sky-deliveries",!owner.Skyfall.CanArrive(UndergroundAt(-1,60,60),true));
        var source=owner.Adjacent(undergroundEntrance,-1);
        Check("underground-floor-blocks-shots-from-surface",!owner.Combat.Solve(pawn,new(60,0,60),source,999).Clear);
        Check("native-exploration-hives-exist",map.listerThings.ThingsOfDef(ThingDefOf.Hive).Count>0,map.listerThings.ThingsOfDef(ThingDefOf.Hive).Count.ToString());
        if(ModsConfig.OdysseyActive)CheckUndergroundMixedShip();
        var cave=owner.LayerRect(b2).First(c=>c.Standable(map)&&c.GetFirstBuilding(map)==null&&owner.TryDecode(c,out var q)&&q.X>10&&q.X<owner.Layout.Width-10&&q.Z>10&&q.Z<owner.Layout.Height-10&&c.GetRoom(map)?.CellCount>=10);
        map.fogGrid.Unfog(cave);map.fogGrid.FloodUnfogAdjacent(cave);
        var colony=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),cave,map);colony.SetFaction(Faction.OfPlayer);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();cave.GetRoom(map).Temperature=15;
        Find.TickManager.DoSingleTick(); // Refresh native per-tick temperature/path snapshots after fixture changes.
        bool found=InfestationCellFinder.TryFindCell(out var infestation,map);
        Check("native-infestation-finder-accepts-explored-underground",found&&owner.TryDecode(infestation,out var inf)&&owner.Elevation(inf.LayerId)<0,infestation.ToString());
        bool oldAllow=Find.Storyteller.difficulty.allowCaveHives;
        int beforeHives=map.listerThings.ThingsOfDef(ThingDefOf.Hive).Count;
        try
        {
            Find.Storyteller.difficulty.allowCaveHives=false;
            AccessTools.Method(typeof(UndergroundService),"SpawnCaveHives").Invoke(owner.Underground,new object?[]{b2,new List<IntVec3>(),null});
            Check("difficulty-disabled-cave-hives-spawn-nothing",beforeHives==map.listerThings.ThingsOfDef(ThingDefOf.Hive).Count);
        }
        finally{Find.Storyteller.difficulty.allowCaveHives=oldAllow;}
        var incident=DefDatabase<IncidentDef>.GetNamed("Infestation");
        bool executed=incident.Worker.TryExecute(new IncidentParms{target=map,points=500,faction=Faction.OfInsects});
        Check("native-infestation-incident-executes",executed);
        File.WriteAllText(Path.Combine(directory,"underground-timing.json"),JsonSerializer.Serialize(new{timing.Milliseconds,timing.Phases,layers=owner.Layout.Elevations,pawns=map.mapPawns.AllPawnsSpawned.Count}));
        LayerView.Switch(owner,b2);CameraJumper.TryJump(cave,map,CameraJumper.MovementMode.Cut);Find.CameraDriver.SetRootPosAndSize(cave.ToVector3Shifted(),18);
        pendingScreenshot=Path.Combine(directory,"underground-explored.png");screenshotHold=3;undergroundPhase=5;ticks=0;
    }
}
