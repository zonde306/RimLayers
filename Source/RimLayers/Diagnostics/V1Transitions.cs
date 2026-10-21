using System.Text.Json;
using System.Globalization;
using Verse.AI;
using HarmonyLib;
using RimLayers.Generation;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static int transitionCase, transitionFrames, transitionImages, transitionStableCaptures;
    private static float transitionNext, transitionRequested, transitionLastFrame;
    private static bool transitionRunning, transitionRetargeted;
    private static Vector3 transitionRoot;
    private static float transitionSize, transitionLocalZ;
    private static int transitionTick;
    private static bool transitionKeysDone;
    private static readonly List<object> transitionTiming = new();
    private static readonly List<float> transitionFrameTimes = new();
    private static readonly List<double> transitionCaptureTimes = new();
    private static readonly int[] transitionElevations = {1,0,-1,0,2,0,1,0,1,0,1,0,1,0,1,0,1};
    private static void BeginTransitions()
    {
        LiveMapExpansion.EnsureElevation(map,-1);
        int underground = owner.Layout!.LayerAt(-1);
        foreach (var cell in new CellRect(48,48,20,20))
        {
            var at = At(underground,cell.x,cell.z);
            foreach (var thing in at.GetThingList(map).ToList())
                if (thing is not Pawn) { if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish); else thing.DeSpawn(); }
            map.terrainGrid.SetTerrain(at, TerrainDefOf.Concrete);
            map.fogGrid.Unfog(at);
        }
        foreach (int layer in new[]{0,1,2,underground})
        {
            for (int x=54;x<=60;x+=3)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Stool,ThingDefOf.WoodLog),At(layer,x,57),map);
        }
        Find.Selector.ClearSelection(); Find.Selector.Select(pawn);
        LayerView.Switch(owner,0);
        Find.CameraDriver.SetRootPosAndSize(At(0,58,58).ToVector3Shifted(),14);
        GameDataSaveLoader.SaveGame("RimLayers_Transitions");
        StartTransitionSequence();
    }
    private static void RestoreTransitions()
    {
        pawn=map.mapPawns.FreeColonistsSpawned.First();
        LayerView.Switch(owner,0);
        Find.CameraDriver.SetRootPosAndSize(At(0,58,58).ToVector3Shifted(),14);
        StartTransitionSequence();
    }
    private static void StartTransitionSequence()
    {
        if(GenCommandLine.TryGetCommandLineArg("rimlayersuiscale",out var scale)) Prefs.UIScale=float.Parse(scale,CultureInfo.InvariantCulture);
        ResolutionUtility.SetResolutionRaw(ScaleArgument("screen-width",1920,1024,3840),ScaleArgument("screen-height",1080,720,2160),ScaleArgument("screen-height",1080,720,2160)>=1080);
        Find.Selector.ClearSelection(); Find.Selector.Select(pawn);
        RimLayersMod.Settings.LayerTransitions=true;
        Check("transition-setting-default-on",new RimLayersSettings().LayerTransitions);
        Check("transition-no-state-after-load",!LayerTransition.Active&&LayerTransition.LiveTextures==0);
        Check("underground-storage-index-does-not-set-direction",owner.Layout!.LayerAt(-1)>owner.Layout.LayerAt(1));
        Check("invalid-manual-request-is-rejected",!LayerTransition.Request(owner,-1)&&!LayerTransition.Active);
        Check("same-layer-request-does-not-capture",LayerTransition.Request(owner,0)&&!LayerTransition.Active);
        transitionNext=Time.realtimeSinceStartup+1;stage=510;
        transitionStableCaptures=LayerTransition.SceneCaptures;
    }
    private static void TransitionsStep()
    {
        if (stage!=510) return;
        float now=Time.realtimeSinceStartup;
        if (!transitionRunning)
        {
            if(now<transitionNext)return;
            Check("idle-does-not-capture-"+transitionCase,LayerTransition.SceneCaptures==transitionStableCaptures);
            if(transitionCase>=transitionElevations.Length)
            {
                if(!transitionKeysDone||LayerTransition.Active)return;
                transitionFailureHook?.UnpatchAll(transitionFailureHook.Id);transitionFailureHook=null;
                Check("capture-failure-falls-back-to-immediate-switch",owner.ViewedLayer==1&&LayerTransition.LiveTextures==0);
                Check("actual-transition-resolution",Screen.width==ScaleArgument("screen-width",1920,1024,3840)&&Screen.height==ScaleArgument("screen-height",1080,720,2160));
                File.WriteAllText(Path.Combine(directory,mode+"-transition-timing.json"),JsonSerializer.Serialize(new{
                    Screen.width,Screen.height,uiScale=Prefs.UIScale,frameMilliseconds=transitionFrameTimes,transitions=transitionTiming,captureMilliseconds=transitionCaptureTimes,
                    captures=LayerTransition.SceneCaptures,compositions=LayerTransition.Compositions
                },new JsonSerializerOptions{WriteIndented=true}));
                Check("all-transition-textures-released",LayerTransition.LiveTextures==0);
                Finish();return;
            }
            int target=owner.Layout!.LayerAt(transitionElevations[transitionCase]);
            if(transitionCase==8) Find.CameraDriver.SetRootPosAndSize(At(owner.ViewedLayer,2,2).ToVector3Shifted(),Find.CameraDriver.config.sizeRange.min);
            if(transitionCase==9) Find.CameraDriver.SetRootPosAndSize(At(owner.ViewedLayer,118,2).ToVector3Shifted(),Find.CameraDriver.config.sizeRange.min);
            if(transitionCase==10) Find.CameraDriver.SetRootPosAndSize(At(owner.ViewedLayer,118,118).ToVector3Shifted(),Find.CameraDriver.config.sizeRange.max);
            if(transitionCase==11) Find.CameraDriver.SetRootPosAndSize(At(owner.ViewedLayer,2,118).ToVector3Shifted(),Find.CameraDriver.config.sizeRange.max);
            if(transitionCase==12) Find.CameraDriver.SetRootPosAndSize(At(owner.ViewedLayer,58,58).ToVector3Shifted(),14);
            Find.CameraDriver.Update();
            transitionRoot=LayerView.CameraRoot;transitionSize=Find.CameraDriver.RootSize;
            transitionLocalZ=transitionRoot.z-owner.ViewedLayer*owner.Layout.Stride;
            Find.TickManager.CurTimeSpeed=transitionCase is 2 or 3?TimeSpeed.Normal:transitionCase is 4 or 5?TimeSpeed.Superfast:TimeSpeed.Paused;
            transitionTick=Find.TickManager.TicksGame;
            if(transitionCase==4)pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,At(0,63,58)));
            int old=owner.ViewedLayer;
            if(transitionCase==14)RimLayersMod.Settings.LayerTransitions=false;
            Check("request-accepted-"+transitionCase,LayerTransition.Request(owner,target));
            if(transitionCase==14)
            {
                Check("disabled-switch-is-synchronous",owner.ViewedLayer==target&&!LayerTransition.Active);
                RimLayersMod.Settings.LayerTransitions=true;
            }
            else Check("manual-request-retains-source-until-captured-"+transitionCase,owner.ViewedLayer==old&&(target==old||LayerTransition.Active));
            transitionRequested=transitionLastFrame=now;transitionRunning=true;transitionRetargeted=false;transitionFrames=0;transitionImages=0;
            return;
        }
        transitionFrameTimes.Add((now-transitionLastFrame)*1000);transitionLastFrame=now;
        transitionFrames++;
        if(LayerTransition.Active)
        {
            transitionCaptureTimes.Add(LayerTransition.LastCaptureMs);
            CheckOnceTransitionBudget();
            if(transitionCase==4&&LayerTransition.Progress>.1f&&!transitionRetargeted)
            {
                // Latest target is 1 -> 0, rather than queuing 2 -> 1 -> 0 animations.
                LayerTransition.RequestRelative(owner,-1);LayerTransition.RequestRelative(owner,-1);
                transitionRetargeted=true;
                Check("repeat-keys-coalesce-to-latest-target",LayerTransition.TargetLayer==0);
            }
            if(transitionCase==6&&LayerTransition.Progress>.1f&&!transitionRetargeted)
            {
                LayerTransition.Request(owner,owner.Layout!.LayerAt(-1));transitionRetargeted=true;
                Check("reverse-can-cross-surface-during-blend",LayerTransition.TargetLayer==owner.Layout.LayerAt(-1));
            }
            if(transitionCase==13&&LayerTransition.Progress>.1f)
            {
                LayerView.Switch(owner,0);
                Check("synchronous-switch-cancels-visual-state",!LayerTransition.Active&&owner.ViewedLayer==0);
            }
            if(transitionCase==16&&LayerTransition.Progress>.1f&&!transitionRetargeted)
            {
                transitionRetargeted=true;
                ResolutionUtility.SetResolutionRaw(Screen.width==1280?1024:1280,768,false);
            }
            if(now-transitionRequested>3){Check("transition-timeout-"+transitionCase,false);Finish();}
            return;
        }
        int wanted=transitionCase==4?0:transitionCase==6?-1:transitionElevations[transitionCase];
        Check("transition-final-elevation-"+transitionCase,owner.Elevation(owner.ViewedLayer)==wanted);
        var final=LayerView.CameraRoot;
        Check("transition-preserves-local-view-and-zoom-"+transitionCase,
            Math.Abs(final.z-owner.ViewedLayer*owner.Layout!.Stride-transitionLocalZ)<.01f&&Math.Abs(final.x-transitionRoot.x)<.01f&&Math.Abs(Find.CameraDriver.RootSize-transitionSize)<.01f,$"before={transitionRoot}/{transitionSize},after={final}/{Find.CameraDriver.RootSize}");
        Check("transition-keeps-selected-pawn-"+transitionCase,Find.Selector.SelectedObjectsListForReading.Contains(pawn));
        transitionTiming.Add(new{index=transitionCase,elapsedMilliseconds=(now-transitionRequested)*1000,frames=transitionFrames,
            speed=Find.TickManager.CurTimeSpeed.ToString(),ticks=Find.TickManager.TicksGame-transitionTick,retargeted=transitionRetargeted,elevation=wanted,liveTextures=LayerTransition.LiveTextures});
        if(transitionCase==16)
        {
            Check("actual-window-resize-cancels-and-releases",Screen.height==768&&!LayerTransition.Active&&LayerTransition.LiveTextures==0);
            ResolutionUtility.SetResolutionRaw(ScaleArgument("screen-width",1920,1024,3840),ScaleArgument("screen-height",1080,720,2160),ScaleArgument("screen-height",1080,720,2160)>=1080);
        }
        Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
        transitionCase++;transitionRunning=false;transitionNext=now+.45f;transitionStableCaptures=LayerTransition.SceneCaptures;
    }
    private static bool transitionBudgetRecorded;
    private static void CheckOnceTransitionBudget()
    {
        if(transitionBudgetRecorded||!LayerTransition.HasPicture)return;
        transitionBudgetRecorded=true;Check("transition-uses-at-most-three-textures",LayerTransition.LiveTextures is >0 and <=3);
    }
    private static void TransitionsOnGUI()
    {
        if(scenario!="transition"||stage!=510||Event.current.type!=EventType.Repaint)return;
        if(transitionCase>=transitionElevations.Length&&!transitionKeysDone)
        {
            if(Time.realtimeSinceStartup>=transitionNext&&Screen.width==ScaleArgument("screen-width",1920,1024,3840)&&Screen.height==ScaleArgument("screen-height",1080,720,2160))TransitionKeys();
            return;
        }
        if(!transitionRunning)return;
        if(transitionCase==12&&LayerTransition.Progress>.1f&&!transitionRetargeted)TransitionOrder();
        // Native frame captures include the final GUI. Export only the first two complete directions.
        if(transitionCase<4&&transitionImages<40 || transitionCase>=4&&transitionImages<1&&LayerTransition.Progress>.4f)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(directory,$"{mode}-transition-{transitionCase}-{transitionImages++:D3}.png"));
        }
    }

    private static void TransitionKeys()
    {
        transitionKeysDone=true; // Guard recursive native UIRoot calls.
        var previous=Event.current;
        try
        {
            TransitionButtons();
            LayerView.Switch(owner,0);
            void Key(KeyCode code,bool alt=false)
            {
                Event.current=new Event{type=EventType.KeyDown,rawType=EventType.KeyDown,keyCode=code,alt=alt,modifiers=alt?EventModifiers.Alt:EventModifiers.None};
                ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
                Check("manual-key-consumed-"+code,Event.current.type==EventType.Used);
            }
            Key(KeyCode.F8);
            Check("f8-queues-before-capture",LayerTransition.Active&&LayerTransition.TargetLayer==1&&owner.ViewedLayer==0);
            Key(KeyCode.F8);
            Check("f8-repeat-uses-latest-target",LayerTransition.TargetLayer==2&&owner.ViewedLayer==0);
            Key(KeyCode.F7);Key(KeyCode.F7);
            Check("pre-capture-reversal-cancels",!LayerTransition.Active&&owner.ViewedLayer==0);
            Key(KeyCode.DownArrow,true);
            Check("alt-down-uses-elevation",LayerTransition.TargetLayer==owner.Layout!.LayerAt(-1));
            Event.current=new Event{type=EventType.MouseDrag,button=2,mousePosition=new Vector2(UI.screenWidth/2,UI.screenHeight/2)};
            ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
            Check("native-drag-finishes-pending-before-camera",!LayerTransition.Active&&owner.Elevation(owner.ViewedLayer)==-1);
            Key(KeyCode.UpArrow,true);
            RimLayersMod.Settings.LayerTransitions=false;LayerTransition.ValidateContext();
            Check("mid-transition-setting-off-finishes-target",!LayerTransition.Active&&owner.ViewedLayer==0);
            RimLayersMod.Settings.LayerTransitions=true;
            LayerTransition.Request(owner,1);
            CameraJumper.TryJump(At(2,58,58),map);
            Check("automatic-jump-cancels-manual-animation",!LayerTransition.Active&&owner.ViewedLayer==2);
            LayerTransition.Request(owner,0);
            LayerView.Switch(owner,1);
            Check("public-switch-returns-with-target-applied",owner.ViewedLayer==1&&!LayerTransition.Active);
            Find.Targeter.BeginTargeting(TargetingParameters.ForAttackAny(),_=>{});
            var source=Find.Targeter.targetingSource;
            LayerTransition.Request(owner,0);LayerTransition.FinishForInput();
            Check("targeting-survives-manual-switch",Find.Targeter.IsTargeting&&Find.Targeter.targetingSource==source);
            Find.Targeter.StopTargeting();
        }
        finally {Event.current=previous;LayerTransition.Cancel();}
        TransitionLifetimeChecks();
    }

    private static bool TransitionMouseOnUI(ref Vector2 __result){var point=UI.MapToUIPosition(commandMouse);__result=new Vector2(point.x,UI.screenHeight-point.y);return false;}
    private static void TransitionOrder()
    {
        transitionRetargeted=true;
        var previous=Event.current;
        var hook=new Harmony("rimlayers.diagnostics.transition-order");
        try
        {
            commandGuiBusy=true;commandTrace.Clear();
            hook.Patch(AccessTools.Method(typeof(Event),nameof(Event.Use)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandEventUsed)));
            pawn.jobs.StopAll();pawn.drafter.Drafted=true;
            var cell=At(LayerTransition.TargetLayer,64,60);
            commandMouse=cell.ToVector3Shifted();
            settingsInputBusy=true;settingsInputOutsideClip=false;settingsInputPoint=UI.MapToUIPosition(commandMouse);
            var backend=AccessTools.Property(typeof(Event).Assembly.GetType("UnityStubs.Gui.GuiBackend"),"Current").GetValue(null)!;
            hook.Patch(AccessTools.PropertyGetter(backend.GetType(),"MouseOutsideHitClip"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputClip)));
            hook.Patch(AccessTools.PropertyGetter(typeof(Input),nameof(Input.mousePosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputMouse)));
            hook.Patch(AccessTools.Method(typeof(UI),nameof(UI.MouseMapPosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandMouse)));
            hook.Patch(AccessTools.PropertyGetter(typeof(UI),nameof(UI.MousePositionOnUI)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(TransitionMouseOnUI)));
            Event.current=new Event{type=EventType.MouseDown,rawType=EventType.MouseDown,button=1,clickCount=1,mousePosition=UI.MapToUIPosition(commandMouse)};
            ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
            Check("blend-right-click-submits-native-cross-floor-goto",!LayerTransition.Active&&pawn.CurJob?.def==JobDefOf.Goto&&pawn.CurJob.targetA.Cell==cell,$"job={pawn.CurJob},cell={cell},actualMouse={UI.MouseCell()},event={Event.current.type},active={LayerTransition.Active},canOrder={pawn.CanTakeOrder},selected={Find.Selector.SelectedObjectsListForReading.Contains(pawn)},position={pawn.Position}");
        }
        finally {Log.Message("[RimLayers.TransitionInput] "+string.Join("\n",commandTrace));commandGuiBusy=false;settingsInputBusy=false;hook.UnpatchAll(hook.Id);Event.current=previous;}
    }

    private static void TransitionButtons()
    {
        var hook=new Harmony("rimlayers.diagnostics.transition-buttons");
        var previous=Event.current;
        try
        {
            var backend=AccessTools.Property(typeof(Event).Assembly.GetType("UnityStubs.Gui.GuiBackend"),"Current").GetValue(null)!;
            hook.Patch(AccessTools.PropertyGetter(backend.GetType(),"MouseOutsideHitClip"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputClip)));
            hook.Patch(AccessTools.Method(typeof(Input),nameof(Input.GetMouseButtonDown)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputDown)));
            hook.Patch(AccessTools.Method(typeof(Input),nameof(Input.GetMouseButtonUp)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputUp)));
            hook.Patch(AccessTools.Method(typeof(Input),nameof(Input.GetMouseButton)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputHeld)));
            hook.Patch(AccessTools.PropertyGetter(typeof(Input),nameof(Input.mousePosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputMouse)));
            hook.Patch(AccessTools.PropertyGetter(typeof(UI),nameof(UI.MousePositionOnUI)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputUiMouse)));
            void Click(Vector2 point,Action draw)
            {
                settingsInputBusy=true;settingsInputOutsideClip=false;settingsInputPoint=point;settingsInputPhase=0;
                foreach(var type in new[]{EventType.MouseDown,EventType.MouseUp})
                {
                    settingsInputKind=type;
                    Event.current=new Event{type=type,rawType=type,button=0,clickCount=1,mousePosition=point};
                    draw();
                }
                settingsInputBusy=false;
            }
            LayerView.Switch(owner,0);
            var panel=LayerView.ControlsRect(owner);
            Click(new Vector2(panel.xMax-25,panel.y+22),()=>LayerView.DrawControls(owner));
            Check("native-floor-panel-queues-transition",LayerTransition.Active&&LayerTransition.TargetLayer==1&&owner.ViewedLayer==0);
            LayerTransition.FinishForInput();
            Find.CameraDriver.SetRootPosAndSize(At(1,58,58).ToVector3Shifted(),14);
            Find.CameraDriver.Update();
            var stair=LandingMarkers.Visible(owner).First();
            var point=GenMapUI.LabelDrawPosFor(stair.UpperCell);
            Click(new Vector2(point.x,point.y+21),()=>LandingMarkers.OnGUI(owner));
            owner.TryDecode(stair.Position,out var origin);
            Check("native-stair-return-queues-transition",LayerTransition.Active&&LayerTransition.TargetLayer==origin.LayerId);
            LayerTransition.FinishForInput();
        }
        finally{settingsInputBusy=false;hook.UnpatchAll(hook.Id);Event.current=previous;}
    }

    private static Harmony? transitionFailureHook;
    private static void TransitionCaptureFault() => throw new InvalidOperationException("Injected transition capture failure");
    private static void TransitionLifetimeChecks()
    {
        var turret=(Building_TurretGun)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"),ThingDefOf.Steel),At(1,64,60),map);
        turret.SetFaction(Faction.OfPlayer);LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.Selector.Select(turret);
        Find.Targeter.BeginTargeting(turret.AttackVerb);
        Check("turret-fixture-selected-and-targeting",Find.Targeter.IsTargeting&&Find.Selector.SelectedObjectsListForReading.Contains(turret));
        LayerTransition.Request(owner,0);LayerTransition.FinishForInput();
        Check("turret-selection-and-aim-source-survive-switch",Find.Targeter.IsTargeting&&Find.Targeter.targetingSource==turret.AttackVerb&&Find.Selector.SelectedObjectsListForReading.Contains(turret),$"targeting={Find.Targeter.IsTargeting},source={Find.Targeter.targetingSource},selected={Find.Selector.SelectedObjectsListForReading.Contains(turret)}");
        Find.Targeter.StopTargeting();turret.Destroy(DestroyMode.Vanish);
        var mode=Find.World.renderer.wantedMode;
        LayerTransition.Request(owner,1);
        Find.World.renderer.wantedMode=RimWorld.Planet.WorldRenderMode.Planet;
        LayerTransition.ValidateContext();
        Check("world-view-cancels-pending-transition",!LayerTransition.Active&&LayerTransition.LiveTextures==0);
        Find.World.renderer.wantedMode=mode;
        LayerTransition.Request(owner,1);
        Current.Game.CurrentMap=null;LayerTransition.ValidateContext();
        Check("leaving-current-map-cancels-transition",!LayerTransition.Active);
        Current.Game.CurrentMap=map;
        LayerView.Switch(owner,0);LayerTransition.Request(owner,1);
        var field=AccessTools.Field(typeof(WorldComponent_GravshipController),"cutsceneInProgress");
        var old=field.GetValue(null);
        try
        {
            field.SetValue(null,true);LayerTransition.ValidateContext();
            Check("native-cutscene-flag-cancels-transition",!LayerTransition.Active);
        }
        finally{field.SetValue(null,old);}
        LayerView.Switch(owner,0);
        Find.CameraDriver.SetRootPosAndSize(At(0,58,58).ToVector3Shifted(),14);
        transitionFailureHook=new Harmony("rimlayers.diagnostics.transition-fault");
        transitionFailureHook.Patch(AccessTools.Method(typeof(LayerTransition),"Capture"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(TransitionCaptureFault)));
        LayerTransition.Request(owner,1);
    }
}
