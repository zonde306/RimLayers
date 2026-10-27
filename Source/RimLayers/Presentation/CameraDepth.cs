using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Presentation;

/// <summary>
/// Native keeps farClipPlane at 65.5 while the camera itself rises to y = 65 at maximum zoom
/// (CameraDriver.ApplyPositionToGameObject: 15 + t*50, t = 1 at max RootSize). The visible bottom
/// edge therefore stops at y = -0.5: enough for a flat single-floor map, not for a projected lower
/// floor. LowerFloorBackdrop sinks floor distance d by d*3, so viewing the top layer puts the
/// backdrop at y = -3*d and the whole background is clipped away. That is the black screen behind an
/// upper layer once the camera is zoomed all the way out, and it also swallows the gravship
/// takeoff/landing cutscene, which maxes the zoom immediately. Rules live in CameraDepthMath.
/// </summary>
internal static class CameraDepth
{
    internal static int FloorsBelow(LayerMapComponent? owner)=>owner?.Layout?.Below(owner.ViewedLayer).Count??0;

    internal static void Apply(Camera? camera,LayerMapComponent? owner)
    {
        if(camera==null)return;
        float far=CameraDepthMath.Far(camera.transform.position.y,FloorsBelow(owner));
        // Tolerance well under one cell avoids rewriting the same float every frame.
        if(Mathf.Abs(camera.farClipPlane-far)>0.5f)camera.farClipPlane=far;
    }
}

// ApplyPositionToGameObject is the single writer of both the camera height and the far plane:
// Awake sets 65.5 and Update rewrites the transform every frame. A postfix there keeps the two in
// sync without a per-frame Harmony callback of our own. When the viewed layer has nothing below it
// the native 65.5 is written back, so a plain single-floor map keeps vanilla behaviour.
//
// Subcameras deliberately need nothing: GravshipCapturer.Fit(bounds, 15f) puts the capture cameras at
// y = 15 where native 65.5 already reaches -50, and widening WaterDepth's far plane would change the
// water depth samples that subcamera exists to produce.
[HarmonyPatch(typeof(CameraDriver),"ApplyPositionToGameObject")]
internal static class CameraDepthPatch
{
    static void Postfix(Camera ___cachedCamera)=>CameraDepth.Apply(___cachedCamera,LayerView.Current);
}
