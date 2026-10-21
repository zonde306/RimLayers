using HarmonyLib;
using RimLayers.Core;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static bool ShipVisual=>GenCommandLine.CommandLineArgPassed("rimlayersshipvisual");
    private static Action? shipVisualLaunch;
    private static int shipVisualLaunchFrames;
    private static readonly Dictionary<string,int> shipVisualSamples=new();
    private static void ConfigureShipVisual(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(UIRoot_Play),nameof(UIRoot_Play.UIRootOnGUI)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(VisualLaunchOnGui)));
        h.Patch(AccessTools.Method(typeof(GravshipCapturer),nameof(GravshipCapturer.BeginTerrainRender)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(VisualTerrainCapture)));
        h.Patch(AccessTools.Method(typeof(GravshipCapturer),nameof(GravshipCapturer.BeginGravshipRender)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(VisualCaptureStarted)));
        h.Patch(AccessTools.Method(typeof(WorldComponent_GravshipController),nameof(WorldComponent_GravshipController.OnGravshipCaptureComplete)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(VisualCaptureCompleted)));
        h.Patch(AccessTools.Method(typeof(GravshipRenderer),nameof(GravshipRenderer.UpdateTakeoff)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(VisualTakeoff)){priority=Priority.First});
        h.Patch(AccessTools.Method(typeof(GravshipRenderer),nameof(GravshipRenderer.UpdateLanding)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(VisualLanding)){priority=Priority.First});
        h.Patch(AccessTools.Method(typeof(GravshipRenderer),"DrawGravship"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(VisualShipDraw)){priority=Priority.Last,after=new[]{"rimlayers.core"}});
    }
    private static void VisualLaunchOnGui()
    {
        if(shipVisualLaunch==null||Event.current.type!=EventType.Repaint||++shipVisualLaunchFrames<3)return;
        var action=shipVisualLaunch;shipVisualLaunch=null;
        try{action();}
        catch(Exception e){Check("visual-gui-launch-exception",false,e.ToString());Finish();}
    }
    private static void VisualTerrainCapture(CellRect landingBounds)
    {Check("terrain-capture-submission-bounds-match-current-destination",GravshipCapturer.GravshipCaptureBounds==landingBounds);}
    private static void VisualCaptureStarted(Building_GravEngine engine)
    {
        if(engine==shipEngine&&LayerMapComponent.For(engine.Map) is {} o)
        {int floor=GenCommandLine.CommandLineArgPassed("rimlayerscapturecontrol")?0:ShipEngineLayer==2?1:2;LayerView.Switch(o,floor);Check("capture-starts-on-requested-floor",o.ViewedLayer==floor);}
    }
    private static void VisualCaptureCompleted(Capture capture)
    {
        if(capture.engine!=shipEngine)return;
        var texture=capture.capture.Texture;var pixels=texture.GetPixels32();
        int opaque=pixels.Count(p=>p.a>127);
        texture.SaveAsPNG(Path.Combine(directory,mode+"-ship-capture.png"));
        Check("native-capture-has-opaque-hull-on-requested-floor",opaque>pixels.Length*.2,$"opaque={opaque}/{pixels.Length}");
    }
    private static void VisualTakeoff(float progress)=>VisualSwitch(progress,"takeoff");
    private static void VisualLanding(float progress)=>VisualSwitch(progress,"landing");
    private static void VisualSwitch(float p,string kind)
    {
        if(LayerView.Current is not {} o)return;
        int layer=p<.35f?1:p<.65f?2:0;
        if(p>.08f&&p<.85f&&o.ViewedLayer!=layer)LayerView.Switch(o,layer);
    }
    private static void VisualShipDraw(Vector3 gravshipCenter,float cutsceneProgressPercent,bool isTakeoff)
    {
        var p=cutsceneProgressPercent;if(p<.16f||p>.64f||LayerView.Current is not {} o)return;
        string key=(isTakeoff?"takeoff":"landing")+"-layer-"+(o.ViewedLayer+1);
        if(shipVisualSamples.ContainsKey(key))return;
        shipVisualSamples[key]=1;
        float local=gravshipCenter.z-o.ViewedLayer*o.Layout!.Stride;
        Check(key+"-quad-is-in-view-band",local>-20&&local<o.Layout.Height+100,$"center={gravshipCenter}, localZ={local}");
        ScreenCapture.CaptureScreenshot(Path.Combine(directory,mode+"-"+key+".png"));
    }
}
