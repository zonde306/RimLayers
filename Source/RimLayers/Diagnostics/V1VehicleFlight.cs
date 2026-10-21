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
    private static bool PlatformLoss=>GenCommandLine.CommandLineArgPassed("rimlayersvehicleplatformloss");
    private static bool ManualFlight=>GenCommandLine.CommandLineArgPassed("rimlayersvehiclemanual");
    private static System.Reflection.MethodInfo? airToggle,airInput;
    private static void ActivateManualFlight()
    {
        var a=SteelFlightAdapter.Installed!;var control=a.CompType.Assembly.GetType("VehicleFramework.LocalUpgrade.AircraftDirectControlUtility",true)!;
        airToggle=FrameworkVehicleAdapter.Method(control,"Toggle",typeof(void),a.CompType);
        airInput=FrameworkVehicleAdapter.Method(a.CompType,"SetDirectControlInput",typeof(void),typeof(UnityEngine.Vector2));
        Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
        if(!a.Manual(testVehicle))airToggle.Invoke(null,new object[]{a.Comp(testVehicle)!});
        Check("native-manual-flight-activated",a.Manual(testVehicle));
    }
    private static bool AirRouteFlight=>GenCommandLine.CommandLineArgPassed("rimlayersvehicleairroute");
    private static float[] flightBeforeImpact=Array.Empty<float>();
    private static bool CrashFlight=>GenCommandLine.CommandLineArgPassed("rimlayersvehiclecrash");
    private static int flightKillCount,flightImpactCount;
    private static double lastFlightHeight;
    private static int flightInitialLayers;
    private static bool flightSmooth=true;
    private static void FlightIgnition(bool drafted)
    {
        var ignition=FrameworkVehicleAdapter.Field(testVehicle.GetType(),"ignition").GetValue(testVehicle)!;
        FrameworkVehicleAdapter.Property(ignition.GetType(),"Drafted").SetValue(ignition,drafted);
    }
    private static void BeginVehicleFlight()
    {
        if(!WorldFlight&&SteelFlightAdapter.Installed==null)throw new Exception(VehicleCompatibility.EnhancementDiagnostic);
        if(!WorldFlight)Check("continuous-flight-capability-bound",VehicleCompatibility.Capabilities.HasFlag(VehicleCapabilities.ContinuousFlight));
        if(!WorldFlight)BindFlightImpactChecks();flightInitialLayers=owner.Layout!.LayerCount;
        foreach(var local in new CellRect(35,25,70,80))for(int layer=0;layer<owner.Layout.LayerCount;layer++)
        {
            var c=At(layer,local.x,local.z);
            foreach(var t in c.GetThingList(map).ToArray())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,layer==0?TerrainDefOf.Concrete:RimLayersDefOf.RL_OpenAir);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        GenCommandLine.TryGetCommandLineArg("rimlayersvehicledef",out var requested);
        var def=DefDatabase<ThingDef>.GetNamed(requested??"VVE_Mosquito");
        var spawner=def.GetType().Assembly.GetType("Vehicles.VehicleSpawner",true)!;
        testVehicle=(Pawn)FrameworkVehicleAdapter.Method(spawner,"GenerateVehicle",def.thingClass,def.GetType(),typeof(Faction)).Invoke(null,new object[]{def,Faction.OfPlayer})!;
        GenSpawn.Spawn(testVehicle,At(0,78,60),map,Rot4.North);
        pawn=map.mapPawns.FreeColonistsSpawned.First();
        var add=FrameworkVehicleAdapter.Method(testVehicle.GetType(),"TryAddPawn",typeof(bool),typeof(Pawn));
        Check("aircraft-pilot-boards-original-vehicle",(bool)add.Invoke(testVehicle,new object[]{pawn})!);testVehicleCrew.Add(pawn);
        var passenger=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        Check("aircraft-passenger-boards",(bool)add.Invoke(testVehicle,new object[]{passenger})!);testVehicleCrew.Add(passenger);
        foreach(var fuel in testVehicle.AllComps.Where(c=>c.GetType().FullName=="Vehicles.CompFueledTravel"))FrameworkVehicleAdapter.Method(fuel.GetType(),"Refuel",typeof(void),typeof(float)).Invoke(fuel,new object[]{10000f});
        // Gold cannot merge with the aircraft/wall steel salvage during the lethal native crash.
        testVehicleCargo=ThingMaker.MakeThing(ManualFlight||PlatformLoss?ThingDefOf.Gold:ThingDefOf.Steel);testVehicleCargo.stackCount=37;testVehicle.inventory.innerContainer.TryAdd(testVehicleCargo,false);
        if(WorldFlight){BeginVehicleWorld();return;}
        FlightIgnition(true);vehiclePhase=0;stage=554;ticks=0;lastFlightHeight=0;
        LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
        Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),12);
    }
    private static void VehicleFlightStep()
    {
        ticks++;if(WorldFlight){VehicleWorldStep();return;}var adapter=SteelFlightAdapter.Installed!;
        if(vehiclePhase==11||vehiclePhase==31){CheckFlightCrash();return;}
        if(vehiclePhase==12){if(ticks>60){Check("crash-remains-single-after-following-ticks",flightImpactCount==1&&flightKillCount<=1);Finish();}return;}
        if(ManualFlight&&vehiclePhase==30&&map.GetComponent<VehicleFlight>().State(testVehicle)?.ImpactCommitted==true){vehiclePhase=31;CheckFlightCrash();return;}
        if(!testVehicle.Spawned)throw new Exception("Flight vehicle left the map unexpectedly.");
        if(ManualFlight&&adapter.Manual(testVehicle))airInput!.Invoke(adapter.Comp(testVehicle),new object[]{vehiclePhase==30?new UnityEngine.Vector2(0,1):UnityEngine.Vector2.zero});
        double height=adapter.Height(testVehicle);var state=map.GetComponent<VehicleFlight>().Ensure(testVehicle);
        if(Math.Abs(height-lastFlightHeight)>1)flightSmooth=false;lastFlightHeight=height;
        if(ticks%120==0)Log.Message($"[RimLayers.FlightTest] phase={vehiclePhase} native={adapter.State(testVehicle)} height={height:F3} datum={state.Datum} proxy={state.ProxyLayer} cell={testVehicle.Position} guidance={state.Guidance?.Heading:F1} yaw={VehicleCompatibility.Adapter!.Heading(testVehicle):F1}");
        if(ticks>15000)throw new Exception("Flight phase timed out: "+vehiclePhase+" native="+adapter.State(testVehicle)+" height="+height);
        if(vehiclePhase==0)
        {
            if(ManualFlight)
            {
                if(adapter.State(testVehicle)!="Airborne")return;
                if(airToggle==null){ActivateManualFlight();adapter.SetTargetHeight(testVehicle,4.5);return;}
                if(height>5.8)return;
            }
            if(height<3.3)return;
            if(CrashFlight||AirRouteFlight){vehiclePhase=2;ticks=0;return;}
            Check(ManualFlight?"native-manual-descent-crosses-floor-without-jump":"native-climb-crosses-first-floor-without-jump",flightSmooth&&state.ProxyLayer==1&&state.Datum==0,$"height={height},proxy={state.ProxyLayer},datum={state.Datum}");
            Check(ManualFlight?"manual-flight-camera-follows-controlled-floor":"automatic-flight-does-not-switch-camera",owner.ViewedLayer==(ManualFlight?1:0));
            File.WriteAllText(Path.Combine(directory,"vehicle-flight-baseline.json"),JsonSerializer.Serialize(new{vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray(),height,datum=state.Datum,layer=state.ProxyLayer,layers=flightInitialLayers}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleFlight");
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),12);
            CheckFlightProjection();
            pendingScreenshot=Path.Combine(directory,"vehicle-flight-climb.png");screenshotHold=4;vehiclePhase=1;ticks=0;return;
        }
        if(vehiclePhase==1){if(ticks>10)Finish();return;}
        if(vehiclePhase==2)
        {
            if(adapter.State(testVehicle)!="Airborne"||height<9)return;
            Check("restart-native-climb-continues-without-frame-bounce",flightSmooth);
            Check("high-airspace-does-not-create-floors",owner.Layout!.LayerCount==flightInitialLayers&&state.ProxyLayer==VehicleFlight.ProxyAt(owner,height));
            Check("flight-keeps-original-cargo-and-crew",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37);
            // Build a supported landing deck below the aircraft after it has cleared it.
            owner.TryDecode(testVehicle.Position,out var at);
            for(int x=at.X-6;x<=at.X+6;x+=6)for(int z=at.Z-6;z<=at.Z+6;z+=6)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,x,z),map);
            foreach(var c in new CellRect(at.X-6,at.Z-6,13,13))
            {map.roofGrid.SetRoof(At(0,c.x,c.z),RoofDefOf.RoofConstructed);map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);}
            if((bool)FrameworkVehicleAdapter.Property(adapter.CompType,"UsesFixedWingDynamics").GetValue(adapter.Comp(testVehicle))!)
            {
                // A fixed wing cannot remain above a helicopter pad. Build a supported airport,
                // covering its unmodified native holding pattern and landing run.
                for(int x=8;x<113;x+=6)for(int z=8;z<113;z+=6)
                    if(At(0,x,z).GetEdifice(map)==null)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,x,z),map);
                foreach(var c in new CellRect(8,8,105,105))
                {
                    foreach(var t in At(1,c.x,c.z).GetThingList(map).ToArray())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
                    map.roofGrid.SetRoof(At(1,c.x,c.z),null);
                    map.roofGrid.SetRoof(At(0,c.x,c.z),RoofDefOf.RoofConstructed);map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);
                }
                map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
                Check("fixed-wing-keeps-native-forward-airspeed",(float)FrameworkVehicleAdapter.Property(adapter.CompType,"AirspeedCellsPerSecond").GetValue(adapter.Comp(testVehicle))!>1);
            }
            if(AirRouteFlight)
            {
                destination=At(1,96,82);
                for(int z=65;z<=82;z++)
                {
                    map.roofGrid.SetRoof(At(0,88,z),RoofDefOf.RoofConstructed);map.terrainGrid.SetTerrain(At(1,88,z),RimLayersDefOf.RL_StructuralFloor);
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(0,88,z),map);
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(1,88,z),map);
                }
                var job=JobMaker.MakeJob(JobDefOf.Goto,destination);expectedJobId=job.loadID;
                Check("native-cross-layer-flight-order-accepted",testVehicle.jobs.TryTakeOrderedJob(job));
                vehiclePhase=20;ticks=0;return;
            }
            if(CrashFlight)
            {
                foreach(var fuel in testVehicle.AllComps.Where(c=>c.GetType().FullName=="Vehicles.CompFueledTravel"))
                    FrameworkVehicleAdapter.Method(fuel.GetType(),"ConsumeFuel",typeof(void),typeof(float)).Invoke(fuel,new object[]{10000f});
                vehiclePhase=10;ticks=0;return;
            }
            adapter.SetTargetHeight(testVehicle,4.5);vehiclePhase=3;ticks=0;return;
        }
        if(vehiclePhase==3)
        {
            if(height>4.6)return;
            if(ManualFlight)
            {
                owner.TryDecode(testVehicle.Position,out var manualAt);
                foreach(var local in new CellRect(manualAt.X-9,manualAt.Z-9,19,19).EdgeCells)
                {
                    var c=At(1,local.x,local.z);map.terrainGrid.SetTerrain(c,RimLayersDefOf.RL_StructuralFloor);
                    map.roofGrid.SetRoof(At(0,local.x,local.z),RoofDefOf.RoofConstructed);
                    if(At(0,local.x,local.z).GetEdifice(map)==null)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(0,local.x,local.z),map);
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),c,map);
                }
                vehiclePhase=30;ticks=0;return;
            }
            Check("native-descent-selects-upper-storage-frame",state.ProxyLayer==1&&flightSmooth);
            FlightIgnition(false);vehiclePhase=4;ticks=0;return;
        }
        if(vehiclePhase==30)
        {
            if(state.ImpactCommitted){vehiclePhase=31;CheckFlightCrash();return;}
            if(ticks>1800)throw new Exception("Manual aircraft did not reach its dynamic wall.");
            return;
        }
        if(vehiclePhase==4)
        {
            if(adapter.State(testVehicle)!="Grounded")return;
            Check("native-touchdown-uses-upper-height-datum",state.ProxyLayer==1&&Math.Abs(adapter.Height(testVehicle)-3)<.001,$"height={adapter.Height(testVehicle)},datum={state.Datum}");
            Check("upper-touchdown-has-whole-airframe-surface",VehicleSpace.CanStand(owner,testVehicle,testVehicle.Position,VehicleCompatibility.Adapter!.Heading(testVehicle),out var reason),reason);
            Check("touchdown-keeps-original-cargo",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37);
            LayerView.Switch(owner,1);Find.Selector.Select(testVehicle);Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-flight-upper-landed.png");screenshotHold=4;vehiclePhase=5;ticks=0;return;
        }
        if(vehiclePhase==10)
        {
            if(adapter.State(testVehicle)!="Crashing"||ticks<20)return;
            Check("empty-tank-starts-native-failure-motion",height>3&&!state.ImpactCommitted&&state.Datum<0,$"height={height},datum={state.Datum}");
            File.WriteAllText(Path.Combine(directory,"vehicle-flight-baseline.json"),JsonSerializer.Serialize(new{vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray(),height,datum=state.Datum,layer=state.ProxyLayer,layers=flightInitialLayers,health=VehicleIntegrity(testVehicle)}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleFlight");Finish();return;
        }
        if(vehiclePhase==20)
        {
            if(ticks<90||!state.RouteActive)return;
            Check("air-route-retains-original-job-while-crossing",testVehicle.CurJob?.loadID==expectedJobId&&testVehicle.CurJob.targetA.Cell==destination);
            File.WriteAllText(Path.Combine(directory,"vehicle-flight-baseline.json"),JsonSerializer.Serialize(new{vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray(),height,datum=state.Datum,layer=state.ProxyLayer,layers=flightInitialLayers,job=expectedJobId}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleFlight");Finish();return;
        }
        if(vehiclePhase==21)
        {
            if(state.RouteActive||testVehicle.CurJobDef==JobDefOf.Goto)return;
            Check("automatic-air-route-reaches-original-floor-and-destination",state.ProxyLayer==1&&testVehicle.Position.DistanceToSquared(destination)<=4&&!state.ImpactCommitted,$"cell={testVehicle.Position},height={height}");
            Check("automatic-air-route-preserves-original-cargo",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37);
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),15);
            pendingScreenshot=Path.Combine(directory,"vehicle-flight-route.png");screenshotHold=4;vehiclePhase=5;ticks=0;return;
        }
        if(vehiclePhase==5&&ticks>10)Finish();
    }
    private static void RestoreVehicleFlight()
    {
        if(WorldFlight){RestoreVehicleWorld();return;}
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-flight-baseline.json")));var data=doc.RootElement;
        testVehicle=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("vehicle").GetString());
        testVehicleCargo=testVehicle.inventory.innerContainer.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        var state=map.GetComponent<VehicleFlight>().State(testVehicle)!;var adapter=SteelFlightAdapter.Installed!;
        Check("restart-restores-owned-flight-height-datum",state!=null&&state.Datum==data.GetProperty("datum").GetDouble()&&state.ProxyLayer==data.GetProperty("layer").GetInt32());
        Check("restart-restores-native-continuous-altitude",Math.Abs(adapter.Height(testVehicle)-data.GetProperty("height").GetDouble())<.001);
        var crew=((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>();
        Check("flight-restart-preserves-crew-identities",data.GetProperty("crew").EnumerateArray().Select(x=>x.GetString()).OrderBy(x=>x).SequenceEqual(crew.Select(p=>p.ThingID).OrderBy(x=>x)));
        testVehicleCrew.Clear();testVehicleCrew.AddRange(crew);
        BindFlightImpactChecks();flightInitialLayers=data.GetProperty("layers").GetInt32();lastFlightHeight=adapter.Height(testVehicle);vehiclePhase=CrashFlight?11:AirRouteFlight?21:2;stage=554;ticks=0;
        if(CrashFlight&&data.TryGetProperty("health",out var health))flightBeforeImpact=health.EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        if(PlatformLoss)
        {
            owner.TryDecode(testVehicle.Position,out var local);
            foreach(var c in new CellRect(local.X-12,local.Z-12,25,25))
            {
                map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_OpenAir);map.roofGrid.SetRoof(At(0,c.x,c.z),null);
                foreach(var t in At(0,c.x,c.z).GetThingList(map).ToArray())if(t.def==ThingDefOf.Column)t.Destroy(DestroyMode.Vanish);
            }
            map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        }
        if(ManualFlight){LayerView.Switch(owner,state!.ProxyLayer);ActivateManualFlight();adapter.SetTargetHeight(testVehicle,9.5);}
        if(AirRouteFlight){destination=At(1,96,82);expectedJobId=data.GetProperty("job").GetInt32();Check("restart-restores-final-air-route-job",testVehicle.CurJob?.loadID==expectedJobId&&state!.Destination.Cell==destination&&state.RouteActive,$"job={testVehicle.CurJob},savedJob={expectedJobId},active={state!.RouteActive},target={state.Destination}");}
    }
    private static void CheckFlightProjection()
    {
        var a=SteelFlightAdapter.Installed!;var comp=a.Comp(testVehicle)!;
        var p=a.CompType.Assembly.GetType("VehicleFramework.LocalUpgrade.AircraftPresentation",true)!;
        FrameworkVehicleAdapter.Method(p,"RefreshCamera",typeof(void)).Invoke(null,null);
        var center=(UnityEngine.Vector3)FrameworkVehicleAdapter.Method(p,"Center",typeof(UnityEngine.Vector3),a.CompType).Invoke(null,new object[]{comp})!;
        var click=(UnityEngine.Vector3)FrameworkVehicleAdapter.Method(p,"OnPlane",typeof(UnityEngine.Vector3),typeof(UnityEngine.Vector3),typeof(float)).Invoke(null,new object[]{center,0f})!;
        var hit=FrameworkVehicleAdapter.Method(p,"HitsBody",typeof(bool),a.CompType,typeof(UnityEngine.Vector3),typeof(float));
        Check("projected-airframe-mouse-hit-matches-visible-body",(bool)hit.Invoke(null,new object[]{comp,click,.5f})!);
        var other=click;other.z-=owner.Layout!.Stride;
        Check("mouse-hit-rejects-same-logical-point-on-other-floor",!(bool)hit.Invoke(null,new object[]{comp,other,.5f})!);
        var bounds=(UnityEngine.Rect)FrameworkVehicleAdapter.Method(p,"BodyBounds",typeof(UnityEngine.Rect),a.CompType,typeof(float)).Invoke(null,new object[]{comp,0f})!;
        Check("selection-bounds-use-same-floor-projection",bounds.Contains(new UnityEngine.Vector2(click.x,click.z)));
        var r=a.CompType.Assembly.GetType("VehicleFramework.LocalUpgrade.AircraftAltitudeRendering",true)!;
        var shadow=(UnityEngine.Vector3)FrameworkVehicleAdapter.Method(r,"ShadowCenter",typeof(UnityEngine.Vector3),a.CompType).Invoke(null,new object[]{comp})!;
        Check("shadow-center-stays-in-airframe-floor-band",owner.TryDecode(shadow.ToIntVec3(),out var at)&&at.LayerId==map.GetComponent<VehicleFlight>().State(testVehicle)!.ProxyLayer);
    }
    private static void BindFlightImpactChecks()
    {
        var basic=(FrameworkVehicleAdapter)VehicleCompatibility.Adapter!;
        var h=new HarmonyLib.Harmony("rimlayers.flight.test.impact");
        h.Patch(FrameworkVehicleAdapter.Method(basic.VehicleType,"Kill",typeof(void),typeof(DamageInfo?),typeof(DestroyMode),typeof(bool)),
            prefix:new HarmonyLib.HarmonyMethod(typeof(V1Integration),nameof(FlightKillObserved)));
        h.Patch(HarmonyLib.AccessTools.Method(typeof(SteelFlightAdapter),"CompleteImpact"),prefix:new HarmonyLib.HarmonyMethod(typeof(V1Integration),nameof(FlightImpactObserved)));
    }
    private static void FlightKillObserved(Pawn __instance){if(__instance==testVehicle)flightKillCount++;}
    private static void FlightImpactObserved(Pawn pawn){if(pawn==testVehicle)flightImpactCount++;}
    private static void CheckFlightCrash()
    {
        var state=map.GetComponent<VehicleFlight>().State(testVehicle)!;
        if(!state.ImpactCommitted){if(ticks>1500)throw new Exception("Native failure did not reach a real structure.");return;}
        if((vehiclePhase==11||vehiclePhase==31)&&flightImpactCount>0)
        {
            Check(ManualFlight?"manual-collision-hits-first-real-wall":PlatformLoss?"missing-platform-crash-reaches-real-ground":"crash-hits-upper-platform-not-imaginary-ground",(ManualFlight?state.ImpactHeight>4&&state.ImpactHeight<6:PlatformLoss?state.ImpactHeight>=-.1&&state.ImpactHeight<.25:state.ImpactHeight>=2.9&&state.ImpactHeight<3.25)&&state.ProxyLayer==(PlatformLoss?0:1),$"height={state.ImpactHeight},surface={state.ImpactCell},proxy={state.ProxyLayer}");
            if(ManualFlight)Check("manual-wall-impact-creates-one-native-wreck",flightKillCount==1);
            Check("native-crash-damage-committed-once",flightImpactCount==1&&flightKillCount<=1,$"impact={flightImpactCount},kill={flightKillCount}");
            var wrecks=map.listerThings.AllThings.Where(t=>t.GetType().FullName=="Vehicles.VehicleBuilding"&&ReferenceEquals(FrameworkVehicleAdapter.Field(t.GetType(),"vehicle").GetValue(t),testVehicle)).ToArray();
            Check("native-wreck-keeps-original-aircraft-once",flightKillCount==0?testVehicle.Spawned:wrecks.Length==1,$"wrecks={wrecks.Length},spawned={testVehicle.Spawned}");
            var cargo=map.listerThings.AllThings.Where(t=>t.ThingID==testVehicleCargo.ThingID).Concat(testVehicle.inventory.innerContainer.Where(t=>t.ThingID==testVehicleCargo.ThingID)).Distinct().ToArray();
            Check("crash-does-not-duplicate-original-cargo",cargo.Length==1&&cargo[0].stackCount==37,$"objects={cargo.Length},stack={cargo.FirstOrDefault()?.stackCount},allSteel={map.listerThings.AllThings.Where(t=>t.def==ThingDefOf.Steel).Sum(t=>t.stackCount)},inventory={testVehicle.inventory.innerContainer.TotalStackCount}");
            var aboard=((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>();
            var allCrew=map.mapPawns.AllPawnsSpawned.Concat(map.listerThings.AllThings.OfType<Corpse>().Select(c=>c.InnerPawn)).Concat(aboard).Distinct().ToArray();
            Check("native-crash-keeps-each-original-crew-object-once",testVehicleCrew.All(p=>allCrew.Count(other=>other.ThingID==p.ThingID)==1));
            if(flightBeforeImpact.Length>0)Check("crash-applies-real-native-component-damage",flightBeforeImpact.Zip(VehicleIntegrity(testVehicle)).Any(v=>v.Second<v.First-.01));
            owner.TryDecode(state.ImpactCell,out var impactLocal);
            LayerView.Switch(owner,state.ProxyLayer);Find.CameraDriver.SetRootPosAndSize(At(state.ProxyLayer,impactLocal.X,impactLocal.Z).ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-flight-upper-crash.png");screenshotHold=4;vehiclePhase=12;ticks=0;return;
        }
    }

}
