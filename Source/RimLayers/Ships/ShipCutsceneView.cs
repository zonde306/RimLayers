using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
namespace RimLayers.Ships;

// Native captures stay in real coordinates. Only the final cutscene presentation
// moves to the selected storage band; the ship payload and viewed floor are unchanged.
internal static class ShipCutsceneSurface
{
    [ThreadStatic] internal static float Offset;
    internal static float Delta(Map map,Vector3 origin)
    {
        var o=LayerView.Current;
        return WorldComponent_GravshipController.CutsceneInProgress&&o!=null&&o.map==map&&o.TryDecode(origin.ToIntVec3(),out var at)?
            (o.ViewedLayer-at.LayerId)*o.Layout!.Stride:0;
    }
}
[HarmonyPatch]
internal static class ShipAnimationView
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GravshipRenderer),nameof(GravshipRenderer.UpdateTakeoff));
        yield return AccessTools.Method(typeof(GravshipRenderer),nameof(GravshipRenderer.UpdateLanding));
    }
    static void Prefix(Map ___map,ref Vector3 ___takeoffOrLandingPosition,ref Vector3 ___takeoffOrLandingEnginePos,out (Vector3 Center,Vector3 Engine) __state)
    {
        __state=(___takeoffOrLandingPosition,___takeoffOrLandingEnginePos);
        float delta=ShipCutsceneSurface.Delta(___map,___takeoffOrLandingEnginePos);
        ___takeoffOrLandingPosition.z+=delta;___takeoffOrLandingEnginePos.z+=delta;
    }
    static Exception? Finalizer(Exception? __exception,ref Vector3 ___takeoffOrLandingPosition,ref Vector3 ___takeoffOrLandingEnginePos,(Vector3 Center,Vector3 Engine) __state)
    {___takeoffOrLandingPosition=__state.Center;___takeoffOrLandingEnginePos=__state.Engine;return __exception;}
}
[HarmonyPatch(typeof(WorldComponent_GravshipController),"DrawTerrainCurtain")]
internal static class ShipTerrainCurtainView
{
    static void Prefix(Map ___map,CellRect ___terrainCurtainBounds,out float __state)
    {__state=ShipCutsceneSurface.Offset;ShipCutsceneSurface.Offset=ShipCutsceneSurface.Delta(___map,___terrainCurtainBounds.CenterVector3);}
    static Exception? Finalizer(Exception? __exception,float __state)
    {ShipCutsceneSurface.Offset=__state;return __exception;}
}
[HarmonyPatch(typeof(CameraDriver),nameof(CameraDriver.PanToMapLocAndSize))]
internal static class ShipCutscenePanView
{
    static void Prefix(ref Vector3 loc)
    {if(Find.CurrentMap is {} map)loc.z+=ShipCutsceneSurface.Delta(map,loc);}
}

// Unlike BeginGravshipRender, native BeginTerrainRender leaves the public
// submission bounds stale. Offscreen ground capture must submit its own area.
[HarmonyPatch(typeof(GravshipCapturer),nameof(GravshipCapturer.BeginTerrainRender))]
internal static class ShipTerrainCaptureBounds
{
    static void Prefix(CellRect landingBounds)
    {if(LayerMapComponent.For(Find.CurrentMap)!=null)GravshipCapturer.GravshipCaptureBounds=landingBounds;}
}
