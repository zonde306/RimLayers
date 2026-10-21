using System.Text.Json;
using RimLayers.Core;
using RimLayers.Vehicles;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void BeginVehicleFlat()
    {
        Check("ordinary-map-is-not-converted-for-vehicles",LayerMapComponent.For(map)==null&&map.Size.z==120);
        Check("basic-capability-is-independent-of-enhancement",VehicleCompatibility.Capabilities.HasFlag(VehicleCapabilities.GroundMovement)&&
            (!GenCommandLine.CommandLineArgPassed("rimlayersvehiclebasic")||VehicleCompatibility.Capabilities==VehicleCapabilities.GroundMovement));
        foreach(var c in new CellRect(40,40,31,21))
        {
            foreach(var t in c.GetThingList(map).ToArray())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(40,40,31,21).Contains(c));
        GenCommandLine.TryGetCommandLineArg("rimlayersvehicledef",out var requested);
        var def=DefDatabase<ThingDef>.GetNamed(requested??"RL_TestVehicle");
        var spawner=def.GetType().Assembly.GetType("Vehicles.VehicleSpawner",true)!;
        testVehicle=(Pawn)FrameworkVehicleAdapter.Method(spawner,"GenerateVehicle",def.thingClass,def.GetType(),typeof(Faction)).Invoke(null,new object[]{def,Faction.OfPlayer})!;
        GenSpawn.Spawn(testVehicle,new IntVec3(45,0,50),map,Rot4.East);
        pawn=map.mapPawns.FreeColonistsSpawned.First();
        if(def.defName!="RL_TestVehicle")
        {
            Check("flat-map-native-driver-boards",(bool)FrameworkVehicleAdapter.Method(testVehicle.GetType(),"TryAddPawn",typeof(bool),typeof(Pawn)).Invoke(testVehicle,new object[]{pawn})!);
            testVehicleCrew.Add(pawn);
            foreach(var fuel in testVehicle.AllComps.Where(c=>c.GetType().FullName=="Vehicles.CompFueledTravel"))FrameworkVehicleAdapter.Method(fuel.GetType(),"Refuel",typeof(void),typeof(float)).Invoke(fuel,new object[]{10000f});
        }
        var ignition=FrameworkVehicleAdapter.Field(testVehicle.GetType(),"ignition").GetValue(testVehicle)!;
        FrameworkVehicleAdapter.Property(ignition.GetType(),"Drafted").SetValue(ignition,true);
        testVehicleCargo=ThingMaker.MakeThing(ThingDefOf.Steel);testVehicleCargo.stackCount=37;testVehicle.inventory.innerContainer.TryAdd(testVehicleCargo,false);
        destination=new IntVec3(65,0,50);vehiclePhase=0;stage=553;ticks=0;
    }
    private static void VehicleFlatStep()
    {
        ticks++;
        if(vehiclePhase==0)
        {
            if(ticks<120)return;
            var job=JobMaker.MakeJob(JobDefOf.Goto,destination);expectedJobId=job.loadID;
            Check("flat-map-keeps-native-goto-entry",testVehicle.jobs.TryTakeOrderedJob(job));vehiclePhase=1;ticks=0;return;
        }
        if(vehiclePhase==1)
        {
            if(testVehicle.Position.x<54){if(ticks>3000)throw new Exception("Ordinary native vehicle path stalled: "+VehicleMotionTrace());return;}
            Check("flat-map-has-no-layer-transport-state",map.GetComponent<VehicleTraffic>().Trip(testVehicle)==null);
            File.WriteAllText(Path.Combine(directory,"vehicle-ground-baseline.json"),JsonSerializer.Serialize(new{vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray(),job=expectedJobId}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleGround");Finish();return;
        }
        if(testVehicle.Position!=destination||!VehicleNativeStopped()||testVehicle.CurJobDef==JobDefOf.Goto)
        {if(ticks>3000)throw new Exception("Ordinary native vehicle reload did not finish: "+VehicleMotionTrace());return;}
        Check("flat-map-native-job-finishes-after-restart",map.GetComponent<VehicleTraffic>().Trip(testVehicle)==null&&LayerMapComponent.For(map)==null);
        Check("flat-map-preserves-original-cargo",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37);
        Finish();
    }
    private static void RestoreVehicleFlat()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-ground-baseline.json")));var data=document.RootElement;
        testVehicle=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("vehicle").GetString());
        testVehicleCargo=testVehicle.inventory.innerContainer.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        destination=new IntVec3(65,0,50);
        Check("flat-map-restart-preserves-original-job",testVehicle.CurJob?.loadID==data.GetProperty("job").GetInt32()&&testVehicle.CurJob.targetA.Cell==destination);
        var crew=((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>();
        Check("flat-map-restart-preserves-driver-identity",data.GetProperty("crew").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(crew.Select(p=>p.ThingID)));
        vehiclePhase=2;stage=553;ticks=0;
    }
}
