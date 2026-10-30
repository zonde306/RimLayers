using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Presentation;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void BeginPanelDrag()
    {
        LayerView.Switch(owner,0);
        PanelDragChecks();
        GameDataSaveLoader.SaveGame("RimLayers_V1PanelDrag");
        stage=283;ticks=0;
    }
    private static void PanelDragStep()
    {
        if(ticks++>10)Finish();
    }

    private static void RestorePanelDrag()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"panel-drag-baseline.json")));
        var root=baseline.RootElement;
        var panel=LayerView.ControlsRect(owner);
        Check("dragged-panel-position-survives-process-restart",
            Math.Abs(panel.x-root.GetProperty("x").GetSingle())<1f&&Math.Abs(panel.y-root.GetProperty("y").GetSingle())<1f,
            $"expected={root.GetProperty("x").GetSingle()}/{root.GetProperty("y").GetSingle()},actual={panel.position},offset={RimLayersMod.Settings.FloorPanelOffset}");
        Check("reloaded-panel-keeps-its-native-size",Math.Abs(panel.width-root.GetProperty("width").GetSingle())<1f&&Math.Abs(panel.height-root.GetProperty("height").GetSingle())<1f,
            $"actual={panel.size}");
        RimLayersMod.Settings.FloorPanelOffset=Vector2.zero;
        Finish();
    }

    /// <summary>
    /// Drives the real panel through its real drag handler in the coordinate space the panel itself
    /// reports. Nothing here replays the geometry privately: every phase calls LayerView.DrawControls,
    /// so a hit rectangle that stopped following the panel, a drag that stopped consuming input or a
    /// panel that escaped the screen all fail an assertion instead of passing.
    /// </summary>
    private static void PanelDragChecks()
    {
        var settings=RimLayersMod.Settings;
        settings.FloorPanelOffset=Vector2.zero;
        settings.FloorPanel=true;
        var start=LayerView.ControlsRect(owner);
        Rect HandleOf(Rect panel)=>new(panel.x,panel.y-PanelDragMath.HandleHeight,panel.width,PanelDragMath.HandleHeight);
        var hook=new Harmony("rimlayers.diagnostics.panel-drag");
        var previous=Event.current;
        try
        {
            var backend=AccessTools.Property(typeof(Event).Assembly.GetType("UnityStubs.Gui.GuiBackend"),"Current").GetValue(null)!;
            hook.Patch(AccessTools.PropertyGetter(backend.GetType(),"MouseOutsideHitClip"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputClip)));
            hook.Patch(AccessTools.Method(typeof(Input),nameof(Input.GetMouseButton)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputHeld)));
            hook.Patch(AccessTools.PropertyGetter(typeof(UI),nameof(UI.MousePositionOnUI)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputUiMouse)));
            void Frame(EventType kind,Vector2 point,int clickCount=1)
            {
                settingsInputBusy=true;settingsInputOutsideClip=false;settingsInputPoint=point;
                settingsInputKind=kind;settingsInputPhase=0;
                Event.current=new Event{type=kind,rawType=kind,button=0,clickCount=clickCount,mousePosition=point};
                LayerView.DrawControls(owner);
            }
            Check("drag-handle-sits-inside-the-screen",start.y-PanelDragMath.HandleHeight>=0&&start.xMax<=UI.screenWidth,$"panel={start};screen={UI.screenWidth}x{UI.screenHeight}");
            var grip=HandleOf(start).center;
            var delta=new Vector2(-260,150);
            Frame(EventType.MouseDown,grip);
            Frame(EventType.MouseDrag,grip+delta*.5f);
            Frame(EventType.MouseDrag,grip+delta);
            var moved=LayerView.ControlsRect(owner);
            Check("dragging-moves-the-panel-with-the-pointer",
                Math.Abs(moved.x-(start.x+delta.x))<1f&&Math.Abs(moved.y-(start.y+delta.y))<1f,$"start={start},moved={moved},offset={settings.FloorPanelOffset}");
            Check("drag-handle-follows-the-moved-panel",HandleOf(moved).Contains(grip+delta));
            Frame(EventType.MouseUp,grip+delta);
            Check("release-keeps-the-new-position",settings.FloorPanelOffset!=Vector2.zero,$"offset={settings.FloorPanelOffset}");
            Event.current=new Event{type=EventType.Repaint,mousePosition=grip+delta};
            Check("moved-panel-owns-its-new-hit-rectangle",LayerView.MouseOverFloorControls(owner),$"point={grip+delta},panel={moved}");
            Event.current=new Event{type=EventType.Repaint,mousePosition=start.center};
            Check("vacated-corner-no-longer-claims-the-panel",!LayerView.MouseOverFloorControls(owner));
            // Buttons must travel with the panel: a "+" click at the moved position still queues a floor.
            var transitions=settings.LayerTransitions;
            settings.LayerTransitions=true;
            try
            {
                var plus=new Vector2(moved.xMax-25,moved.y+22);
                Frame(EventType.MouseDown,plus);
                Frame(EventType.MouseUp,plus);
                Check("moved-panel-buttons-still-queue-a-floor",LayerTransition.Active&&LayerTransition.TargetLayer==1&&owner.ViewedLayer==0,
                    $"active={LayerTransition.Active},target={LayerTransition.TargetLayer},viewed={owner.ViewedLayer}");
            }
            finally{LayerTransition.FinishForInput();settings.LayerTransitions=transitions;}
            // The pointer leaves the window entirely; the panel must stay wholly visible.
            var grip2=HandleOf(LayerView.ControlsRect(owner)).center;
            Frame(EventType.MouseDown,grip2);
            Frame(EventType.MouseDrag,new Vector2(5000,5000));
            var clamped=LayerView.ControlsRect(owner);
            Check("dragging-cannot-push-the-panel-off-screen",
                clamped.x>=0&&clamped.y>=0&&clamped.xMax<=UI.screenWidth&&clamped.yMax<=UI.screenHeight,$"clamped={clamped},screen={UI.screenWidth}x{UI.screenHeight}");
            Check("clamped-drag-keeps-the-handle-reachable",clamped.y-PanelDragMath.HandleHeight>=0,$"clamped={clamped}");
            Frame(EventType.MouseUp,new Vector2(5000,5000));
            // Double-click snaps back, the only discoverable way out of a bad position.
            var snap=HandleOf(LayerView.ControlsRect(owner)).center;
            Frame(EventType.MouseDown,snap,2);
            Check("double-click-handle-restores-the-native-corner",settings.FloorPanelOffset==Vector2.zero,$"offset={settings.FloorPanelOffset}");
            Check("restored-panel-matches-the-native-rectangle",LayerView.ControlsRect(owner).position==start.position,$"actual={LayerView.ControlsRect(owner).position}");
            Frame(EventType.MouseUp,snap);
            // Leave a deterministic offset behind for the reload half of this scenario.
            var grip3=HandleOf(LayerView.ControlsRect(owner)).center;
            Frame(EventType.MouseDown,grip3);
            Frame(EventType.MouseDrag,grip3+new Vector2(30,-100));
            Frame(EventType.MouseUp,grip3+new Vector2(30,-100));
            var saved=LayerView.ControlsRect(owner);
            File.WriteAllText(Path.Combine(directory,"panel-drag-baseline.json"),JsonSerializer.Serialize(
                new{x=saved.x,y=saved.y,width=saved.width,height=saved.height,screenWidth=UI.screenWidth,screenHeight=UI.screenHeight}));
            Check("drag-offset-is-written-to-the-settings-file",PanelDragSaved(saved),saved.ToString());
        }
        finally{settingsInputBusy=false;hook.UnpatchAll(hook.Id);Event.current=previous;}
    }

    /// <summary>The settings file is the only durable copy of the offset, so assert the real file.</summary>
    private static bool PanelDragSaved(Rect expected)
    {
        string file=Path.Combine(GenFilePaths.ConfigFolderPath,
            GenText.SanitizeFilename($"Mod_{RimLayersMod.SettingsContent.FolderName}_{typeof(RimLayersMod).Name}.xml"));
        if(!File.Exists(file)){Log.Warning($"[RimLayers.PanelDrag] no settings file at {file}");return false;}
        string xml=File.ReadAllText(file);
        Log.Message($"[RimLayers.PanelDrag] saved={expected}; file={file}; entry={xml.Contains("floorPanelOffset")}");
        return xml.Contains("floorPanelOffset");
    }
}
