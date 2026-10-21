using HarmonyLib;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Building reinstallSource = null!;
    private static Blueprint_Install? reinstallBlueprint;
    private static bool reinstallGuiBusy, reinstallInputDone;
    private static IntVec3 reinstallMouse;
    private static int reinstallPhase;
    private static bool ReinstallMouse(ref IntVec3 __result) { __result=reinstallMouse;return false; }

    private static void BeginReinstall()
    {
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;
        foreach(var work in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(work,0);
        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction,1);
        pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        reinstallSource=(Building)ThingMaker.MakeThing(ThingDefOf.Stool,ThingDefOf.WoodLog);
        reinstallSource.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(reinstallSource,At(0,57,54),map);
        foreach(int layer in new[]{0,1,2})
            foreach(var cell in new CellRect(50,50,15,15))
            {map.fogGrid.Unfog(At(layer,cell.x,cell.z));map.areaManager.Home[At(layer,cell.x,cell.z)]=true;}
        LayerView.Switch(owner,0);
        Find.CameraDriver.SetRootPosAndSize(At(0,58,58).ToVector3Shifted(),14);
        stage=520;ticks=0;
    }

    private static void ReinstallEvent(EventType type, int button=0, KeyCode key=KeyCode.None)
    {
        Event.current=new Event{type=type,rawType=type,button=button,keyCode=key,mousePosition=CommandScreenPoint};
        ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
    }

    private static Designator_Install SelectInstall(Thing source)
    {
        Find.DesignatorManager.Deselect();
        LayerView.ShowTarget(source.Position, source.Map);
        Find.Selector.ClearSelection();Find.Selector.Select(source);
        var install=source.GetGizmos().OfType<Designator_Install>().First();
        install.ProcessInput(new Event{type=EventType.MouseDown,button=0});
        return install;
    }

    private static void ReinstallOnGUI()
    {
        if(scenario!="reinstall"||stage!=520||reinstallGuiBusy||reinstallInputDone||Event.current.type!=EventType.Repaint)return;
        reinstallGuiBusy=true;
        var previous=Event.current;
        var hook=new Harmony("rimlayers.diagnostics.reinstall-mouse");
        try
        {
            hook.Patch(AccessTools.Method(typeof(UI),nameof(UI.MouseCell)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(ReinstallMouse)));
            hook.Patch(AccessTools.PropertyGetter(typeof(UI),nameof(UI.MousePositionOnUI)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandMouseOnUI)));
            reinstallMouse=At(owner.ViewedLayer,58,54);
            RimLayersMod.Settings.LayerTransitions=false;
            if(reinstallPhase==0)
            {
                // Native construction tool must survive, but its old drag must not cross storage bands.
                Find.Selector.ClearSelection();Find.Selector.Select(reinstallSource);
                var build=new Designator_Build(ThingDefOf.Wall);
                build.SetStuffDef(ThingDefOf.WoodLog);
                Find.DesignatorManager.Select(build);
                Find.DesignatorManager.Dragger.StartDrag();
                Check("build-fixture-started-drag",Find.DesignatorManager.Dragger.Dragging);
                ReinstallEvent(EventType.KeyDown,key:KeyCode.F8);
                Check("build-tool-retained-and-old-drag-ended",owner.ViewedLayer==1&&Find.DesignatorManager.SelectedDesignator==build&&!Find.DesignatorManager.Dragger.Dragging);
                Check("ordinary-building-selection-still-cleared",!Find.Selector.SelectedObjectsListForReading.Contains(reinstallSource));
                reinstallMouse=At(1,58,54);
                ReinstallEvent(EventType.MouseDown,1);
                Check("right-click-still-cancels-build-tool",Find.DesignatorManager.SelectedDesignator==null);

                // Minified installation uses the same selection-owned source as reinstallation.
                var mini=ThingMaker.MakeThing(ThingDefOf.Stool,ThingDefOf.WoodLog).MakeMinified();
                mini.InnerThing.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(mini,At(1,59,54),map);
                var miniInstall=SelectInstall(mini);
                ReinstallEvent(EventType.KeyDown,key:KeyCode.F7);
                Check("minified-source-and-install-tool-retained-downstairs",owner.ViewedLayer==0&&Find.Selector.SingleSelectedThing==mini&&Find.DesignatorManager.SelectedDesignator==miniInstall&&miniInstall.CanRemainSelected(),$"layer={owner.ViewedLayer},selected={Find.Selector.SingleSelectedThing},tool={Find.DesignatorManager.SelectedDesignator},event={Event.current.type}");
                reinstallMouse=At(0,58,54);
                ReinstallEvent(EventType.MouseDown,1);
                Check("right-click-still-cancels-install-tool",Find.DesignatorManager.SelectedDesignator==null);
                Find.Selector.ClearSelection();mini.Destroy(DestroyMode.Vanish);
            }
            var install=SelectInstall(reinstallSource);
            int target=reinstallPhase==0?1:0;
            ReinstallEvent(EventType.KeyDown,key:reinstallPhase==0?KeyCode.F8:KeyCode.F7);
            Check("native-key-keeps-reinstall-source-"+reinstallPhase,owner.ViewedLayer==target&&Find.Selector.SingleSelectedThing==reinstallSource&&Find.DesignatorManager.SelectedDesignator==install&&install.CanRemainSelected());
            if(reinstallPhase==0)
            {
                // The animated route commits through the same switch; exercise its input fast-forward too.
                RimLayersMod.Settings.LayerTransitions=true;
                LayerTransition.Request(owner,2);
                LayerTransition.FinishForInput();
                Check("transition-commit-retains-install-source",owner.ViewedLayer==2&&Find.Selector.SingleSelectedThing==reinstallSource&&Find.DesignatorManager.SelectedDesignator==install);
                LayerView.Switch(owner,1);
            }
            reinstallMouse=At(target,58,54);
            var allowed=install.CanDesignateCell(reinstallMouse);
            Check("destination-accepts-reinstall-"+reinstallPhase,allowed.Accepted,allowed.Reason);
            if(!allowed.Accepted){Finish();return;}
            ReinstallEvent(EventType.MouseDown);
            reinstallBlueprint=reinstallMouse.GetThingList(map).OfType<Blueprint_Install>().SingleOrDefault();
            Check("native-click-creates-linked-install-blueprint-"+reinstallPhase,reinstallBlueprint?.MiniToInstallOrBuildingToReinstall==reinstallSource);
            Check("native-placement-finishes-tool-"+reinstallPhase,Find.DesignatorManager.SelectedDesignator==null);
            if(reinstallBlueprint==null){Finish();return;}
            destination=reinstallMouse;
            pendingScreenshot=Path.Combine(directory,"reinstall-blueprint-"+reinstallPhase+".png");screenshotHold=3;
            reinstallInputDone=true;ticks=0;pawn.drafter.Drafted=false;
        }
        catch(Exception error){Check("reinstall-input-exception",false,error.ToString());Finish();}
        finally{hook.UnpatchAll(hook.Id);Event.current=previous;reinstallGuiBusy=false;}
    }

    private static void ReinstallStep()
    {
        if(!reinstallInputDone)return;
        ticks++;
        if(reinstallSource.Spawned&&reinstallSource.Position==destination)
        {
            Check("native-builder-moves-original-building-"+reinstallPhase,!reinstallSource.Destroyed&&reinstallBlueprint!.Destroyed,$"id={reinstallSource.ThingID},ticks={ticks}");
            Check("reinstall-does-not-duplicate-building-"+reinstallPhase,map.listerThings.ThingsOfDef(ThingDefOf.Stool).Count(t=>t==reinstallSource)==1);
            pawn.jobs.StopAll();pawn.drafter.Drafted=true;
            if(reinstallPhase++==0){reinstallInputDone=false;ticks=0;}
            else
            {
                pendingScreenshot=Path.Combine(directory,"reinstall-completed.png");screenshotHold=3;
                stage=11;ticks=0;
            }
        }
        else if(ticks>12000){Check("native-builder-completes-reinstall-"+reinstallPhase,false,DescribePawn());Finish();}
    }
}
