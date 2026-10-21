using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;
using Verse.AI;
namespace RimLayers.Vehicles;

internal sealed partial class SteelFlightAdapter
{
    private Func<object,bool> fixedWing=null!;
    private Func<object,object> envelope=null!;
    private Func<object,float> climb=null!,descent=null!,turn=null!,localSpeed=null!;
    private Action<object> arrived=null!;
    private Func<object,float> maximumBank=null!,throttle=null!,powerFactor=null!,operatingMass=null!,referenceMass=null!;
    private void BindNavigation()
    {
        fixedWing=VehicleReflection.Call<Func<object,bool>>(FrameworkVehicleAdapter.Property(CompType,"UsesFixedWingDynamics").GetMethod!);
        var property=FrameworkVehicleAdapter.Property(CompType,"Envelope");envelope=VehicleReflection.Call<Func<object,object>>(property.GetMethod!);
        climb=VehicleReflection.Read<float>(property.PropertyType,"climbRate");descent=VehicleReflection.Read<float>(property.PropertyType,"descentRate");turn=VehicleReflection.Read<float>(property.PropertyType,"turnRate");
        localSpeed=VehicleReflection.Call<Func<object,float>>(FrameworkVehicleAdapter.Property(CompType,"LocalMapSpeed").GetMethod!);
        maximumBank=VehicleReflection.Read<float>(property.PropertyType,"maximumBank");
        throttle=VehicleReflection.Call<Func<object,float>>(FrameworkVehicleAdapter.Property(CompType,"Throttle01").GetMethod!);
        powerFactor=VehicleReflection.Call<Func<object,float>>(FrameworkVehicleAdapter.Property(CompType,"PowerSpeedFactor").GetMethod!);
        var dynamics=CompType.Assembly.GetType("VehicleFramework.LocalUpgrade.VehicleDynamicsUtility",true)!;
        operatingMass=VehicleReflection.Call<Func<object,float>>(FrameworkVehicleAdapter.Method(dynamics,"OperatingMassKg",typeof(float),basic.VehicleType));
        referenceMass=VehicleReflection.Call<Func<object,float>>(FrameworkVehicleAdapter.Method(dynamics,"ReferenceMassKg",typeof(float),basic.VehicleType.Assembly.GetType("Vehicles.VehicleDef",true)!));
        arrived=VehicleReflection.Call<Action<object>>(FrameworkVehicleAdapter.Method(basic.PatherType,"PatherArrived",typeof(void)));
    }
    private void InstallNavigation()
    {
        patches.Patch(Method("VehicleGroundProxy",typeof(Vector2)),prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(NavigationProxy)));
        patches.Patch(Method("TryDesiredHeading",typeof(bool),typeof(float).MakeByRefType()),prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(NavigationHeading)));
        patches.Patch(FrameworkVehicleAdapter.Property(CompType,"FreezePather").GetMethod!,postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(NavigationFreeze)));
        patches.Patch(FrameworkVehicleAdapter.Property(CompType,"IsHoldingPattern").GetMethod!,postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(NavigationHolding)));
    }
    public bool BeginRoute(Pawn pawn,LocalTargetInfo destination,PathEndMode mode)
    {
        if(!Active(pawn)||LayerMapComponent.For(pawn.Map) is not {} owner||!owner.TryDecode(destination.Cell,out var goal))return false;
        var f=pawn.Map.GetComponent<VehicleFlight>().Ensure(pawn);
        f.Destination=destination;f.EndMode=mode;f.JobId=pawn.CurJob?.loadID??-1;f.GoalHeight=owner.Elevation(goal.LayerId)*3+1.5;
        f.RouteActive=true;f.Guidance=null;f.NextPlanTick=0;
        StartNativeRoute(pawn,owner,f);return true;
    }
    private void StartNativeRoute(Pawn pawn,LayerMapComponent owner,VehicleFlightState f)
    {
        if(!owner.TryDecode(f.Destination.Cell,out var goal))return;
        var proxy=owner.ToStorage(new(f.ProxyLayer,goal.X,goal.Z));
        VehicleTraffic.Command(()=>VehicleCompatibility.Resume(pawn,proxy,f.EndMode));
    }
    public bool OwnsRoute(Pawn pawn)=>pawn.MapHeld?.GetComponent<VehicleFlight>().State(pawn)?.RouteActive==true;
    public void CancelRoute(Pawn pawn)
    {if(pawn.MapHeld?.GetComponent<VehicleFlight>().State(pawn) is {} f){f.RouteActive=false;f.Guidance=null;}}
    private void UpdateRoute(object comp,Pawn pawn,LayerMapComponent owner,VehicleFlightState f)
    {
        if(!f.RouteActive)return;
        if((pawn.CurJob?.loadID??-1)!=f.JobId||manual(comp)||crashing(comp)||f.Destination.ThingDestroyed||!owner.TryDecode(f.Destination.Cell,out _))
        {f.RouteActive=false;f.Guidance=null;return;}
        if(stateName(comp)!="Airborne")return;
        int now=Find.TickManager.TicksGame;
        if(f.Guidance.HasValue&&now<f.NextPlanTick&&f.PlannedRevision==owner.Revision)return;
        var native=envelope(comp);var body=Body(comp,owner,f);var hull=basic.Hull(pawn);
        var goal=VehicleSpace.Pose(owner,pawn,f.Destination.Cell,heading(comp)).Position with{Height=f.GoalHeight};
        double speed=Math.Max(.5,airSpeed(comp)),nominal=localSpeed(comp),power=powerFactor(comp),mass=Math.Clamp(operatingMass(pawn)/Math.Max(1,referenceMass(pawn.def)),.6,4);
        bool plane=fixedWing(comp);double minimum=plane?.22:.42;
        double up=climb(native)*(minimum+(1.18-minimum)*Math.Clamp((power-.46)/(.92),0,1));
        double down=descent(native)*(1.12-.30*throttle(comp));
        var prediction=new VehicleFlightEnvelope(speed,up/Math.Sqrt(mass),down/Math.Sqrt(mass),turn(native),plane,maximumBank(native),nominal,nominal*power,verticalSpeed(comp),up*.85/mass,down*.85/mass);
        var snapshot=VehicleAirspace.Snapshot(owner,pawn,body,new(goal,body.Yaw,body.Pitch,body.Bank),speed*5);
        // A modest vertical padding covers the aircraft's pitch/bank envelope during guidance.
        var plannedHull=new VehicleHull(Math.Sqrt(hull.Width*hull.Width+hull.Height*hull.Height),Math.Sqrt(hull.Length*hull.Length+hull.Height*hull.Height),Math.Sqrt(hull.Width*hull.Width+hull.Height*hull.Height)+hull.Length*.23);
        var boxes=snapshot.Surfaces.Where(s=>!s.Horizontal||s.Bounds.Top>Math.Min(body.Bottom.Height,goal.Height)+1e-7).Select(s=>s.Bounds).ToArray();
        f.Guidance=VehicleAirPlanner.Choose(plannedHull,body with{Pitch=0},goal,prediction,boxes);
        f.NextPlanTick=now+15;f.PlannedRevision=snapshot.Revision;
        if(f.Guidance is {} guidance)target.Set(comp,(float)(guidance.TargetHeight-f.Datum));
        // A fixed wing with no feasible maneuver keeps its native dynamics; the actual segment
        // collision decides the result rather than substituting helicopter hover or teleportation.
    }
    private void CompleteRoute(object comp,Pawn pawn,LayerMapComponent owner,VehicleFlightState f,RampBody body)
    {
        if(!f.RouteActive||stateName(comp)!="Airborne"||!owner.TryDecode(f.Destination.Cell,out var goal))return;
        var targetPose=VehicleSpace.Pose(owner,pawn,f.Destination.Cell,heading(comp)).Position;
        double radius=fixedWing(comp)?Math.Max(1,airSpeed(comp)/30):.65;
        if(goal.LayerId!=f.ProxyLayer||Math.Abs(body.Bottom.Height-f.GoalHeight)>.4||
            Math.Pow(body.Bottom.X-targetPose.X,2)+Math.Pow(body.Bottom.Z-targetPose.Z,2)>radius*radius)return;
        f.RouteActive=false;f.Guidance=null;
        VehicleTraffic.Command(()=>arrived(basic.Pather(pawn)));
    }
    private static bool NavigationProxy(ThingComp __instance,ref Vector2 __result)
    {
        if(!Context(__instance,out _,out var owner,out var f)||!f.RouteActive||f.Guidance is not {} guidance)return true;
        __result=new((float)guidance.Aim.X,(float)(guidance.Aim.Z+f.ProxyLayer*owner.Layout!.Stride));return false;
    }
    private static bool NavigationHeading(ThingComp __instance,ref float heading,ref bool __result)
    {
        if(!Context(__instance,out _,out _,out var f)||!f.RouteActive||f.Guidance is not {} guidance)return true;
        heading=(float)guidance.Heading;__result=true;return false;
    }
    private static void NavigationFreeze(ThingComp __instance,ref bool __result)
    {if(Context(__instance,out _,out _,out var f)&&f.RouteActive)__result=true;}
    private static void NavigationHolding(ThingComp __instance,ref bool __result)
    {if(Context(__instance,out _,out _,out var f)&&f.RouteActive)__result=false;}
}
