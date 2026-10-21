using System.Text.Json;
using RimLayers.Vehicles;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn testVehicle=null!;
    private static Thing testVehicleCargo=null!;
    private static int vehiclePhase;
    private static readonly List<Pawn> testVehicleCrew=new();
    private static void BeginVehicleGround()
    {
        var adapter=VehicleCompatibility.Adapter??throw new InvalidOperationException(VehicleCompatibility.Diagnostic);
        var surfaceProbe=At(0,54,58);var originalTerrain=map.terrainGrid.TerrainAt(surfaceProbe);
        map.terrainGrid.SetTerrain(surfaceProbe,DefDatabase<TerrainDef>.GetNamed("WaterShallow"));
        Check("vehicle-facilities-do-not-create-water-foundations",!VehicleFacilitySpace.NativeSurface(owner,surfaceProbe));
        map.terrainGrid.SetTerrain(surfaceProbe,originalTerrain);
        var provider=LoadedModManager.RunningModsListForReading.Single(m=>m.PackageId.Equals("baal.vehicleframework",StringComparison.OrdinalIgnoreCase));
        var patherType=provider.assemblies.loadedAssemblies.Single(a=>a.GetName().Name=="Vehicles").GetType("Vehicles.VehiclePathFollower",true)!;
        new HarmonyLib.Harmony("rimlayers.vehicles.fixture").Patch(FrameworkVehicleAdapter.Method(patherType,"PatherFailed",typeof(void)),prefix:new HarmonyLib.HarmonyMethod(typeof(V1Integration),nameof(VehiclePatherFailed)));
        GenCommandLine.TryGetCommandLineArg("rimlayersvehicledef",out var requestedDef);
        var def=DefDatabase<ThingDef>.GetNamed(requestedDef??"RL_TestVehicle");
        var type=def.GetType().Assembly.GetType("Vehicles.VehicleSpawner",true)!;
        var generate=FrameworkVehicleAdapter.Method(type,"GenerateVehicle",def.thingClass,def.GetType(),typeof(Faction));
        testVehicle=(Pawn)generate.Invoke(null,new object[]{def,Faction.OfPlayer})!;
        if(testVehicle==null)throw new Exception("Native VehicleSpawner returned null.");
        GenSpawn.Spawn(testVehicle,At(0,54,58),map,Rot4.North);
        if(def.defName!="RL_TestVehicle")
        {
            var add=FrameworkVehicleAdapter.Method(testVehicle.GetType(),"TryAddPawn",typeof(bool),typeof(Pawn));
            Check("native-driver-boarded",(bool)add.Invoke(testVehicle,new object[]{pawn})!);
            testVehicleCrew.Add(pawn);
            var passenger=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
            if(!(bool)add.Invoke(testVehicle,new object[]{passenger})!)throw new Exception("Fixture vehicle cannot take its test passenger.");
            testVehicleCrew.Add(passenger);
            foreach(var comp in testVehicle.AllComps.Where(c=>c.GetType().FullName=="Vehicles.CompFueledTravel"))
                FrameworkVehicleAdapter.Method(comp.GetType(),"Refuel",typeof(void),typeof(float)).Invoke(comp,new object[]{10000f});
        }
        var ignition=FrameworkVehicleAdapter.Field(testVehicle.GetType(),"ignition").GetValue(testVehicle)!;
        FrameworkVehicleAdapter.Property(ignition.GetType(),"Drafted").SetValue(ignition,true);
        testVehicleCargo=ThingMaker.MakeThing(ThingDefOf.Steel);testVehicleCargo.stackCount=37;
        Check("native-vehicle-generated-with-real-cargo",adapter.IsVehicle(testVehicle)&&testVehicle.inventory.innerContainer.TryAdd(testVehicleCargo,false));
        Check("native-ground-permission",adapter.CanMove(testVehicle));
        if(scenario=="vehicle-ramp"){BeginVehicleRamp();return;}
        if(scenario=="vehicle-lift"){BeginVehicleLift();return;}
        foreach(var heading in new[]{0d,45d,90d,180d,270d})
        {
            adapter.SetHeading(testVehicle,heading);
            Check("native-heading-"+heading,Math.Abs(VehicleGeometry.SignedTurn(adapter.Heading(testVehicle),heading))<.01);
            Check("native-footprint-"+heading,adapter.OccupiedCells(testVehicle,testVehicle.Position,heading).Any());
            var center=(UnityEngine.Vector3)FrameworkVehicleAdapter.Method(testVehicle.GetType(),"TrueCenter",typeof(UnityEngine.Vector3)).Invoke(testVehicle,null)!;
            var logical=VehicleSpace.Pose(owner,testVehicle,testVehicle.Position,heading);
            Check("native-hull-anchor-"+heading,Math.Abs(center.x-logical.Position.X)<.001&&Math.Abs(center.z-logical.Position.Z)<.001);
        }
        adapter.Commit(testVehicle,At(0,54,58),0);
        Check("turn-clearance-open-apron",VehicleSpace.CanTurn(owner,testVehicle,testVehicle.Position,0,90));
        if(def.defName=="RL_TestVehicle")
        {
        var turnBlocker=ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel);
        GenSpawn.Spawn(turnBlocker,At(0,56,58),map);
        Check("turn-blocker-is-outside-endpoint-footprints",VehicleSpace.CanStand(owner,testVehicle,testVehicle.Position,0,out _)&&VehicleSpace.CanStand(owner,testVehicle,testVehicle.Position,90,out _));
        Check("swept-turn-detects-intermediate-corner",!VehicleSpace.CanTurn(owner,testVehicle,testVehicle.Position,0,90));
        turnBlocker.Destroy(DestroyMode.Vanish);
        }
        destination=At(0,54,61);
        var route=adapter.FindLeg(testVehicle,testVehicle.Position,destination,PathEndMode.OnCell);
        Check("native-same-layer-route",route is {Count:>0}&&route.Last()==destination);
        var upper=adapter.FindLeg(testVehicle,At(1,54,58),At(1,54,61),PathEndMode.OnCell);
        Check("hypothetical-upper-leg-does-not-move-real-vehicle",upper is {Count:>0}&&testVehicle.Position==At(0,54,58));
        // Native region updates are deferred after the wall was removed. Observe readiness rather than
        // invoking Goto against the intentionally dirty graph in the same fixture-construction frame.
        vehiclePhase=-1;stage=550;ticks=0;
    }
    private static void VehicleGroundStep()
    {
        ticks++;
        if(vehiclePhase==-1)
        {
            var utility=testVehicle.GetType().Assembly.GetType("Vehicles.VehicleReachabilityUtility",true)!;
            var reach=FrameworkVehicleAdapter.Method(utility,"CanReachVehicle",typeof(bool),testVehicle.GetType(),typeof(LocalTargetInfo),typeof(PathEndMode),typeof(Danger),typeof(TraverseMode));
            bool reachable=false;
            var assembly=testVehicle.GetType().Assembly;
            // The framework rebuilds regions on its own thread. Do not query room caches mid-rebuild.
            using((IDisposable)Activator.CreateInstance(assembly.GetType("Vehicles.ThreadDisabler",true)!)!)
            {
                var system=map.components.Single(c=>c.GetType().FullName=="Vehicles.VehiclePathingSystem");
                var data=FrameworkVehicleAdapter.Property(system.GetType(),"Item").GetValue(system,new object[]{testVehicle.def})!;
                var updater=FrameworkVehicleAdapter.Property(data.GetType(),"VehicleRegionAndRoomUpdater").GetValue(data)!;
                bool initialized=(bool)FrameworkVehicleAdapter.Property(updater.GetType(),"Initialized").GetValue(updater)!;
                bool dirty=(bool)FrameworkVehicleAdapter.Property(updater.GetType(),"AnythingToRebuild").GetValue(updater)!;
                if(initialized&&!dirty)
                    reachable=(bool)reach.Invoke(null,new object[]{testVehicle,new LocalTargetInfo(destination),PathEndMode.OnCell,Danger.Deadly,TraverseMode.ByPawn})!;
            }
            if(!reachable){if(ticks>300)throw new Exception("Native region graph did not settle after fixture wall removal.");return;}
            Check("native-region-graph-ready-after-fixture-edit",true,$"ticks={ticks}");
            Check("native-ordered-goto-accepted",testVehicle.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination)));
            vehiclePhase=0;ticks=0;return;
        }
        if(vehiclePhase==0)
        {
            var nativePather=FrameworkVehicleAdapter.Field(testVehicle.GetType(),"vehiclePather").GetValue(testVehicle)!;
            bool moving=(bool)FrameworkVehicleAdapter.Property(nativePather.GetType(),"Moving").GetValue(nativePather)!;
            if(testVehicle.Position!=destination||moving||testVehicle.CurJobDef==JobDefOf.Goto)
            {if(ticks>1800)throw new Exception("Native vehicle leg did not arrive: "+testVehicle.Position+" job="+testVehicle.CurJob+" canMove="+VehicleCompatibility.Adapter!.CanMove(testVehicle)+" "+VehicleMotionTrace());return;}
            Check("native-pather-completes-layer-leg",true);
            var adapter=VehicleCompatibility.Adapter!;
            Check("upper-landing-has-whole-vehicle-clearance",VehicleSpace.CanStand(owner,testVehicle,At(1,54,60),0,out var reason),reason);
            if(!VehicleSpace.CanStand(owner,testVehicle,At(1,54,60),0,out _))throw new Exception("Invalid fixture upper landing.");
            var identity=testVehicle;
            VehicleTraffic.Command(()=>adapter.Commit(testVehicle,At(1,54,60),0));
            Check("cross-band-commit-retains-original-vehicle-and-cargo",ReferenceEquals(identity,testVehicle)&&testVehicle.Spawned&&testVehicle.Position==At(1,54,60)&&testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37);
            Check("cross-band-commit-retains-crew",testVehicleCrew.All(p=>p.ParentHolder!=null&&p.MapHeld==map&&!p.Spawned));
            File.WriteAllText(Path.Combine(directory,"vehicle-ground-baseline.json"),JsonSerializer.Serialize(new{vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray()}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleGround");
            LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            Find.CameraDriver.SetRootPosAndSize(At(1,54,60).ToVector3Shifted(),9);
            pendingScreenshot=Path.Combine(directory,"vehicle-ground.png");screenshotHold=3;
            vehiclePhase=1;ticks=0;return;
        }
        if(ticks>30)Finish();
    }
    private static string VehicleMotionTrace(Pawn? actor=null)
    {
        actor??=testVehicle;
        var pather=FrameworkVehicleAdapter.Field(actor.GetType(),"vehiclePather").GetValue(actor)!;
        var targets=new List<object>{pather};
        targets.AddRange(actor.AllComps.Where(c=>c.GetType().FullName=="VehicleFramework.LocalUpgrade.CompVehicleGroundMotion"));
        return string.Join("; ",targets.SelectMany(o=>o.GetType().GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public)
            .Where(f=>f.FieldType.IsPrimitive||f.FieldType==typeof(IntVec3)||f.FieldType==typeof(UnityEngine.Vector3))
            .Select(f=>o.GetType().Name+"."+f.Name+"="+f.GetValue(o))));
    }
    private static void VehiclePatherFailed(object __instance)
    {
        var field=FrameworkVehicleAdapter.Field(__instance.GetType(),"vehicle");
        if(field.GetValue(__instance) is Pawn p&&p==testVehicle)
            Log.Message($"[RimLayers.VehicleTrace] native failure: cell={p.Position}, job={p.CurJob}\n{System.Environment.StackTrace}");
    }
    private static void RestoreVehicleGround()
    {
        if(GenCommandLine.CommandLineArgPassed("rimlayersvehicleflat")){RestoreVehicleFlat();return;}
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-ground-baseline.json")));
        var data=document.RootElement;
        testVehicle=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("vehicle").GetString());
        testVehicleCargo=testVehicle.inventory.innerContainer.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        Check("restart-preserves-vehicle-identity-layer-and-cargo",testVehicle.Position==At(1,54,60)&&testVehicleCargo.stackCount==37);
        var aboard=((System.Collections.IEnumerable)FrameworkVehicleAdapter.Property(testVehicle.GetType(),"AllPawnsAboard").GetValue(testVehicle)!).Cast<Pawn>().ToArray();
        Check("restart-preserves-crew-identities",data.GetProperty("crew").EnumerateArray().Select(e=>e.GetString()).OrderBy(s=>s).SequenceEqual(aboard.Select(p=>p.ThingID).OrderBy(s=>s)));
        Check("restart-rebuilds-native-upper-route",VehicleCompatibility.Adapter!.FindLeg(testVehicle,testVehicle.Position,At(1,54,58),PathEndMode.OnCell) is {Count:>0});
        vehiclePhase=1;stage=550;ticks=0;
    }
}
