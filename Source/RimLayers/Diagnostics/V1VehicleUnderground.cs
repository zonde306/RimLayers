using System.Text.Json;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Presentation;
using RimLayers.Vehicles;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static bool UndergroundLift=>GenCommandLine.CommandLineArgPassed("rimlayersvehicleunderground");
    private static int LiftLayer(int index)=>UndergroundLift?(index==0?0:owner.Layout!.TryGetLayerAtElevation(-2,out var id)?id:throw new Exception("Missing B2")):index;
    private static IntVec3 LiftAt(int index,int x,int z)=>At(LiftLayer(index),x,z);
    private static Pawn shaftMiner=null!;
    private static Building_VehicleConnector middleStation=null!;
    private static WorkGiver_VehicleShaft ShaftMinerWork=>(WorkGiver_VehicleShaft)DefDatabase<WorkGiverDef>.GetNamed("RL_ExcavateVehicleShaft").Worker;
    private static Building_VehicleConnector MakeUndergroundStation(int elevation)
    {
        var cell=owner.AtElevation(elevation,85,85);var def=DefDatabase<ThingDef>.GetNamed("RL_VehicleLift");
        var report=GenConstruct.CanPlaceBlueprintAt(def,cell,Rot4.North,map);
        if(!report.Accepted)throw new Exception("Underground station cannot be planned: "+report.Reason);
        var frame=(Frame)ThingMaker.MakeThing(def.frameDef);frame.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(frame,cell,map);
        foreach(var cost in def.CostListAdjusted(null)){var item=ThingMaker.MakeThing(cost.thingDef);item.stackCount=cost.count;frame.resourceContainer.TryAdd(item);}
        frame.workDone=frame.WorkToBuild;frame.CompleteConstruction(shaftMiner??map.mapPawns.FreeColonistsSpawned.First());
        return map.GetComponent<VehicleTraffic>().Connectors.Single(s=>s.Position==cell);
    }
    private static void BeginUndergroundLift()
    {
        LiveMapExpansion.EnsureElevation(map,-2);
        Check("underground-expansion-keeps-existing-positive-layer-ids",owner.Elevation(1)==1&&owner.Elevation(2)==2&&LiftLayer(1)>2);
        // Existing excavated rooms are fixture inputs; retain native underground terrain and roofs.
        foreach(int elevation in new[]{0,-1,-2})
        {
            foreach(var local in new CellRect(74,74,23,23))
            {
                var cell=owner.AtElevation(elevation,local.x,local.z);
                foreach(var t in cell.GetThingList(map).ToArray())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
                if(elevation==0){map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Soil);}
                map.fogGrid.Unfog(cell);
            }
            owner.Layout!.TryGetLayerAtElevation(elevation,out var layer);
            for(int x=75;x<=95;x+=10)for(int z=75;z<=95;z+=10)
                if(x!=85||z!=85)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,x,z),map);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>owner.TryDecode(c,out var l)&&new CellRect(74,74,23,23).Contains(new(l.X,0,l.Z)));
        shaftMiner=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        GenSpawn.Spawn(shaftMiner,LiftAt(0,80,85),map);shaftMiner.skills.GetSkill(SkillDefOf.Mining).Level=20;
        shaftMiner.health.AddHediff(DefDatabase<HediffDef>.GetNamed("RL_TestShaftMiner")); // Accelerates work, not the production job/commit.
        vehicleStations=new[]{MakeUndergroundStation(0),MakeUndergroundStation(-2)};
        middleStation=MakeUndergroundStation(-1);
        foreach(var station in vehicleStations.Append(middleStation))
        {
            owner.TryDecode(station.Position,out var at);
            UtilityBuilding("WoodFiredGenerator",at.LayerId,91,85).GetComp<CompRefuelable>().Refuel(100);
            UtilityBuilding("WoodFiredGenerator",at.LayerId,91,89).GetComp<CompRefuelable>().Refuel(100);
            for(int z=85;z<=89;z++)UtilityBuilding("PowerConduit",at.LayerId,90,z);
            for(int x=85;x<90;x++)UtilityBuilding("PowerConduit",at.LayerId,x,85);
        }
        Check("building-stations-preserves-natural-rock-roofs",middleStation.OccupiedRect().All(c=>map.roofGrid.RoofAt(c)==RoofDefOf.RoofRockThick)&&vehicleStations[1].OccupiedRect().All(c=>map.roofGrid.RoofAt(c)==RoofDefOf.RoofRockThick));
        Check("solid-earth-rejects-route-before-excavation",!VehicleRoutePlanner.ShaftIntact(map.GetComponent<VehicleTraffic>(),owner,vehicleStations[0],vehicleStations[1],false));
        var probe=vehicleStations[0].OccupiedRect().First();map.terrainGrid.SetTerrain(probe,TerrainDefOf.Concrete);
        Check("paved-floor-requires-deliberate-removal",!vehicleStations[0].ShaftExcavationConditions().Accepted);
        map.terrainGrid.RemoveTopLayer(probe,false);
        Check("excavation-command-visible-on-ground-station",vehicleStations[0].GetGizmos().OfType<Command_Action>().Any(c=>c.defaultLabel=="RL_VehicleDigStart".Translate().ToString()));
        var lowerProbe=middleStation.OccupiedRect().First();var nativeRoof=map.roofGrid.RoofAt(lowerProbe);
        map.roofGrid.SetRoof(lowerProbe,RoofDefOf.RoofConstructed);
        Check("constructed-roof-requires-deliberate-removal",!vehicleStations[0].ShaftExcavationConditions().Accepted);
        map.roofGrid.SetRoof(lowerProbe,nativeRoof);
        map.terrainGrid.SetTerrain(lowerProbe,TerrainDefOf.Concrete);
        Check("receiving-cabin-paving-does-not-block-ceiling-excavation",vehicleStations[0].ShaftExcavationConditions().Accepted);
        map.terrainGrid.RemoveTopLayer(lowerProbe,false);
        var original=testVehicle.Position;VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(testVehicle,vehicleStations[0].Position,0));
        Check("occupied-cabin-refuses-shaft-construction",!vehicleStations[0].ShaftExcavationConditions().Accepted);
        VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(testVehicle,original,0));
        Check("explicit-excavation-order-accepted",vehicleStations[0].StartShaftExcavation());
        vehiclePhase=-10;stage=551;ticks=0;
    }
    private static void StartMinerJob(Building_VehicleConnector station)
    {
        var job=ShaftMinerWork.JobOnThing(shaftMiner,station,true)??throw new Exception("No native miner job: "+station.ShaftExcavationConditions(shaftMiner).Reason);
        Check("miner-job-accepted-"+owner.LevelName(owner.TryDecode(station.Position,out var at)?at.LayerId:0),shaftMiner.jobs.TryTakeOrderedJob(job));
    }
    private static bool VehicleShaftTestStep()
    {
        if(vehiclePhase==-10)
        {
            if(ticks<60)return true;
            StartMinerJob(vehicleStations[0]);vehiclePhase=-11;ticks=0;return true;
        }
        if(vehiclePhase==-11)
        {
            int open=vehicleStations[0].OccupiedRect().Count(vehicleStations[0].ShaftCellOpen);
            if(open<5){if(ticks>1000)throw new Exception("Shaft worker did not drill: "+shaftMiner.CurJob);return true;}
            Check("native-mining-job-opens-real-roof-and-retractable-deck",shaftMiner.CurJobDef?.defName=="RL_ExcavateVehicleShaft"&&open<81);
            vehicleStations[0].CancelShaftExcavation();float work=vehicleStations[0].ShaftWork;
            Check("pause-retains-work-and-openings",!vehicleStations[0].ExcavatingShaft&&vehicleStations[0].OccupiedRect().Count(vehicleStations[0].ShaftCellOpen)==open&&vehicleStations[0].ShaftWork==work);
            Check("resume-excavation-accepted",vehicleStations[0].StartShaftExcavation());
            Check("partial-shaft-cannot-transport-vehicles",!VehicleRoutePlanner.ShaftIntact(map.GetComponent<VehicleTraffic>(),owner,vehicleStations[0],middleStation,false));
            File.WriteAllText(Path.Combine(directory,"vehicle-shaft-baseline.json"),JsonSerializer.Serialize(new{vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,miner=shaftMiner.ThingID,open,work,job=shaftMiner.CurJob!.loadID}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleLift");
            LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.Selector.Select(vehicleStations[0]);
            Find.CameraDriver.SetRootPosAndSize(vehicleStations[0].Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"underground-shaft-construction.png");screenshotHold=3;
            vehiclePhase=99;ticks=0;return true;
        }
        if(vehiclePhase==-12)
        {
            if(vehicleStations[0].ExcavatingShaft){if(ticks>1500)throw new Exception("Restarted shaft work stalled: "+shaftMiner.CurJob);return true;}
            Check("restarted-miner-completes-all-81-cells",vehicleStations[0].OccupiedRect().All(vehicleStations[0].ShaftCellOpen));
            Check("missing-B1-B2-segment-still-rejects-direct-route",!VehicleRoutePlanner.ShaftIntact(map.GetComponent<VehicleTraffic>(),owner,vehicleStations[0],vehicleStations[1],false));
            shaftMiner.jobs.StopAll();shaftMiner.Position=middleStation.Position+new IntVec3(-5,0,0);shaftMiner.Notify_Teleported();
            Check("B1-excavation-order-accepted",middleStation.StartShaftExcavation());
            vehiclePhase=-13;ticks=0;return true;
        }
        if(vehiclePhase==-13)
        {
            if(ticks<60)return true;
            StartMinerJob(middleStation);vehiclePhase=-14;ticks=0;return true;
        }
        if(vehiclePhase==-14)
        {
            if(middleStation.ExcavatingShaft){if(ticks>1500)throw new Exception("B2 shaft work stalled: "+shaftMiner.CurJob);return true;}
            Check("complete-ground-B1-B2-shaft-is-bidirectional",VehicleRoutePlanner.ShaftIntact(map.GetComponent<VehicleTraffic>(),owner,vehicleStations[0],vehicleStations[1],false)&&VehicleRoutePlanner.ShaftIntact(map.GetComponent<VehicleTraffic>(),owner,vehicleStations[1],vehicleStations[0],false));
            Check("outside-cabin-natural-roof-is-preserved",map.roofGrid.RoofAt(owner.AtElevation(-1,80,85))==RoofDefOf.RoofRockThick&&map.roofGrid.RoofAt(owner.AtElevation(-2,80,85))==RoofDefOf.RoofRockThick);
            shaftMiner.jobs.StopAll();shaftMiner.DeSpawn();DamageVehicleFixture(testVehicle);
            BuildVehicleLiftFixture();return true;
        }
        if(vehiclePhase==30)
        {
            var hole=owner.AtElevation(-1,85,85);
            Check("removed-underground-deck-remains-a-real-opening",hole.GetTerrain(map)==RimLayersDefOf.RL_OpenAir&&!owner.Structure.HasSurface(hole)&&!owner.Mountains.IsNaturalFloor(hole));
            Check("removed-ground-deck-does-not-become-solid-earth",!owner.Structure.HasSurface(LiftAt(0,85,85)));
            vehicleStations[0]=MakeUndergroundStation(0);middleStation=MakeUndergroundStation(-1);
            Check("rebuild-over-underground-opening-restores-deck-not-earth",middleStation.OccupiedRect().All(c=>c.GetTerrain(map).defName=="RL_VehicleDeck"));
            vehiclePhase=31;ticks=0;return true;
        }
        if(vehiclePhase==31)
        {
            if(ticks<120||!middleStation.Powered||!vehicleStations[0].Powered)return true;
            destination=LiftAt(0,82,92);testVehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination));vehiclePhase=32;ticks=0;return true;
        }
        if(vehiclePhase==32)
        {
            if(testVehicle.Position!=destination||!testVehicle.Spawned||testVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>4000)throw new Exception("B2-to-ground round trip stalled.");return true;}
            Check("B2-to-surface-completes-final-native-leg",VehicleNativeStopped()&&testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37);
            destination=owner.AtElevation(-1,82,92);testVehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination));vehiclePhase=33;ticks=0;return true;
        }
        if(vehiclePhase==33)
        {
            if(testVehicle.Position!=destination||!testVehicle.Spawned||testVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>4000)throw new Exception("Intermediate B1 destination stalled.");return true;}
            Check("B1-is-a-selectable-intermediate-destination",VehicleNativeStopped()&&map.GetComponent<VehicleTraffic>().Trip(testVehicle)==null);
            owner.TryDecode(destination,out var at);LayerView.Switch(owner,at.LayerId);Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            Find.CameraDriver.SetRootPosAndSize(destination.ToVector3Shifted(),12);pendingScreenshot=Path.Combine(directory,"underground-B1-arrival.png");screenshotHold=3;
            vehiclePhase=99;ticks=0;return true;
        }
        return false;
    }
    private static void RestoreShaftWork()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-shaft-baseline.json")));var data=document.RootElement;
        var traffic=map.GetComponent<VehicleTraffic>();
        testVehicle=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("vehicle").GetString());
        testVehicleCargo=testVehicle.inventory.innerContainer.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        shaftMiner=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("miner").GetString());
        testVehicleCrew.Clear();testVehicleCrew.AddRange(((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>());
        vehicleStations=new[]{traffic.Connectors.Single(s=>s.Position==LiftAt(0,85,85)),traffic.Connectors.Single(s=>s.Position==LiftAt(1,85,85))};
        middleStation=traffic.Connectors.Single(s=>s.Position==owner.AtElevation(-1,85,85));
        Check("restart-keeps-mining-job-and-work-progress",shaftMiner.CurJob?.loadID==data.GetProperty("job").GetInt32()&&Math.Abs(vehicleStations[0].ShaftWork-data.GetProperty("work").GetSingle())<.01&&vehicleStations[0].ExcavatingShaft);
        Check("restart-keeps-excavated-cell-count",vehicleStations[0].OccupiedRect().Count(vehicleStations[0].ShaftCellOpen)==data.GetProperty("open").GetInt32());
        BindBoardIntegrity();vehiclePhase=-12;stage=551;ticks=0;
    }
}
