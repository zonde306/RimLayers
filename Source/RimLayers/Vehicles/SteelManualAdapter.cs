using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;
namespace RimLayers.Vehicles;

/// <summary>Runs Steel Horizon's original drive/steering integration. Only the facility's
/// constrained pose, occupancy and collision frame replace its flat-map assumptions.</summary>
internal sealed class SteelManualAdapter
{
    private static SteelManualAdapter? installed;
    private static readonly Harmony patches=new("rimlayers.vehicles.steel.manual");
    private readonly FrameworkVehicleAdapter basic;
    private readonly Type control,motion,rotation;
    private readonly Func<Pawn,bool> active;
    private readonly Func<ThingComp,Vector3> position;
    private readonly Func<ThingComp,float> heading;
    private readonly Action<ThingComp,Vector3,float> begin;
    private readonly Action<bool,bool> deactivate;
    private readonly Action brake;
    private readonly Action<float> setSteer,setFiltered,setHeading;
    private readonly Func<object,float> angle;
    [ThreadStatic] private static VehicleTravelState? frameTrip;
    private SteelManualAdapter(Assembly assembly,FrameworkVehicleAdapter basic)
    {
        this.basic=basic;
        if(assembly.GetName().Name!="VehicleFramework.LocalUpgrade")throw new InvalidOperationException("Unexpected enhancement identity.");
        control=assembly.GetType("VehicleFramework.LocalUpgrade.GroundVehicleDirectControlUtility",true)!;
        motion=assembly.GetType("VehicleFramework.LocalUpgrade.CompVehicleGroundMotion",true)!;
        rotation=FrameworkVehicleAdapter.Property(basic.VehicleType,"FullRotation").PropertyType;
        active=Call<Func<Pawn,bool>>(FrameworkVehicleAdapter.Method(control,"IsActive",typeof(bool),basic.VehicleType));
        position=Call<Func<ThingComp,Vector3>>(FrameworkVehicleAdapter.Property(motion,"Position").GetMethod!);
        heading=Call<Func<ThingComp,float>>(FrameworkVehicleAdapter.Property(motion,"Heading").GetMethod!);
        begin=Call<Action<ThingComp,Vector3,float>>(FrameworkVehicleAdapter.Method(motion,"Begin",typeof(void),typeof(Vector3),typeof(float)));
        deactivate=Call<Action<bool,bool>>(FrameworkVehicleAdapter.Method(control,"Deactivate",typeof(void),typeof(bool),typeof(bool)));
        brake=Call<Action>(FrameworkVehicleAdapter.Method(control,"RequestServiceBrake",typeof(void)));
        setSteer=Setter(control,"steeringInput");setFiltered=Setter(control,"filteredSteeringInput");setHeading=Setter(control,"desiredHeading");
        angle=Call<Func<object,float>>(FrameworkVehicleAdapter.Property(rotation,"AsAngle").GetMethod!);
    }
    private static T Call<T>(MethodInfo method)where T:Delegate
    {
        var signature=typeof(T).GetMethod("Invoke")!;
        var args=signature.GetParameters().Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
        int offset=method.IsStatic?0:1;
        var call=Expression.Call(method.IsStatic?null:Expression.Convert(args[0],method.DeclaringType!),method,
            method.GetParameters().Select((p,i)=>Expression.Convert(args[i+offset],p.ParameterType)));
        return Expression.Lambda<T>(call,args).Compile();
    }
    private static Action<float> Setter(Type type,string name)
    {
        var field=FrameworkVehicleAdapter.Field(type,name);if(!field.IsStatic||field.FieldType!=typeof(float))throw new MissingFieldException(name);
        var value=Expression.Parameter(typeof(float),"value");
        return Expression.Lambda<Action<float>>(Expression.Assign(Expression.Field(null,field),value),value).Compile();
    }
    internal static bool TryInstall(Assembly assembly,FrameworkVehicleAdapter basic,out string diagnostic)
    {
        try
        {
            var a=new SteelManualAdapter(assembly,basic);
            var run=FrameworkVehicleAdapter.Method(a.control,"RunManualPatherTick",typeof(void),basic.VehicleType,basic.PatherType);
            var occupy=FrameworkVehicleAdapter.Method(a.control,"CanOccupy",typeof(bool),basic.VehicleType,typeof(IntVec3),a.rotation);
            var commit=FrameworkVehicleAdapter.Method(a.control,"CommitContinuousCell",typeof(void),basic.VehicleType,typeof(IntVec3),typeof(float));
            var publish=FrameworkVehicleAdapter.Method(a.motion,"Publish",typeof(void),typeof(Vector3),typeof(float));
            var sweep=FrameworkVehicleAdapter.Method(a.motion,"SweepCollisions",typeof(void),typeof(Vector3),typeof(Vector3),typeof(float),typeof(float),typeof(float));
            var impact=FrameworkVehicleAdapter.Method(a.motion,"ImpactBlockingHull",typeof(void),typeof(Vector3),typeof(float));
            var cost=FrameworkVehicleAdapter.Method(basic.PatherType,"CostToMoveIntoCell",typeof(float),basic.VehicleType,typeof(IntVec3),typeof(IntVec3));
            void Prefix(MethodInfo m,string name)=>patches.Patch(m,prefix:new HarmonyMethod(typeof(SteelManualAdapter),name){priority=Priority.First});
            patches.Patch(run,prefix:new HarmonyMethod(typeof(SteelManualAdapter),nameof(BeforeTick)),postfix:new HarmonyMethod(typeof(SteelManualAdapter),nameof(AfterTick)),finalizer:new HarmonyMethod(typeof(SteelManualAdapter),nameof(EndFrame)));
            Prefix(occupy,nameof(CanOccupy));Prefix(commit,nameof(CommitCell));Prefix(publish,nameof(BeforePublish));
            Prefix(sweep,nameof(SkipFlatCollision));Prefix(impact,nameof(SkipFlatCollision));Prefix(cost,nameof(FloorCost));
            installed=a;diagnostic="Native WASD ramp pose integration installed.";return true;
        }
        catch(Exception error){patches.UnpatchAll(patches.Id);installed=null;diagnostic="Manual driving integration disabled: "+error;return false;}
    }
    internal static bool Active(Pawn pawn)=>installed?.active(pawn)==true;
    internal static void Release(Pawn pawn){if(Active(pawn))VehicleTraffic.Command(()=>installed!.deactivate(true,false));}
    private ThingComp? Motion(Pawn pawn)=>pawn.AllComps.FirstOrDefault(motion.IsInstanceOfType);
    private static bool BeforeTick(Pawn vehicle,out double __state)
    {
        __state=0;frameTrip=null;var a=installed;
        if(a==null||!vehicle.Spawned||LayerMapComponent.For(vehicle.Map)==null||a.Motion(vehicle) is not {} comp)return true;
        var traffic=vehicle.Map.GetComponent<VehicleTraffic>();
        var trip=traffic.ManualRamp(vehicle,a.position(comp),a.heading(comp));
        if(trip==null)return true;
        __state=trip.ManualFraction;
        if(!traffic.AdmitManual(trip)){a.brake();return false;}
        frameTrip=trip;
        // The ramp constrains yaw, not the engine. Native acceleration, reverse, mass, fuel and braking still run.
        a.setSteer(0);a.setFiltered(0);a.setHeading((float)trip.Heading);
        var point=traffic.DrawPosition(vehicle)!.Value;
        if((a.position(comp)-point).sqrMagnitude>.0001f)a.begin(comp,point,(float)trip.Heading);
        return true;
    }
    private static void AfterTick(Pawn vehicle,double __state)
    {
        if(frameTrip is not {} trip||installed is not {} a||!vehicle.Spawned)return;
        var traffic=vehicle.Map.GetComponent<VehicleTraffic>();
        var point=traffic.FinishManualFrame(trip);
        if(a.Motion(vehicle) is {} comp&&(a.position(comp)-point).sqrMagnitude>.0001f)a.begin(comp,point,(float)trip.Heading);
        a.basic.SyncContinuousNextCell(vehicle);
        traffic.EndManualIfClear(trip,__state);
    }
    private static void EndFrame()=>frameTrip=null;
    private static bool CanOccupy(Pawn vehicle,IntVec3 center,object rotation,ref bool __result)
    {
        if(frameTrip is not {} trip||trip.Vehicle!=vehicle||installed is not {} a)return true;
        if(Math.Abs(VehicleGeometry.SignedTurn(a.angle(rotation),trip.Heading))>.1){__result=false;return false;}
        var owner=LayerMapComponent.For(vehicle.Map)!;
        var before=VehicleRampService.Body(owner,trip,trip.ManualFraction);
        var candidate=VehicleSpace.Pose(owner,vehicle,center,trip.Heading).Position;
        var after=trip.Ramp.Body(a.basic.Hull(vehicle),candidate.X,candidate.Z);
        __result=VehicleRampService.CanSweep(owner,vehicle,trip.Ramp,before,after);return false;
    }
    private static bool CommitCell(Pawn vehicle,IntVec3 nextCell)
    {
        if(frameTrip is not {} trip||trip.Vehicle!=vehicle)return true;
        installed!.basic.CommitContinuous(vehicle,nextCell,trip.Heading);return false;
    }
    private static void BeforePublish(ThingComp __instance,ref Vector3 point,ref float angle)
    {
        if(frameTrip is not {} trip||__instance.parent!=trip.Vehicle)return;
        if(!trip.Vehicle.Map.GetComponent<VehicleTraffic>().PublishManual(trip,ref point))installed!.brake();
        angle=(float)trip.Heading;
    }
    private static bool SkipFlatCollision(ThingComp __instance)=>frameTrip?.Vehicle!=__instance.parent;
    private static void FloorCost(Pawn vehicle,ref IntVec3 from,ref IntVec3 to)
    {
        if(frameTrip is not {} trip||trip.Vehicle!=vehicle)return;
        var owner=LayerMapComponent.For(vehicle.Map)!;
        if(owner.TryDecode(from,out var f)&&owner.TryDecode(to,out var t))
        {
            owner.TryDecode(trip.EntryCell,out var entry);owner.TryDecode(trip.ExitCell,out var exit);
            var lower=owner.Elevation(entry.LayerId)<owner.Elevation(exit.LayerId)?trip.EntryCell:trip.ExitCell;
            // The ramp is impassable to ordinary flat-map paths. Use the native cost of
            // its clear lower approach, not the artificial impassable-building penalty.
            var direction=to-from;from=lower;to=lower+direction;
        }
    }
}
