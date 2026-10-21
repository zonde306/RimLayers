using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Recovery;
using RimWorld;
using Verse;
namespace RimLayers.Vehicles;

/// <summary>Known provider entry points fail closed when an optional adapter cannot bind.</summary>
internal static class VehicleCapabilityGuards
{
    private static Func<object,bool>? flightEligible;
    private static Func<object,object>? flightState;
    private static readonly Harmony patches=new("rimlayers.vehicles.capabilities");
    internal static void Install(FrameworkVehicleAdapter basic,Assembly? enhancement)
    {
        void Pre(MethodBase method,string hook)=>patches.Patch(method,prefix:new HarmonyMethod(typeof(VehicleCapabilityGuards),hook){priority=Priority.First});
        var launcher=basic.VehicleType.Assembly.GetType("Vehicles.CompVehicleLauncher",true)!;
        Pre(FrameworkVehicleAdapter.Method(launcher,"CompGetGizmosExtra",typeof(IEnumerable<Gizmo>)),nameof(WorldGizmos));
        Pre(AccessTools.GetDeclaredMethods(launcher).Single(m=>m.Name=="Launch"&&m.GetParameters().Length==2),nameof(WorldLaunch));
        if(enhancement==null)return;
        var flight=enhancement.GetType("VehicleFramework.LocalUpgrade.CompVehicleLocalFlight",true)!;
        Pre(FrameworkVehicleAdapter.Method(flight,"CompGetGizmosExtra",typeof(IEnumerable<Gizmo>)),nameof(FlightGizmos));
        Pre(FrameworkVehicleAdapter.Method(flight,"BeginTakeoff",typeof(void)),nameof(FlightLaunch));
        flightEligible=VehicleReflection.Call<Func<object,bool>>(FrameworkVehicleAdapter.Property(flight,"Eligible").GetMethod!);
        flightState=VehicleReflection.Call<Func<object,object>>(FrameworkVehicleAdapter.Property(flight,"State").GetMethod!);
        Pre(FrameworkVehicleAdapter.Method(flight,"CompTick",typeof(void)),nameof(FlightTick));
        var manual=enhancement.GetType("VehicleFramework.LocalUpgrade.GroundVehicleDirectControlUtility",true)!;
        Pre(FrameworkVehicleAdapter.Method(manual,"Toggle",typeof(void),basic.VehicleType),nameof(ManualToggle));
        Pre(FrameworkVehicleAdapter.Method(manual,"GetGizmos",typeof(IEnumerable<Gizmo>),basic.VehicleType),nameof(ManualGizmos));
    }
    private static bool Missing(Thing? pawn,VehicleCapabilities capability)=>pawn?.Spawned==true&&LayerMapComponent.For(pawn.Map)!=null&&!VehicleCompatibility.Capabilities.HasFlag(capability);
    private static bool Hide(Thing? pawn,VehicleCapabilities capability,ref IEnumerable<Gizmo> result)
    {if(!Missing(pawn,capability))return true;result=Array.Empty<Gizmo>();return false;}
    private static bool WorldGizmos(ThingComp __instance,ref IEnumerable<Gizmo> __result)=>Hide(__instance.parent,VehicleCapabilities.WorldTravel,ref __result);
    private static bool FlightGizmos(ThingComp __instance,ref IEnumerable<Gizmo> __result)=>Hide(__instance.parent,VehicleCapabilities.ContinuousFlight,ref __result);
    private static bool ManualGizmos(Pawn vehicle,ref IEnumerable<Gizmo> __result)=>Hide(vehicle,VehicleCapabilities.ManualDriving,ref __result);
    private static bool Reject(Thing pawn,VehicleCapabilities capability)
    {
        if(!Missing(pawn,capability))return true;
        Messages.Message("RL_VehicleUnavailable".Translate()+" "+VehicleCompatibility.EnhancementDiagnostic,pawn,MessageTypeDefOf.RejectInput,false);return false;
    }
    private static bool WorldLaunch(ThingComp __instance)=>Reject(__instance.parent,VehicleCapabilities.WorldTravel);
    private static bool FlightLaunch(ThingComp __instance)=>Reject(__instance.parent,VehicleCapabilities.ContinuousFlight);
    private static bool ManualToggle(Pawn vehicle)=>Reject(vehicle,VehicleCapabilities.ManualDriving);
    private static bool FlightTick(ThingComp __instance)
    {
        if(!Missing(__instance.parent,VehicleCapabilities.ContinuousFlight)||flightEligible!(__instance)==false||flightState!(__instance).ToString()=="Grounded")return true;
        // A previously airborne original object stays recoverable if a provider update breaks
        // its adapter. Never let vanilla falling or storage-map movement consume that object.
        RecoveryCompatibility.DeferFor(__instance.parent,3);return false;
    }
}
