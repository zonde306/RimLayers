using System.Reflection;
using System.Text.Json;
using RimLayers.Core;
using RimLayers.Vehicles;
using RimLayers.Presentation;
using Verse;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static MethodInfo manualToggle=null!,manualInput=null!;
    private static double manualCheckpoint;
    private static IntVec3 manualUpper;
    private static FieldInfo manualBrake=null!;
    private static void BindManualTest()
    {
        var asm=AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a=>a.GetName().Name=="VehicleFramework.LocalUpgrade");
        if(asm==null)return;
        var control=asm.GetType("VehicleFramework.LocalUpgrade.GroundVehicleDirectControlUtility",true)!;
        manualToggle=FrameworkVehicleAdapter.Method(control,"Toggle",typeof(void),testVehicle.GetType());
        manualBrake=FrameworkVehicleAdapter.Field(control,"brakeInput");
        manualInput=FrameworkVehicleAdapter.Method(control,"SetDriveInput",typeof(void),typeof(float),typeof(float));
        new HarmonyLib.Harmony("rimlayers.vehicle.manual.test").Patch(FrameworkVehicleAdapter.Method(control,"Deactivate",typeof(void),typeof(bool),typeof(bool)),prefix:new HarmonyLib.HarmonyMethod(typeof(V1Integration),nameof(ObserveManualDeactivate)));
    }
    private static void ObserveManualDeactivate()
    {
        if(testVehicle!=null&&SteelManualAdapter.Active(testVehicle))Log.Message($"[RimLayers.ManualTrace] deactivate selected={Find.Selector.IsSelected(testVehicle)} canMove={VehicleCompatibility.Adapter!.CanMove(testVehicle)} trip={testVehicle.MapHeld?.GetComponent<VehicleTraffic>().Trip(testVehicle)?.Phase} cell={testVehicle.Position}\n{System.Environment.StackTrace}");
    }
    private static void VehicleManualRampStep()
    {
        var traffic=map.GetComponent<VehicleTraffic>();var trip=traffic.Trip(testVehicle);
        if(vehiclePhase==0)
        {
            if(ticks<120)return;
            Check("manual-capability-binds-independently",VehicleCompatibility.Capabilities.HasFlag(VehicleCapabilities.ManualDriving),VehicleCompatibility.EnhancementDiagnostic);
            BindManualTest();
            Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            manualToggle.Invoke(null,new object[]{testVehicle});
            Check("native-wasd-activated",SteelManualAdapter.Active(testVehicle));
            vehiclePhase=30;ticks=0;return;
        }
        if(vehiclePhase==30)
        {
            manualBrake.SetValue(null,false);manualInput.Invoke(null,new object[]{0f,1f});
            if(ticks%120==0)Log.Message($"[RimLayers.ManualTrace] active={SteelManualAdapter.Active(testVehicle)} selected={Find.Selector.IsSelected(testVehicle)} canMove={VehicleCompatibility.Adapter!.CanMove(testVehicle)} trip={trip?.Phase} manual={trip?.Manual} fraction={trip?.ManualFraction} cell={testVehicle.Position}");
            if(trip?.Manual!=true||trip.ManualFraction<.5)
            {if(ticks>6000)throw new Exception("Native manual driving did not reach ramp midpoint. "+trip?.Phase+" "+VehicleMotionTrace());return;}
            Check("native-wasd-enters-shared-ramp-transport",trip.Phase==VehicleTravelPhase.Crossing&&trip.ManualFraction<.6);
            manualCheckpoint=trip.ManualFraction;
            File.WriteAllText(Path.Combine(directory,"vehicle-ramp-baseline.json"),JsonSerializer.Serialize(new{
                vehicle=testVehicle.ThingID,cargo=testVehicleCargo.ThingID,crew=testVehicleCrew.Select(p=>p.ThingID).ToArray(),job=testVehicle.CurJob?.loadID??-1,remaining=trip.RemainingTicks,fraction=manualCheckpoint}));
            GameDataSaveLoader.SaveGame("RimLayers_VehicleRamp");
            Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            Find.CameraDriver.SetRootPosAndSize(testVehicle.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"vehicle-manual-ramp.png");screenshotHold=3;vehiclePhase=99;ticks=0;return;
        }
        if(vehiclePhase==40)
        {
            if(ticks<60)return;
            Check("manual-restart-holds-safe-pose-without-input",trip?.Manual==true&&Math.Abs(trip.ManualFraction-manualCheckpoint)<1e-8&&!SteelManualAdapter.Active(testVehicle),$"phase={trip?.Phase},manual={trip?.Manual},fraction={trip?.ManualFraction},saved={manualCheckpoint},modules={traffic.Connectors.Count},cell={testVehicle.Position}");
            Find.Selector.ClearSelection();Find.Selector.Select(testVehicle);
            manualToggle.Invoke(null,new object[]{testVehicle});vehiclePhase=41;ticks=0;return;
        }
        if(vehiclePhase==41)
        {
            manualBrake.SetValue(null,false);manualInput.Invoke(null,new object[]{0.8f,1f}); // Steering is constrained only while the body occupies the grade.
            if(trip!=null)
            {
                if(Math.Abs(VehicleGeometry.SignedTurn(VehicleCompatibility.Adapter!.Heading(testVehicle),trip.Heading))>.1)throw new Exception("Manual steering turned across the ramp.");
                if(ticks>6000)throw new Exception("Manual ramp did not finish after reactivation. "+trip.ManualFraction+" "+VehicleMotionTrace());return;
            }
            SteelManualAdapter.Release(testVehicle);
            Check("manual-finishes-in-upper-coordinate-frame",owner.TryDecode(testVehicle.Position,out var at)&&at.LayerId==1);
            Check("manual-camera-follows-only-controlled-vehicle",owner.ViewedLayer==1);
            Check("manual-ramp-retains-cargo-and-crew",testVehicle.inventory.innerContainer.Contains(testVehicleCargo)&&testVehicleCargo.stackCount==37&&testVehicleCrew.All(p=>p.MapHeld==map&&!p.Spawned));
            manualUpper=testVehicle.Position;
            if(!VehicleSpace.CanTurn(owner,testVehicle,manualUpper,VehicleCompatibility.Adapter!.Heading(testVehicle),180))throw new Exception("Manual return fixture has insufficient turning space.");
            VehicleTraffic.Command(()=>VehicleCompatibility.Adapter!.Commit(testVehicle,manualUpper,180));
            manualToggle.Invoke(null,new object[]{testVehicle});vehiclePhase=42;ticks=0;return;
        }
        if(vehiclePhase==42)
        {
            manualBrake.SetValue(null,false);manualInput.Invoke(null,new object[]{0f,1f});
            if(trip?.Manual!=true||trip.ManualFraction<.4){if(ticks>4000)throw new Exception("Manual descent did not enter grade.");return;}
            traffic.CancelOrder(testVehicle);vehiclePhase=43;ticks=0;return;
        }
        if(vehiclePhase==43)
        {
            if(trip!=null){if(ticks>600)throw new Exception("Cancelled manual descent did not retreat to source.");return;}
            Check("manual-cancel-retreats-without-turning-on-grade",testVehicle.Spawned&&testVehicle.Position==manualUpper&&VehicleNativeStopped());
            manualToggle.Invoke(null,new object[]{testVehicle});vehiclePhase=44;ticks=0;return;
        }
        if(vehiclePhase==44)
        {
            manualBrake.SetValue(null,false);manualInput.Invoke(null,new object[]{0f,1f});
            if(trip!=null||!owner.TryDecode(testVehicle.Position,out var at)||at.LayerId!=0)
            {if(ticks>4000)throw new Exception("Manual downward journey did not complete.");return;}
            SteelManualAdapter.Release(testVehicle);
            Check("manual-downward-transit-completes-on-lower-floor",owner.ViewedLayer==0&&testVehicle.inventory.innerContainer.Contains(testVehicleCargo));
            vehiclePhase=99;ticks=0;return;
        }
        if(vehiclePhase==99&&ticks>30)Finish();
    }
    private static void RestoreManualRamp()
    {
        BindManualTest();
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"vehicle-ramp-baseline.json")));
        manualCheckpoint=document.RootElement.GetProperty("fraction").GetDouble();
        vehiclePhase=40;
    }
}
