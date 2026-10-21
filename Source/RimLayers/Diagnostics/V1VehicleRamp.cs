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
    private static List<Building_VehicleConnector> rampModules=new();
    private static int rampJob;
    private static IntVec3 rampStart,rampGoal;
    private static Thing? rampRoadblock;
    private static void BeginVehicleRamp()
    {
        foreach(var c in new CellRect(75,73,25,43))
        {
            foreach(var t in c.GetThingList(map).ToArray())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(75,73,25,43).Contains(c));
        foreach(int x in new[]{80,90,96})foreach(int z in new[]{88,94,100,106,112})
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,x,z),map);
        foreach(var c in new CellRect(81,89,15,25))
        {
            map.roofGrid.SetRoof(At(0,c.x,c.z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);map.fogGrid.Unfog(At(1,c.x,c.z));
        }
        StartVehiclePreview("RL_VehicleRamp",At(0,84,85),"vehicle-ramp-preview-clear.png");
        vehiclePhase=-2;stage=552;ticks=0;
    }
    private static void BuildVehicleRampFixture()
    {
        var def=DefDatabase<ThingDef>.GetNamed("RL_VehicleRamp");
        int width=testVehicle.def.size.x;
        for(int i=0;i<width;i++)
        {
            var module=(Building_VehicleConnector)GenSpawn.Spawn(def,At(0,84+i,85),map,Rot4.North);
            module.SetFaction(Faction.OfPlayer);rampModules.Add(module);
        }
        VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(testVehicle,At(0,84,76),0));
        rampStart=testVehicle.Position;rampGoal=At(1,84,108);destination=rampGoal;
        if(!GenCommandLine.CommandLineArgPassed("rimlayersvehiclemanual"))
        {
            var generator=testVehicle.GetType().Assembly.GetType("Vehicles.VehicleSpawner",true)!;
            secondLiftVehicle=(Pawn)FrameworkVehicleAdapter.Method(generator,"GenerateVehicle",testVehicle.GetType(),testVehicle.def.GetType(),typeof(Faction)).Invoke(null,new object[]{testVehicle.def,Faction.OfPlayer})!;
            GenSpawn.Spawn(secondLiftVehicle,At(1,91,98),map,Rot4.South);
            if(testVehicle.def.defName!="RL_TestVehicle")
            {
                var driver=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
                if(!(bool)FrameworkVehicleAdapter.Method(secondLiftVehicle.GetType(),"TryAddPawn",typeof(bool),typeof(Pawn)).Invoke(secondLiftVehicle,new object[]{driver})!)throw new Exception("Second ramp driver could not board.");
                foreach(var fuel in secondLiftVehicle.AllComps.Where(c=>c.GetType().FullName=="Vehicles.CompFueledTravel"))FrameworkVehicleAdapter.Method(fuel.GetType(),"Refuel",typeof(void),typeof(float)).Invoke(fuel,new object[]{10000f});
            }
            var ignition=FrameworkVehicleAdapter.Field(secondLiftVehicle.GetType(),"ignition").GetValue(secondLiftVehicle)!;
            FrameworkVehicleAdapter.Property(ignition.GetType(),"Drafted").SetValue(ignition,true);
            VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(secondLiftVehicle,At(1,91,98),180));
        }
        vehiclePhase=0;stage=552;ticks=0;
    }
    private static void VehicleRampStep()
    {
        ticks++;var traffic=map.GetComponent<VehicleTraffic>();var trip=traffic.Trip(testVehicle);
        if(vehiclePhase==-2)
        {
            StartVehiclePreview("RL_VehicleRamp",At(0,84,88),"vehicle-ramp-preview-blocked.png");vehiclePhase=-1;return;
        }
        if(vehiclePhase==-1){vehiclePreviewDef=null;BuildVehicleRampFixture();return;}
        if(GenCommandLine.CommandLineArgPassed("rimlayersvehiclemanual")){VehicleManualRampStep();return;}
        if(vehiclePhase==0)
        {
            if(ticks<120)return;
            var edges=VehicleRoutePlanner.Edges(traffic,testVehicle).Where(e=>!e.Entry.IsLift).ToArray();
            Check("exact-width-modules-form-two-directed-ramp-edges",edges.Length==2,"edges="+edges.Length);
            if(edges.Length!=2)throw new Exception("Ramp geometry did not produce a bidirectional route.");
            Check("native-route-crosses-ramp-and-retains-final-goal",VehicleRoutePlanner.FirstCrossing(traffic,testVehicle,destination,PathEndMode.OnCell)!=null);
            var job=JobMaker.MakeJob(JobDefOf.Goto,destination);rampJob=job.loadID;
            Check("ramp-order-accepted",testVehicle.jobs.TryTakeOrderedJob(job));
            Check("opposing-ramp-order-accepted",secondLiftVehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,At(0,91,76))));
            vehiclePhase=1;ticks=0;return;
        }
        if(vehiclePhase==1)
        {
            if(trip?.Phase!=VehicleTravelPhase.Crossing||trip.RemainingTicks>trip.TotalTicks/2)
            {if(ticks>2400)throw new Exception("Ramp did not reach its moving midpoint. "+trip?.Phase+" "+VehicleMotionTrace());return;}
            var body=VehicleRampService.Body(owner,trip,1-(double)trip.RemainingTicks/trip.TotalTicks);
            Check("ramp-moves-through-intermediate-logical-cells",testVehicle.Position!=trip.EntryCell&&testVehicle.Position!=trip.ExitCell);
            Check("ramp-rigid-body-has-real-intermediate-height-and-pitch",body.Bottom.Height>0&&body.Bottom.Height<3&&Math.Abs(body.Pitch)>.1,$"height={body.Bottom.Height},pitch={body.Pitch}");
            Check("ramp-intermediate-travel-preserves-original-job",testVehicle.CurJob?.loadID==rampJob&&trip.Destination.Cell==destination);
            File.WriteAllText(Path.Combine(directory,"vehicle-ramp-baseline.json"),JsonSerializer.Serialize(new{
                second=secondLiftVehicle.ThingID,vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray(),job=rampJob,remaining=trip.RemainingTicks}));
            Check("opposing-ramp-vehicle-waits-outside-grade",traffic.Trip(secondLiftVehicle)?.Phase==VehicleTravelPhase.Queued&&secondLiftVehicle.Position==At(1,91,98));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleRamp");
            LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-ramp-midpoint.png");screenshotHold=3;vehiclePhase=99;ticks=0;return;
        }
        if(vehiclePhase==10)
        {
            if(testVehicle.Position!=destination||!VehicleNativeStopped()||testVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>2400)throw new Exception("Reloaded ramp journey did not finish. "+trip?.Phase+" "+VehicleMotionTrace());return;}
            Check("restart-finishes-ramp-and-final-native-leg",trip==null);
            Check("ramp-arrival-preserves-cargo-and-crew",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37&&testVehicleCrew.All(p=>p.MapHeld==map&&!p.Spawned));
            vehiclePhase=14;ticks=0;return;
        }
        if(vehiclePhase==14)
        {
            if(secondLiftVehicle.Position!=At(0,91,76)||secondLiftVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>6000)throw new Exception("Opposing ramp queue did not complete. "+traffic.Trip(secondLiftVehicle)?.Phase+" "+VehicleMotionTrace(secondLiftVehicle));return;}
            Check("opposing-ramp-queue-completes-after-first-exit",traffic.Trip(secondLiftVehicle)==null);
            destination=At(0,84,76);
            var reverse=JobMaker.MakeJob(JobDefOf.Goto,destination);rampJob=reverse.loadID;
            Check("reverse-ramp-order-accepted",testVehicle.jobs.TryTakeOrderedJob(reverse));
            vehiclePhase=11;ticks=0;return;
        }
        if(vehiclePhase==11)
        {
            if(GenCommandLine.CommandLineArgPassed("rimlayersvehicleobstruction"))
            {
                var pather=FrameworkVehicleAdapter.Field(testVehicle.GetType(),"vehiclePather").GetValue(testVehicle)!;
                if(rampRoadblock==null&&trip?.Phase==VehicleTravelPhase.Approaching&&owner.TryDecode(testVehicle.Position,out var here)&&here.Z>=103&&
                    FrameworkVehicleAdapter.Field(pather.GetType(),"curPath").GetValue(pather)!=null)
                {
                    rampRoadblock=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(1,84,98),map);
                    Check("dynamic-roadblock-inserted-after-native-path",testVehicle.Spawned&&testVehicle.CurJob?.loadID==rampJob);
                }
                if(rampRoadblock?.Spawned==true&&VehicleCompatibility.Adapter!.OccupiedCells(testVehicle,testVehicle.Position,VehicleCompatibility.Adapter.Heading(testVehicle)).Contains(rampRoadblock.Position))
                    throw new Exception("Vehicle footprint entered the dynamic wall.");
                if(trip!=null&&testVehicle.CurJob?.loadID!=rampJob)throw new Exception("Dynamic detour lost its original vehicle job.");
            }
            if(testVehicle.Position!=destination||!VehicleNativeStopped()||testVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>2400)throw new Exception("Downward ramp journey did not finish. "+trip?.Phase+" "+VehicleMotionTrace());return;}
            Check("bidirectional-ramp-completes-with-original-vehicle",trip==null&&testVehicle.Spawned);
            if(GenCommandLine.CommandLineArgPassed("rimlayersvehicleobstruction"))
            {
                Check("dynamic-roadblock-replans-and-completes-original-goal",rampRoadblock?.Spawned==true);
                rampRoadblock?.Destroy(DestroyMode.Vanish);
            }
            destination=At(1,84,108);
            testVehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination));
            vehiclePhase=12;ticks=0;return;
        }
        if(vehiclePhase==12)
        {
            if(trip?.Phase!=VehicleTravelPhase.Crossing||trip.RemainingTicks>trip.TotalTicks*3/4)
            {if(ticks>2400)throw new Exception("Damage scenario did not enter ramp.");return;}
            rampStart=trip.EntryCell;rampModules[0].Destroy(DestroyMode.Vanish);
            vehiclePhase=13;ticks=0;return;
        }
        if(vehiclePhase==13)
        {
            if(trip!=null){if(ticks>400)throw new Exception("Broken ramp did not recover its original vehicle.");return;}
            Check("broken-module-recovers-original-at-legal-source",testVehicle.Spawned&&testVehicle.Position==rampStart&&VehicleNativeStopped());
            Check("broken-module-disconnects-ramp-without-duplication",VehicleRoutePlanner.Edges(traffic,testVehicle).All(e=>e.Entry.IsLift)&&testVehicle.inventory.innerContainer.Contains(testVehicleCargo));
            vehiclePhase=99;ticks=0;return;
        }
        if(vehiclePhase==99&&ticks>30)Finish();
    }
    private static void RestoreVehicleRamp()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-ramp-baseline.json")));var data=document.RootElement;
        testVehicle=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("vehicle").GetString());
        testVehicleCargo=testVehicle.inventory.innerContainer.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        testVehicleCrew.Clear();testVehicleCrew.AddRange(((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>());
        if(data.TryGetProperty("second",out var second))
        {
            secondLiftVehicle=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==second.GetString());
            Check("restart-restores-opposing-ramp-queue",map.GetComponent<VehicleTraffic>().Trip(secondLiftVehicle)?.Phase==VehicleTravelPhase.Queued);
        }
        rampModules=map.GetComponent<VehicleTraffic>().Connectors.Where(b=>!b.IsLift).OrderBy(b=>b.Position.x).ToList();
        var trip=map.GetComponent<VehicleTraffic>().Trip(testVehicle);
        Check("restart-restores-ramp-phase-progress-and-original-job",trip?.Phase==VehicleTravelPhase.Crossing&&trip.RemainingTicks==data.GetProperty("remaining").GetInt32()&&testVehicle.CurJob?.loadID==data.GetProperty("job").GetInt32());
        Check("restart-restores-ramp-passengers",data.GetProperty("crew").EnumerateArray().Select(e=>e.GetString()).OrderBy(s=>s).SequenceEqual(testVehicleCrew.Select(p=>p.ThingID).OrderBy(s=>s)));
        destination=At(1,84,108);vehiclePhase=10;stage=552;ticks=0;
        if(GenCommandLine.CommandLineArgPassed("rimlayersvehiclemanual"))RestoreManualRamp();
    }
}
