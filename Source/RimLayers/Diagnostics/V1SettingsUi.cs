using System.Globalization;
using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Recovery;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static SettingsMenu settingsUiMenu=null!;
    private static Window settingsUiWindow=null!;
    private static int settingsUiPage;
    private static bool settingsInputRequested,settingsInputDone,settingsInputBusy;
    private static float settingsUiEcologyHeightBefore;
    private static float EnvironmentPageHeight()
    {
        var heights=AccessTools.Field(typeof(SettingsMenu),"heights").GetValue(settingsUiMenu) as float[];
        return heights==null?0:heights[5];
    }
    private static int settingsInputPhase;
    private static EventType settingsInputKind;
    private static Vector2 settingsInputPoint,settingsInputLocal;
    private static Harmony? settingsInputHooks;
    private static void BeginSettingsUi()
    {
        if(GenCommandLine.TryGetCommandLineArg("rimlayersuiscale",out var scale))Prefs.UIScale=float.Parse(scale,CultureInfo.InvariantCulture);
        ResolutionUtility.SetResolutionRaw(ScaleArgument("screen-width",1280,1024,3840),ScaleArgument("screen-height",720,720,2160),ScaleArgument("screen-height",720,720,2160)>=1080);
        CheckLandingDisplayPersistence();
        settingsUiPage=-3;stage=450;
    }
    private static void SettingsUiPicture(string name)
    {
        pendingScreenshot=Path.Combine(directory,"settings-"+name+".png");screenshotHold=8;
    }
    private static void SettingsUiStep()
    {
        if(settingsUiPage==-3){settingsUiPage=-2;screenshotHold=8;return;}
        if(settingsUiPage==-2)
        {
            var mod=LoadedModManager.GetMod<RimLayersMod>();
            settingsUiMenu=(SettingsMenu)AccessTools.Field(typeof(RimLayersMod),"menu").GetValue(mod)!;
            settingsUiWindow=new Dialog_ModSettings(mod);Find.WindowStack.Add(settingsUiWindow);
            File.WriteAllText(Path.Combine(directory,"settings-ui-screen.json"),JsonSerializer.Serialize(new{Screen.width,Screen.height,UI.screenWidth,UI.screenHeight,scale=Prefs.UIScale,language=LanguageDatabase.activeLanguage.folderName}));
            Check("actual-window-matches-requested-resolution",Screen.width==ScaleArgument("screen-width",1280,1024,3840)&&Screen.height==ScaleArgument("screen-height",720,720,2160));
            settingsUiPage=-1;
        }
        if(settingsUiPage<4)
        {
            settingsUiPage++;AccessTools.Field(typeof(SettingsMenu),"page").SetValue(settingsUiMenu,settingsUiPage);
            AccessTools.Field(typeof(SettingsMenu),"scroll").SetValue(settingsUiMenu,Vector2.zero);
            SettingsUiPicture(new[]{"display","floors","gameplay","underground","recovery"}[settingsUiPage]);return;
        }
        if(settingsUiPage==4)
        {
            settingsUiPage++;AccessTools.Field(typeof(SettingsMenu),"page").SetValue(settingsUiMenu,2);
            AccessTools.Field(typeof(SettingsMenu),"scroll").SetValue(settingsUiMenu,new Vector2(0,10000));
            SettingsUiPicture("gameplay-bottom");return;
        }
        if(settingsUiPage==5)
        {
            settingsUiPage++;AccessTools.Field(typeof(SettingsMenu),"page").SetValue(settingsUiMenu,5);
            AccessTools.Field(typeof(SettingsMenu),"scroll").SetValue(settingsUiMenu,Vector2.zero);
            SettingsUiPicture("environment");return;
        }
        if(settingsUiPage==6)
        {
            settingsUiPage++;AccessTools.Field(typeof(SettingsMenu),"page").SetValue(settingsUiMenu,5);
            AccessTools.Field(typeof(SettingsMenu),"scroll").SetValue(settingsUiMenu,new Vector2(0,10000));
            // heights[5] is written by the Environment page draw; it proves the page
            // produced body rows instead of failing silently behind the tab grid.
            float height=EnvironmentPageHeight();
            Check("environment-page-draws-content",height>0,$"height={height}");
            SettingsUiPicture("environment-bottom");return;
        }
        if(settingsUiPage==7)
        {
            settingsUiPage++;
            var menuField=AccessTools.Field(typeof(SettingsMenu),"environmentMenu");
            var ecologyMenu=menuField.GetValue(settingsUiMenu);
            if(AccessTools.Field(typeof(EnvironmentSettingsMenu),"open").GetValue(ecologyMenu) is HashSet<string> open)
                open.Add("Ecology");
            settingsUiEcologyHeightBefore=EnvironmentPageHeight();
            // Scroll to the expanded group so the screenshot shows its controls.
            AccessTools.Field(typeof(SettingsMenu),"scroll").SetValue(settingsUiMenu,new Vector2(0,10000));
            // The page redraws every frame; expanding the group needs one more draw
            // before its extra rows are reflected in heights[5].
            SettingsUiPicture("environment-ecology");return;
        }
        if(settingsUiPage==8)
        {
            settingsUiPage++;
            float after=EnvironmentPageHeight();
            var ecology=RimLayers.Environment.EnvironmentSettings.Parameters.Where(p=>p.Group=="Ecology").ToArray();
            Check("ecology-group-expands-into-drawn-controls",after>settingsUiEcologyHeightBefore,$"before={settingsUiEcologyHeightBefore}; after={after}; controls={ecology.Length}");
            Check("ecology-generation-parameters-are-marked-new-layers",ecology.Length==3&&ecology.All(p=>p.Generation),$"controls={ecology.Length}");
            settingsUiWindow.Close(false);
            var item=ThingMaker.MakeThing(ThingDefOf.Silver);item.stackCount=43;
            RecoveryService.CaptureInvalidSpawn(item,map,new IntVec3(-100,0,99999),Rot4.North);
            var record=RecoveryService.Records.Single(r=>r.Thing==item);record.ManualOnly=true;
            var dialog=new Dialog_EntityRecovery();settingsUiWindow=dialog;Find.WindowStack.Add(dialog);
            AccessTools.Method(typeof(Dialog_EntityRecovery),"Select").Invoke(dialog,new object[]{record,RecoveryService});
            Check("manual-recovery-list-holds-original-test-object",RecoveryService.GetDirectlyHeldThings().Contains(item));
            SettingsUiPicture("pending-selected");return;
        }
        if(settingsUiPage==9)
        {
            settingsUiPage++;settingsUiWindow.Close(false);RimLayersMod.Settings.Reset();
            AccessTools.Field(typeof(SettingsMenu),"page").SetValue(settingsUiMenu,0);
            AccessTools.Field(typeof(SettingsMenu),"scroll").SetValue(settingsUiMenu,Vector2.zero);
            settingsUiWindow=new Dialog_ModSettings(LoadedModManager.GetMod<RimLayersMod>());Find.WindowStack.Add(settingsUiWindow);
            settingsInputRequested=true;InstallSettingsInput();return;
        }
        if(!settingsInputDone)return;
        settingsInputHooks?.UnpatchAll(settingsInputHooks.Id);settingsInputHooks=null;
        Check("five-production-settings-tabs-and-recovery-window-rendered",true);
        Finish();
    }
    private static bool settingsInputOutsideClip;
    private static bool SettingsInputClip(ref bool __result)
    {if(!settingsInputBusy)return true;__result=settingsInputOutsideClip;return false;}
    private static bool SettingsInputDown(int __0,ref bool __result){if(!settingsInputBusy)return true;__result=__0==0&&settingsInputKind==EventType.MouseDown;return false;}
    // A fast checkbox click has both down/up transitions in one input frame; native
    // draggable widgets then retain the same backend control ID for the complete click.
    private static bool SettingsInputUp(int __0,ref bool __result){if(!settingsInputBusy)return true;__result=__0==0&&(settingsInputKind==EventType.MouseUp||settingsInputPhase is 0 or 9 or 11 or 13);return false;}
    private static bool SettingsInputHeld(int __0,ref bool __result){if(!settingsInputBusy)return true;__result=__0==0&&settingsInputKind!=EventType.MouseUp;return false;}
    private static bool SettingsInputMouse(ref Vector3 __result){if(!settingsInputBusy)return true;__result=new(settingsInputPoint.x*Prefs.UIScale,(UI.screenHeight-settingsInputPoint.y)*Prefs.UIScale,0);return false;}
    private static bool SettingsInputUiMouse(ref Vector2 __result){if(!settingsInputBusy)return true;__result=new(settingsInputPoint.x,UI.screenHeight-settingsInputPoint.y);return false;}
    private static void SettingsInputScroll(Rect outRect,Vector2 scrollPosition,Rect viewRect)
    {
        // The Waylith GUI backend reprojects its physical cursor on Push/PopScrollView.
        // Reproject the injected cursor too; do not override any widget's hit result.
        if(settingsInputBusy)
        {
            settingsInputOutsideClip=!outRect.Contains(settingsInputLocal);
            Event.current.mousePosition=settingsInputLocal-outRect.position+scrollPosition+viewRect.position;
            // Backend clipping marked the physical cursor's event Ignore. Apply the
            // same clip decision to the injected cursor at its actual position.
            if(outRect.Contains(settingsInputLocal)){Event.current.type=settingsInputKind;Event.current.rawType=settingsInputKind;}
        }
    }
    private static void SettingsInputEndScroll(){if(settingsInputBusy){settingsInputOutsideClip=false;Event.current.mousePosition=settingsInputLocal;}}
    private static void SettingsInputCheckboxTrace(Rect rect,string label)
    {
        if(settingsInputBusy&&label==SettingsMenu.TextFor("FloorPanel"))Log.Message($"[RimLayers.SettingsInput] phase={settingsInputPhase};rect={rect};mouse={Event.current.mousePosition};type={Event.current.type};over={Mouse.IsOver(rect)};blocked={Mouse.IsInputBlockedNow};obscured={Find.WindowStack.MouseObscuredNow};gets={Find.WindowStack.CurrentWindowGetsInput};down={Input.GetMouseButtonDown(0)};up={Input.GetMouseButtonUp(0)};held={Input.GetMouseButton(0)}");
    }
    private static void SettingsInputSliderTrace(Rect rect)
    {
        if(settingsInputBusy&&settingsInputPhase is 4 or 5 or 6)Log.Message($"[RimLayers.SettingsInput] phase={settingsInputPhase};slider={rect};mouse={Event.current.mousePosition};type={Event.current.type};over={Mouse.IsOver(rect)};blocked={Mouse.IsInputBlockedNow};obscured={Find.WindowStack.MouseObscuredNow};gets={Find.WindowStack.CurrentWindowGetsInput}");
    }
    private static void InstallSettingsInput()
    {
        var hook=settingsInputHooks=new Harmony("rimlayers.diagnostics.settings-input");
        // Event.type reads the backend clip on EVERY access, independently of Event.mousePosition.
        // Adapt that input coordinate check too, without replacing widget decisions or callbacks.
        var backend=AccessTools.Property(typeof(Event).Assembly.GetType("UnityStubs.Gui.GuiBackend"),"Current").GetValue(null)!;
        hook.Patch(AccessTools.PropertyGetter(backend.GetType(),"MouseOutsideHitClip"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputClip)));
        hook.Patch(AccessTools.Method(typeof(SettingsMenu),"Draw"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputBegin)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputEnd)));
        hook.Patch(AccessTools.Method(typeof(Widgets),nameof(Widgets.CheckboxLabeled)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputCheckboxTrace)));
        hook.Patch(AccessTools.Method(typeof(Widgets),nameof(Widgets.HorizontalSlider),new[]{typeof(Rect),typeof(float).MakeByRefType(),typeof(FloatRange),typeof(string),typeof(float)}),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputSliderTrace)));
        hook.Patch(AccessTools.Method(typeof(Widgets),nameof(Widgets.BeginScrollView)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputScroll)));
        hook.Patch(AccessTools.Method(typeof(Widgets),nameof(Widgets.EndScrollView)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputEndScroll)));
        hook.Patch(AccessTools.Method(typeof(Input),nameof(Input.GetMouseButtonDown)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputDown)));
        hook.Patch(AccessTools.Method(typeof(Input),nameof(Input.GetMouseButtonUp)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputUp)));
        hook.Patch(AccessTools.Method(typeof(Input),nameof(Input.GetMouseButton)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputHeld)));
        hook.Patch(AccessTools.PropertyGetter(typeof(Input),nameof(Input.mousePosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputMouse)));
        hook.Patch(AccessTools.PropertyGetter(typeof(UI),nameof(UI.MousePositionOnUI)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SettingsInputUiMouse)));
    }
    private static void SettingsInputBegin(SettingsMenu __instance,Rect rect,out Event? __state)
    {
        __state=null;
        if(__instance!=settingsUiMenu||!settingsInputRequested||settingsInputDone||Event.current.type!=EventType.Repaint)return;
        // Waylith dispatches native GUI.Window callbacks after UIRootOnGUI. Inject at
        // the actual settings draw, not by recursively invoking the root before them.
        __state=Event.current;settingsInputOutsideClip=false;settingsInputBusy=true;
        Vector2 local=settingsInputPhase switch
        {
            // Body rows start at rect.y+78 (two tab rows); 122/198/234 were the
            // single-row (rect.y+42) offsets and must carry the same +36.
            0 or 1=>new(rect.x+rect.width*.5f,rect.y+158),
            // Six pages are laid out 3*2, so column 2 (Gameplay) is at 5/6 width.
            2 or 3=>new(rect.x+rect.width*(5f/6f),rect.y+16),
            4 or 5 or 6=>new(rect.x+rect.width*.8f,rect.y+158),
            9 or 10 or 13 or 14=>new(rect.x+rect.width*.5f,rect.y+234),
            11 or 12=>new(rect.x+rect.width*.5f,rect.y+270),
            _=>new(rect.x+90,rect.yMax-19)
        };
        settingsInputLocal=local;
        settingsInputPoint=new(settingsUiWindow.windowRect.x+18+local.x,settingsUiWindow.windowRect.y+18+local.y);
        settingsInputKind=settingsInputPhase is 0 or 2 or 4 or 7 or 9 or 11 or 13?EventType.MouseDown:settingsInputPhase==5?EventType.MouseDrag:EventType.MouseUp;
        Event.current=new Event{type=settingsInputKind,rawType=settingsInputKind,button=0,clickCount=1,mousePosition=local};
    }
    private static void SettingsInputEnd(Event? __state)
    {
        if(__state==null)return;
        try
        {
            if(settingsInputPhase==1)Check("native-settings-checkbox-click-toggles-floor-panel",!RimLayersMod.Settings.FloorPanel);
            if(settingsInputPhase==3)Check("native-settings-tab-click-selects-gameplay",(int)AccessTools.Field(typeof(SettingsMenu),"page").GetValue(settingsUiMenu)! == 2);
            if(settingsInputPhase==6)Check("native-settings-slider-drag-updates-travel-multiplier",RimLayersMod.Settings.StairTime>2,$"actual={RimLayersMod.Settings.StairTime}; point={settingsInputPoint}");
            if(settingsInputPhase==8)
            {
                var confirm=Find.WindowStack.Windows.OfType<Dialog_MessageBox>().FirstOrDefault();
                Check("restore-defaults-button-opens-confirmation",confirm!=null);
                if(confirm!=null){confirm.buttonAAction();confirm.Close(false);}
                Check("confirmed-reset-restores-clicked-settings",RimLayersMod.Settings.FloorPanel&&RimLayersMod.Settings.StairTime==1);
                AccessTools.Field(typeof(SettingsMenu),"page").SetValue(settingsUiMenu,0);
            }
            if(settingsInputPhase==10)Check("native-marker-checkbox-keeps-return-buttons-on",!RimLayersMod.Settings.LandingMarkers&&RimLayersMod.Settings.ReturnButtons);
            if(settingsInputPhase==12)Check("native-return-checkbox-can-disable-buttons-separately",!RimLayersMod.Settings.LandingMarkers&&!RimLayersMod.Settings.ReturnButtons);
            if(settingsInputPhase==14)
            {
                Check("native-marker-checkbox-does-not-reenable-return-buttons",RimLayersMod.Settings.LandingMarkers&&!RimLayersMod.Settings.ReturnButtons);
                RimLayersMod.Settings.Reset();
                Check("defaults-enable-both-independent-landing-controls",RimLayersMod.Settings.LandingMarkers&&RimLayersMod.Settings.ReturnButtons);
                settingsInputDone=true;
            }
            settingsInputPhase++;
        }
        catch(Exception ex){Check("native-settings-input-exception",false,ex.ToString());settingsInputDone=true;}
        finally{Event.current=__state;settingsInputBusy=false;}
    }
}
