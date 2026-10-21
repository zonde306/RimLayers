using System.Reflection;
using System.Text.Json;
using RimLayers.Core;
using RimLayers.Presentation;
using RimLayers.Vehicles;
using RimWorld;
using RimWorld.Planet;
using Verse;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static bool WorldFlight=>GenCommandLine.CommandLineArgPassed("rimlayersvehicleworld");
    private static Building? worldBlocker;
    private static object? NativeWorldHolder()=>Find.WorldObjects.AllWorldObjects.FirstOrDefault(w=>w.GetType().FullName=="Vehicles.World.AerialVehicleInFlight"&&ReferenceEquals(FrameworkVehicleAdapter.Field(w.GetType(),"vehicle").GetValue(w),testVehicle));
    private static void BeginVehicleWorld()
    {
        Check("base-world-capability-bound",VehicleCompatibility.Capabilities.HasFlag(VehicleCapabilities.WorldTravel));
        for(int x=66;x<=102;x+=6)for(int z=48;z<=96;z+=6)
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,x,z),map);
        foreach(var c in new CellRect(66,48,37,49))
        {map.roofGrid.SetRoof(At(0,c.x,c.z),RoofDefOf.RoofConstructed);map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);}
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(testVehicle,At(1,78,60),0));
        Check("upper-world-takeoff-whole-airframe-and-corridor-clear",FrameworkWorldAdapter.Installed!.Clear(testVehicle,map,testVehicle.Position,testVehicle.Rotation,out var reason,landing:false),reason);
        destination=At(1,95,83);
        var type=testVehicle.GetType().Assembly.GetType("Vehicles.World.ArrivalAction_LandToCell",true)!;
        var action=type.GetConstructor(new[]{testVehicle.GetType(),typeof(MapParent),typeof(IntVec3),typeof(Rot4)})!.Invoke(new object[]{testVehicle,map.Parent,destination,Rot4.North});
        var launcher=FrameworkVehicleAdapter.Property(testVehicle.GetType(),"CompVehicleLauncher").GetValue(testVehicle)!;
        var launch=launcher.GetType().GetMethods().Single(m=>m.Name=="Launch"&&m.GetParameters().Length==2);
        var data=Activator.CreateInstance(launch.GetParameters()[0].ParameterType)!;
        FrameworkVehicleAdapter.Field(data.GetType(),"targets").SetValue(data,new List<GlobalTargetInfo>{new(map.Parent)});
        launch.Invoke(launcher,new[]{data,action});
        var state=VehicleWorldTravel.Current.State(testVehicle)!;
        Check("world-departure-saves-upper-origin-and-logical-destination",state.OriginLayer==1&&state.OriginX==78&&state.Layer==1&&state.X==95&&state.Z==83);
        worldBlocker=(Building)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),destination,map);
        stage=554;vehiclePhase=100;ticks=0;
    }
    private static void SaveWorldCheckpoint()
    {
        File.WriteAllText(Path.Combine(directory,"vehicle-flight-baseline.json"),JsonSerializer.Serialize(new{vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray()}));
        GameDataSaveLoader.SaveGame("RimLayers_VehicleFlight");
    }
    private static void VehicleWorldStep()
    {
        var state=VehicleWorldTravel.Current.State(testVehicle)!;var a=FrameworkWorldAdapter.Installed!;
        if(ticks%600==0)Log.Message($"[RimLayers.WorldTest] phase={vehiclePhase} waiting={state.Waiting} pawnSpawned={testVehicle.Spawned} holder={testVehicle.ParentHolder?.GetType().FullName} sky={state.Skyfaller}");
        if(ticks>15000)throw new Exception("World arrival phase timed out: "+vehiclePhase);
        if(vehiclePhase==100)
        {
            if(!state.Waiting)return;
            var holder=NativeWorldHolder();
            Check("invalid-arrival-retains-original-native-world-holder",holder!=null&&!testVehicle.Spawned&&state.Skyfaller==null);
            Check("invalid-upper-target-does-not-fallback-to-surface",state.Layer==1&&state.X==95&&state.Z==83);
            Check("held-world-aircraft-offers-reselect-command",((IEnumerable<Gizmo>)FrameworkVehicleAdapter.Method(holder!.GetType(),"GetGizmos",typeof(IEnumerable<Gizmo>)).Invoke(holder,null)!).OfType<Command_Action>().Any(g=>g.defaultLabel=="RL_VehicleChooseLanding".Translate()));
            SaveWorldCheckpoint();Finish();return;
        }
        if(vehiclePhase==101)
        {
            if(ticks<60)return;
            Check("world-hold-remains-stable-after-restart",state.Waiting&&NativeWorldHolder()!=null&&!testVehicle.Spawned);
            Check("reselect-rejects-still-obstructed-upper-target",!a.Retry(state,At(1,95,83),Rot4.North)&&state.Waiting);
            a.ChooseAgain(state);
            var targetType=testVehicle.GetType().Assembly.GetType("Vehicles.LandingTargeter",true)!;
            var targeter=FrameworkVehicleAdapter.Property(targetType,"Instance").GetValue(null)!;
            Check("world-recovery-opens-native-upper-floor-targeter",(bool)FrameworkVehicleAdapter.Property(targetType,"IsTargeting").GetValue(targeter)!&&owner.ViewedLayer==1&&Current.Game.CurrentMap==map);
            var positionState=targetType.GetMethod("GetPosState")!;
            Check("native-targeter-rejects-blocked-whole-airframe",positionState.Invoke(targeter,new object[]{new LocalTargetInfo(At(1,95,83)),false})!.ToString()=="Invalid");
            Check("native-targeter-accepts-clear-upper-landing",positionState.Invoke(targeter,new object[]{new LocalTargetInfo(At(1,90,78)),false})!.ToString()=="Valid");
            var action=(Action<LocalTargetInfo,Rot4>)FrameworkVehicleAdapter.Field(targetType,"action").GetValue(targeter)!;
            action(new LocalTargetInfo(At(1,90,78)),Rot4.North);
            FrameworkVehicleAdapter.Method(targetType,"StopTargeting",typeof(void)).Invoke(targeter,null);
            Check("native-targeter-action-enters-original-arrival-holder",state.Skyfaller is {Spawned:true}&&NativeWorldHolder()==null);
            worldBlocker=(Building)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(1,90,78),map);
            vehiclePhase=102;ticks=0;return;
        }
        if(vehiclePhase==102)
        {
            if(!state.Waiting)return;
            Check("touchdown-rechecks-dynamic-obstacle-without-wiping",state.Skyfaller is {Spawned:true}&&!testVehicle.Spawned&&worldBlocker is {Spawned:true});
            Check("map-has-recovery-entry-for-arriving-original-aircraft",VehicleWorldTravel.Current.HasWaiting(map));
            SaveWorldCheckpoint();LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,90,78).ToVector3Shifted(),15);
            pendingScreenshot=Path.Combine(directory,"vehicle-world-held-arrival.png");screenshotHold=4;vehiclePhase=109;ticks=0;return;
        }
        if(vehiclePhase==103)
        {
            if(ticks<60)return;
            Check("skyfaller-hold-remains-stable-after-second-restart",state.Waiting&&state.Skyfaller is {Spawned:true}&&!testVehicle.Spawned);
            Check("skyfaller-reselection-keeps-original-holder",a.Retry(state,At(1,84,84),Rot4.North)&&state.Skyfaller is {Spawned:true});
            vehiclePhase=104;ticks=0;return;
        }
        if(vehiclePhase==104)
        {
            if(!testVehicle.Spawned)return;
            Check("world-return-lands-on-specified-upper-floor",testVehicle.Position==At(1,84,84)&&state.Skyfaller==null&&!state.Waiting);
            Check("world-return-keeps-original-cargo",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37);
            var crew=((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>();
            Check("world-return-keeps-original-crew",testVehicleCrew.Select(p=>p.ThingID).OrderBy(s=>s).SequenceEqual(crew.Select(p=>p.ThingID).OrderBy(s=>s)));
            Check("world-return-has-no-duplicate-native-holder",NativeWorldHolder()==null&&map.listerThings.AllThings.Count(t=>t.ThingID==testVehicle.ThingID)==1);
            LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-world-return.png");screenshotHold=4;vehiclePhase=109;ticks=0;return;
        }
        if(vehiclePhase==109&&ticks>10)Finish();
    }
    private static void RestoreVehicleWorld()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-flight-baseline.json")));var data=doc.RootElement;
        var id=data.GetProperty("vehicle").GetString();
        var state=Current.Game.GetComponent<VehicleWorldTravel>();
        var waiting=state.WaitingFor(map).Single(s=>s.Vehicle.ThingID==id);testVehicle=waiting.Vehicle;
        testVehicleCargo=testVehicle.inventory.innerContainer.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        testVehicleCrew.Clear();testVehicleCrew.AddRange(((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>());
        Check("world-save-restores-original-crew-identities",data.GetProperty("crew").EnumerateArray().Select(x=>x.GetString()).OrderBy(s=>s).SequenceEqual(testVehicleCrew.Select(p=>p.ThingID).OrderBy(s=>s)));
        Check("world-save-restores-logical-floor-not-storage-target",waiting.Layer==1&&waiting.OriginLayer==1);
        vehiclePhase=GenCommandLine.CommandLineArgPassed("rimlayersvehiclerecovery")?103:101;stage=554;ticks=0;
    }
}
