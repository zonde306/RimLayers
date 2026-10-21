using HarmonyLib;
using UnityEngine;
using Verse;
namespace RimLayers.Vehicles;
internal sealed partial class SteelFlightAdapter
{
    [ThreadStatic] private static int projectedVisuals;
    private void InstallPresentation()
    {
        var rendering=CompType.Assembly.GetType("VehicleFramework.LocalUpgrade.AircraftAltitudeRendering",true)!;
        var presentation=CompType.Assembly.GetType("VehicleFramework.LocalUpgrade.AircraftPresentation",true)!;
        void Scope(Type type,string name,Type result,params Type[] args)=>patches.Patch(FrameworkVehicleAdapter.Method(type,name,result,args),
            prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(BeginVisualProjection)),finalizer:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(EndVisualProjection)));
        Scope(rendering,"ShadowCenter",typeof(Vector3),CompType);
        Scope(rendering,"ShadowFootprintScale",typeof(float),CompType);
        Scope(rendering,"DrawDynamicShadow",typeof(void),CompType,typeof(Material),typeof(MaterialPropertyBlock).MakeByRefType());
        Scope(presentation,"Scale",typeof(float),CompType);
        patches.Patch(FrameworkVehicleAdapter.Property(CompType,"AltitudeMeters").GetMethod!,postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(ProjectedAltitude)));
    }
    private static void BeginVisualProjection(ThingComp flight,out bool __state)
    {__state=Context(flight,out _,out _,out _);if(__state)projectedVisuals++;}
    private static void EndVisualProjection(bool __state){if(__state)projectedVisuals--;}
    private static void ProjectedAltitude(ThingComp __instance,ref float __result)
    {
        if(projectedVisuals==0||!Context(__instance,out _,out var owner,out var f))return;
        __result=(float)Math.Max(0,f.Datum+Installed!.altitude.Get(__instance)-owner.Elevation(f.ProxyLayer)*3);
    }
}
