using System.Text.Json;
using RimLayers.Core;
using RimLayers.Vehicles;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static Building_VehicleConnector[] vehicleStations=null!;
    private static int liftVehicleJob,pausedRemaining;
    private static int liftVisualPhase,liftVisualInitialView;
    private static Pawn secondLiftVehicle=null!;
    private static Pawn shaftVisitor=null!;
    private static Thing recoveryBlocker=null!;
    // Leave more than half the long chassis outside the cabin at every final heading.
    private static readonly IntVec3 secondaryGoalLocal=new(88,0,76);
    private static void BeginVehicleLift()
    {
        BindBoardIntegrity();
        if(UndergroundLift){BeginUndergroundLift();return;}
        // Dedicated clear apron: columns are outside the nine-cell cabin, never hidden inside it.
        foreach(var c in new CellRect(74,74,23,23))
        {
            var ground=LiftAt(0,c.x,c.z);
            foreach(var t in ground.GetThingList(map).ToArray())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(ground,null);map.terrainGrid.SetTerrain(ground,TerrainDefOf.Concrete);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(74,74,23,23).Contains(c));
        for(int x=75;x<=95;x+=10)for(int z=75;z<=95;z+=10)
            if(x!=85||z!=85)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),LiftAt(0,x,z),map);
        foreach(var c in new CellRect(76,76,21,21))
        {
            map.roofGrid.SetRoof(LiftAt(0,c.x,c.z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(LiftAt(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);
            map.fogGrid.Unfog(LiftAt(0,c.x,c.z));map.fogGrid.Unfog(LiftAt(1,c.x,c.z));
        }
        foreach(var c in new CellRect(81,81,9,9))
        {
            map.terrainGrid.SetTerrain(LiftAt(1,c.x,c.z),RimLayersDefOf.RL_OpenAir);
            map.roofGrid.SetRoof(LiftAt(0,c.x,c.z),null);
        }
        Check("station-refuses-uncleared-solid-floor",!owner.Structure.Validate(DefDatabase<ThingDef>.GetNamed("RL_VehicleLift"),LiftAt(1,84,85),Rot4.North,true).Accepted);
        Check("station-accepts-cleared-supported-opening",owner.Structure.Validate(DefDatabase<ThingDef>.GetNamed("RL_VehicleLift"),LiftAt(1,85,85),Rot4.North,true).Accepted);
        DamageVehicleFixture(testVehicle);
        StartVehiclePreview("RL_VehicleLift",LiftAt(1,85,85),"vehicle-lift-preview-clear.png");
        vehiclePhase=-2;stage=551;ticks=0;
    }
    private static void BuildVehicleLiftFixture()
    {
        if(!UndergroundLift)vehicleStations=Enumerable.Range(0,2).Select(l=>{
            var station=(Building_VehicleConnector)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("RL_VehicleLift"),At(l,85,85),map);
            station.SetFaction(Faction.OfPlayer);
            var generator=UtilityBuilding("WoodFiredGenerator",l,91,85);generator.GetComp<CompRefuelable>().Refuel(100);
            UtilityBuilding("WoodFiredGenerator",l,91,89).GetComp<CompRefuelable>().Refuel(100);
            for(int z=85;z<=89;z++)UtilityBuilding("PowerConduit",l,90,z);
            for(int x=85;x<90;x++)UtilityBuilding("PowerConduit",l,x,85);
            return station;
        }).ToArray();
        map.powerNetManager.UpdatePowerNetsAndConnections_First();
        VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(testVehicle,LiftAt(0,85,79),0));
        destination=LiftAt(1,82,92);
        var generatorType=testVehicle.GetType().Assembly.GetType("Vehicles.VehicleSpawner",true)!;
        secondLiftVehicle=(Pawn)FrameworkVehicleAdapter.Method(generatorType,"GenerateVehicle",testVehicle.GetType(),testVehicle.def.GetType(),typeof(Faction)).Invoke(null,new object[]{testVehicle.def,Faction.OfPlayer})!;
        GenSpawn.Spawn(secondLiftVehicle,LiftAt(1,88,92),map,Rot4.South);
        if(testVehicle.def.defName!="RL_TestVehicle")
        {
            var driver=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
            if(!(bool)FrameworkVehicleAdapter.Method(secondLiftVehicle.GetType(),"TryAddPawn",typeof(bool),typeof(Pawn)).Invoke(secondLiftVehicle,new object[]{driver})!)throw new Exception("Second driver cannot board.");
            foreach(var fuel in secondLiftVehicle.AllComps.Where(c=>c.GetType().FullName=="Vehicles.CompFueledTravel"))FrameworkVehicleAdapter.Method(fuel.GetType(),"Refuel",typeof(void),typeof(float)).Invoke(fuel,new object[]{10000f});
        }
        var ignition=FrameworkVehicleAdapter.Field(secondLiftVehicle.GetType(),"ignition").GetValue(secondLiftVehicle)!;
        FrameworkVehicleAdapter.Property(ignition.GetType(),"Drafted").SetValue(ignition,true);
        VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(secondLiftVehicle,LiftAt(1,88,92),180));
        vehiclePhase=0;stage=551;ticks=0;
    }
    private static bool VehicleNativeStopped()
    {
        var pather=FrameworkVehicleAdapter.Field(testVehicle.GetType(),"vehiclePather").GetValue(testVehicle)!;
        return !(bool)FrameworkVehicleAdapter.Property(pather.GetType(),"Moving").GetValue(pather)!;
    }
    private static void VehicleLiftStep()
    {
        ticks++;
        if(UndergroundLift&&VehicleShaftTestStep())return;
        var traffic=map.GetComponent<VehicleTraffic>();var trip=traffic.Trip(testVehicle);
        if(vehiclePhase==-2)
        {
            StartVehiclePreview("RL_VehicleLift",LiftAt(1,84,85),"vehicle-lift-preview-blocked.png");
            vehiclePhase=-1;return;
        }
        if(vehiclePhase==-1){vehiclePreviewDef=null;BuildVehicleLiftFixture();return;}
        if(vehiclePhase==0)
        {
            if(ticks<120||vehicleStations.Any(s=>!s.Powered)){if(ticks>1000)throw new Exception("Test generators did not power both vehicle stations.");return;}
            if(GenCommandLine.CommandLineArgPassed("rimlayersvehicleexpansion"))
            {
                var size=map.Size;var layout=owner.Layout;var system=map.components.Single(c=>c.GetType().FullName=="Vehicles.VehiclePathingSystem");
                var field=FrameworkVehicleAdapter.Field(system.GetType(),"vehicleData");var before=field.GetValue(system);
                RimLayersMod.Settings.UpperLimit=3;
                var activeJob=JobMaker.MakeJob(JobDefOf.Goto,LiftAt(0,85,81));testVehicle.jobs.TryTakeOrderedJob(activeJob);
                bool rolledBack=false;
                try{RimLayers.Generation.LiveMapExpansion.EnsureElevation(map,3,()=>throw new InvalidOperationException("vehicle rollback probe"));}
                catch(InvalidOperationException error)when(error.Message=="vehicle rollback probe"){rolledBack=true;}
                Check("vehicle-grid-expansion-rollback-restores-original-graph",rolledBack&&map.Size==size&&ReferenceEquals(owner.Layout,layout)&&ReferenceEquals(before,field.GetValue(system)));
                Check("rollback-retains-original-active-vehicle-job",testVehicle.CurJob==activeJob);
                RimLayers.Generation.LiveMapExpansion.EnsureElevation(map,3);
                Check("expansion-rebuilds-vehicle-pathing-for-new-map-size",map.Size.z>size.z&&!ReferenceEquals(before,field.GetValue(system))&&owner.Layout!.HighestElevation==3);
                Check("expansion-retains-original-vehicle-and-active-job",testVehicle.CurJob==activeJob&&testVehicle.inventory.innerContainer.Contains(testVehicleCargo));
                testVehicle.jobs.StopAll();
                VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(testVehicle,LiftAt(0,85,79),0));
            }
            Check("lift-nine-by-nine-whole-hull-fits",vehicleStations.All(s=>s.def.size==new IntVec2(9,9)&&VehicleSpace.CanStand(owner,testVehicle,s.Position,0,out _)));
            Check("complete-route-includes-lift-and-final-native-leg",VehicleRoutePlanner.FirstCrossing(traffic,testVehicle,destination,PathEndMode.OnCell)?.Entry==vehicleStations[0]);
            var job=JobMaker.MakeJob(JobDefOf.Goto,destination);liftVehicleJob=job.loadID;
            Check("native-cross-floor-order-accepted",testVehicle.jobs.TryTakeOrderedJob(job));
            Check("opposing-vehicle-order-accepted",secondLiftVehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,LiftAt(0,secondaryGoalLocal.x,secondaryGoalLocal.z))));
            vehiclePhase=1;ticks=0;return;
        }
        if(vehiclePhase==1)
        {
            if(trip?.Phase!=VehicleTravelPhase.Held){if(ticks>2400)throw new Exception("Vehicle did not board lift. "+trip?.Phase+" "+VehicleMotionTrace());return;}
            Check("boarding-retains-original-job-and-destination",testVehicle.CurJob?.loadID==liftVehicleJob&&trip.Destination.Cell==destination);
            Check("lift-holds-original-unspawned-vehicle",!testVehicle.Spawned&&traffic.GetDirectlyHeldThings().Contains(testVehicle));
            vehicleStations[0].GetComp<CompFlickable>().DoFlick();vehicleStations[0].GetComp<CompPowerTrader>().PowerOn=false;
            pausedRemaining=trip.RemainingTicks;vehiclePhase=2;ticks=0;return;
        }
        if(vehiclePhase==2)
        {
            if(ticks<60)return;
            Check("opposing-vehicle-waits-outside-reserved-cabin",traffic.Trip(secondLiftVehicle)?.Phase==VehicleTravelPhase.Queued&&secondLiftVehicle.Position==LiftAt(1,88,92),$"phase={traffic.Trip(secondLiftVehicle)?.Phase},position={secondLiftVehicle.Position}");
            Check("power-loss-pauses-without-emergency-transit",trip?.RemainingTicks==pausedRemaining&&!testVehicle.Spawned);
            File.WriteAllText(Path.Combine(directory,"vehicle-lift-baseline.json"),JsonSerializer.Serialize(new{
                second=secondLiftVehicle.ThingID,vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray(),state=VehicleIntegrity(testVehicle),job=liftVehicleJob,remaining=pausedRemaining}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleLift");
            LayerView.Switch(owner,LiftLayer(0));Find.Selector.ClearSelection();Find.Selector.Select(vehicleStations[0]);
            Find.CameraDriver.SetRootPosAndSize(LiftAt(0,85,85).ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-lift-paused.png");screenshotHold=3;
            vehiclePhase=99;ticks=0;return;
        }
        if(vehiclePhase==10)
        {
            if(ticks<60)return;
            Check("restart-keeps-outage-paused",trip?.RemainingTicks==pausedRemaining&&!testVehicle.Spawned);
            vehicleStations[0].GetComp<CompFlickable>().DoFlick();
            shaftVisitor=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
            GenSpawn.Spawn(shaftVisitor,LiftAt(1,81,81),map);
            var waiting=JobMaker.MakeJob(JobDefOf.Wait);waiting.expiryInterval=600;shaftVisitor.jobs.TryTakeOrderedJob(waiting);
            pausedRemaining=trip!.RemainingTicks;vehiclePhase=16;ticks=0;return;
        }
        if(vehiclePhase==16)
        {
            if(ticks<60)return;
            Check("person-at-cabin-corner-pauses-full-shaft",trip?.RemainingTicks==pausedRemaining&&!testVehicle.Spawned);
            // Choose the lower viewing stop explicitly; the saved game may have been
            // captured on the upper preview floor before the paused-cabin screenshot.
            LayerView.Switch(owner,LiftLayer(0));Find.CameraDriver.SetRootPosAndSize(vehicleStations[0].Position.ToVector3Shifted(),12);
            liftVisualInitialView=owner.ViewedLayer;
            shaftVisitor.DeSpawn();vehiclePhase=11;ticks=0;return;
        }
        if(vehiclePhase==11)
        {
            if(trip?.Phase==VehicleTravelPhase.Held&&1d-(double)trip.RemainingTicks/trip.TotalTicks>.25&&liftVisualPhase==0)
            {
                Check("automatic-lift-does-not-switch-view",owner.ViewedLayer==liftVisualInitialView,$"expected={liftVisualInitialView},actual={owner.ViewedLayer}");
                pendingScreenshot=Path.Combine(directory,"vehicle-lift-moving-lower.png");screenshotHold=3;liftVisualPhase=1;return;
            }
            if(trip?.Phase==VehicleTravelPhase.Held&&1d-(double)trip.RemainingTicks/trip.TotalTicks>.75&&liftVisualPhase==1)
            {
                LayerView.Switch(owner,LiftLayer(1));Find.CameraDriver.SetRootPosAndSize(vehicleStations[1].Position.ToVector3Shifted(),12);
                pendingScreenshot=Path.Combine(directory,"vehicle-lift-moving-upper.png");screenshotHold=3;liftVisualPhase=2;return;
            }
            if(!testVehicle.Spawned||testVehicle.Position!=destination||!VehicleNativeStopped()||testVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>2400)throw new Exception("Lift did not resume and finish final native leg. "+trip?.Phase+" "+VehicleMotionTrace());return;}
            Check("restart-resumes-cabin-and-completes-original-destination",traffic.Trip(testVehicle)==null&&testVehicle.Position==destination);
            Check("lift-arrival-retains-cargo-and-passengers",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37&&testVehicleCrew.All(p=>p.MapHeld==map&&!p.Spawned));
            vehiclePhase=14;ticks=0;return;
        }
        if(vehiclePhase==14)
        {
            if(!secondLiftVehicle.Spawned||secondLiftVehicle.Position!=LiftAt(0,secondaryGoalLocal.x,secondaryGoalLocal.z)||secondLiftVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>6000)throw new Exception("Queued opposing vehicle did not finish. "+traffic.Trip(secondLiftVehicle)?.Phase+" cell="+secondLiftVehicle.Position+" job="+secondLiftVehicle.CurJob+" canMove="+VehicleCompatibility.Adapter!.CanMove(secondLiftVehicle)+" "+VehicleMotionTrace(secondLiftVehicle));return;}
            Check("opposing-queue-releases-and-completes-after-first-cabin",traffic.Trip(secondLiftVehicle)==null);
            Check("opposing-vehicle-parks-fully-outside-cabin",vehicleStations[0].OccupiedRect().All(c=>!c.GetThingList(map).Contains(secondLiftVehicle)));
            destination=vehicleStations[1].Position;
            testVehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination));
            vehiclePhase=15;ticks=0;return;
        }
        if(vehiclePhase==15)
        {
            if(testVehicle.Position!=destination||!VehicleNativeStopped()||testVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>2400)throw new Exception("Vehicle did not park fully inside cabin for floor command.");return;}
            var gizmos=((IEnumerable<Gizmo>)FrameworkVehicleAdapter.Method(testVehicle.GetType(),"GetGizmos",typeof(IEnumerable<Gizmo>)).Invoke(testVehicle,null)!).ToArray();
            Check("parked-vehicle-shows-floor-command",gizmos.OfType<Command_Action>().Any(g=>g.defaultLabel=="RL_VehicleChooseFloor".Translate().ToString()));
            BindManualTest();Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            LayerView.Switch(owner,LiftLayer(1));manualToggle.Invoke(null,new object[]{testVehicle});
            Check("manual-driver-can-select-elevator-floor",SteelManualAdapter.Active(testVehicle));
            Check("floor-command-starts-original-native-goto",traffic.RequestFloor(testVehicle,vehicleStations[0]));
            Check("elevator-floor-command-releases-native-manual-drive",!SteelManualAdapter.Active(testVehicle));
            liftVehicleJob=testVehicle.CurJob!.loadID;vehiclePhase=12;ticks=0;return;
        }
        if(vehiclePhase==12)
        {
            if(trip?.Phase!=VehicleTravelPhase.Held){if(ticks>2400)throw new Exception("Return journey did not board. "+trip?.Phase+" "+testVehicle.Position+" cabin actors="+string.Join(",",vehicleStations.SelectMany(s=>s.OccupiedRect()).SelectMany(c=>c.GetThingList(map)).OfType<Pawn>().Distinct().Select(p=>p.ThingID+":"+p.Position)));return;}
            vehicleStations[0].Destroy(DestroyMode.Vanish);
            recoveryBlocker=GenSpawn.Spawn(PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer),vehicleStations[1].Position,map);
            var wait=JobMaker.MakeJob(JobDefOf.Wait);wait.expiryInterval=6000;((Pawn)recoveryBlocker).jobs.TryTakeOrderedJob(wait);
            vehiclePhase=17;ticks=0;return;
        }
        if(vehiclePhase==17)
        {
            if(ticks<60)return;
            Check("blocked-source-retains-original-vehicle-in-recovery",trip?.Phase==VehicleTravelPhase.Recovering&&!testVehicle.Spawned&&traffic.GetDirectlyHeldThings().Contains(testVehicle));
            Check("blocked-recovery-command-refuses-unsafe-unload",!traffic.RecoverAt(vehicleStations[1]));
            File.WriteAllText(Path.Combine(directory,"vehicle-lift-recovery-baseline.json"),JsonSerializer.Serialize(new{blocker=recoveryBlocker.ThingID,job=liftVehicleJob}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleLift");
            LayerView.Switch(owner,LiftLayer(1));Find.Selector.ClearSelection();Find.Selector.Select(vehicleStations[1]);
            Find.CameraDriver.SetRootPosAndSize(vehicleStations[1].Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-lift-blocked.png");screenshotHold=3;
            vehiclePhase=99;ticks=0;return;
        }
        if(vehiclePhase==20)
        {
            if(ticks<60)return;
            Check("recovery-restart-keeps-original-custody",trip?.Phase==VehicleTravelPhase.Recovering&&!testVehicle.Spawned&&testVehicle.CurJob?.loadID==liftVehicleJob);
            var source=vehicleStations[1].Position;
            vehicleStations[1].Destroy(DestroyMode.Vanish);
            if(UndergroundLift)foreach(var extra in traffic.Connectors.ToArray())extra.Destroy(DestroyMode.Vanish);
            Check("all-stations-removed-retains-map-owned-recovery-action",traffic.Connectors.Count==0&&traffic.RecoveryCount==1&&traffic.RecoveryOptions().Count>=2&&!testVehicle.Spawned);
            LayerView.Switch(owner,LiftLayer(1));Find.CameraDriver.SetRootPosAndSize(source.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-lift-no-stations.png");screenshotHold=3;
            vehiclePhase=21;ticks=0;return;
        }
        if(vehiclePhase==21)
        {
            recoveryBlocker.DeSpawn();
            vehicleStations[1]=(Building_VehicleConnector)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("RL_VehicleLift"),trip!.EntryCell,map);
            vehicleStations[1].SetFaction(Faction.OfPlayer);
            Check("recovery-command-accepts-cleared-supported-station",traffic.RecoverAt(vehicleStations[1]));
            vehiclePhase=13;ticks=0;return;
        }
        if(vehiclePhase==13)
        {
            if(!testVehicle.Spawned){if(ticks>300)throw new Exception("Broken destination did not recover original vehicle at source.");return;}
            Check("destroyed-destination-returns-original-to-source",testVehicle.Position==vehicleStations[1].Position&&traffic.Trip(testVehicle)==null);
            Check("cancelled-trip-ends-original-job",testVehicle.CurJob?.loadID!=liftVehicleJob&&VehicleNativeStopped());
            Check("damage-recovery-does-not-duplicate-cargo",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37&&map.mapPawns.AllPawnsSpawned.Count(p=>p.ThingID==testVehicle.ThingID)==1);
            LayerView.Switch(owner,LiftLayer(1));Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-lift-recovered.png");screenshotHold=3;
            vehiclePhase=UndergroundLift?30:99;ticks=0;return;
        }
        if(vehiclePhase==99&&ticks>30)Finish();
    }
    private static void RestoreVehicleLift()
    {
        if(UndergroundLift&&GenCommandLine.CommandLineArgPassed("rimlayersvehicleshaftresume")){RestoreShaftWork();return;}
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-lift-baseline.json")));
        var data=document.RootElement;var traffic=map.GetComponent<VehicleTraffic>();
        BindBoardIntegrity();
        testVehicle=traffic.GetDirectlyHeldThings().OfType<Pawn>().Single(p=>p.ThingID==data.GetProperty("vehicle").GetString());
        secondLiftVehicle=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("second").GetString());
        if(!GenCommandLine.CommandLineArgPassed("rimlayersvehiclerecovery"))Check("restart-rebuilds-opposing-queue",traffic.Trip(secondLiftVehicle)?.Phase==VehicleTravelPhase.Queued);
        testVehicleCargo=testVehicle.inventory.innerContainer.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        testVehicleCrew.Clear();
        testVehicleCrew.AddRange(((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>());
        vehicleStations=traffic.Connectors.Where(b=>!UndergroundLift||b.Position!=owner.AtElevation(-1,85,85)).OrderBy(b=>(UndergroundLift?-1:1)*owner.Elevation(owner.TryDecode(b.Position,out var l)?l.LayerId:0)).ToArray();
        if(GenCommandLine.CommandLineArgPassed("rimlayersvehiclerecovery"))
        {
            using var recovery=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-lift-recovery-baseline.json")));
            var saved=recovery.RootElement;
            recoveryBlocker=map.listerThings.AllThings.Single(t=>t.ThingID==saved.GetProperty("blocker").GetString());
            liftVehicleJob=saved.GetProperty("job").GetInt32();
            vehicleStations=new[]{null!,vehicleStations.Single()};
            vehiclePhase=20;stage=551;ticks=0;return;
        }
        pausedRemaining=data.GetProperty("remaining").GetInt32();liftVehicleJob=data.GetProperty("job").GetInt32();
        if(data.TryGetProperty("state",out var savedState))Check("restart-restores-held-fuel-and-component-damage",IntegrityMatches(savedState.EnumerateArray().Select(x=>x.GetSingle()).ToArray(),testVehicle));
        destination=LiftAt(1,82,92);
        Check("restart-restores-held-object-job-and-progress",traffic.Trip(testVehicle)?.Phase==VehicleTravelPhase.Held&&testVehicle.CurJob?.loadID==liftVehicleJob&&traffic.Trip(testVehicle)?.RemainingTicks==pausedRemaining);
        Check("restart-restores-original-passenger-identities",data.GetProperty("crew").EnumerateArray().Select(e=>e.GetString()).OrderBy(s=>s).SequenceEqual(testVehicleCrew.Select(p=>p.ThingID).OrderBy(s=>s)));
        vehiclePhase=10;stage=551;ticks=0;
    }
}
