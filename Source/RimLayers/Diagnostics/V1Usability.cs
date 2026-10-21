using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn usabilityNanami=null!,backdropTarget=null!;
    private static readonly CellRect slabPlan=new(65,55,9,9);
    private static bool usabilityKeys;
    private static int usabilityImages;
    private static bool UsabilityMouse(ref Vector3 __result)
    {__result=LowerFloorBackdrop.Project(owner,backdropTarget.DrawPos);return false;}
    private static void BeginUsability()
    {
        bool withNanami=ModLister.GetActiveModWithIdentifier("shimakaze.nanami.startrail")!=null;
        if(withNanami)usabilityNanami=map.mapPawns.AllPawnsSpawned.Single(p=>p.def.defName=="NST_Nanami");
        else {usabilityNanami=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);GenSpawn.Spawn(usabilityNanami,At(1,90,90),map);}
        Check(withNanami?"real-nanami-mod-autojoin-has-safe-surface":"native-new-arrival-has-safe-surface",owner.Structure.HasSurface(usabilityNanami.Position),usabilityNanami.Position.ToString());
        var generated=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        GenSpawn.Spawn(generated,At(1,90,90),map);
        Check("generic-invalid-new-spawn-is-repaired-with-same-pawn",generated.Spawned&&owner.Structure.HasSurface(generated.Position)&&map.mapPawns.AllPawnsSpawned.Count(p=>p==generated)==1);
        generated.drafter.Drafted=true;
        var construction=DefDatabase<WorkTypeDef>.GetNamed("Construction");
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>p!=usabilityNanami&&!p.WorkTypeIsDisabled(construction));
        foreach(var p in map.mapPawns.FreeColonistsSpawned){p.jobs.StopAll();p.drafter.Drafted=true;}
        pawn.drafter.Drafted=false;pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;
        foreach(var work in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(work,0);
        pawn.workSettings.SetPriority(construction,1);
        foreach(var cell in slabPlan)
        {
            map.terrainGrid.SetTerrain(At(0,cell.x,cell.z),TerrainDefOf.Concrete);
            map.roofGrid.SetRoof(At(0,cell.x,cell.z),RoofDefOf.RoofConstructed);
        }
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,70,58),map);
        for(int i=0;i<8;i++){var steel=ThingMaker.MakeThing(ThingDefOf.Steel);steel.stackCount=75;GenSpawn.Spawn(steel,At(0,53+i,54),map);}
        foreach(var cell in slabPlan)map.areaManager.Home[At(1,cell.x,cell.z)]=true;
        bool legal=true;
        foreach(var cell in slabPlan)legal&=GenConstruct.CanPlaceBlueprintAt(RimLayersDefOf.RL_StructuralFloor,At(1,cell.x,cell.z),Rot4.North,map).Accepted;
        Check("whole-nine-by-nine-slab-plan-is-placeable-before-any-new-floor",legal);
        var designator=new Designator_Build(RimLayersDefOf.RL_StructuralFloor);
        LayerView.Switch(owner,1);
        designator.DesignateMultiCell(slabPlan.Select(c=>At(1,c.x,c.z)));
        Find.DesignatorManager.Select(designator);
        Check("one-native-rectangle-designation-creates-all-81-blueprints",slabPlan.Count(c=>At(1,c.x,c.z).GetThingList(map).Any(t=>t is Blueprint&&t.def.entityDefToBuild==RimLayersDefOf.RL_StructuralFloor))==81);
        var far=At(1,73,59).GetThingList(map).Single(t=>t is Blueprint);
        Check("future-slab-is-not-yet-constructible-without-real-neighbor",!GenConstruct.CanConstruct(far,pawn));
        LayerView.Switch(owner,1);
        Check("upper-landings-have-two-markers-without-duplicating-stairs",LandingMarkers.Visible(owner).Count()==2&&map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).Count==3);
        backdropTarget=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        GenSpawn.Spawn(backdropTarget,At(0,58,68),map);backdropTarget.drafter.Drafted=true;
        map.fogGrid.Unfog(backdropTarget.Position);
        Current.Game.CurrentMap=map;Find.Selector.ClearSelection();Find.Selector.Select(pawn);LayerView.Switch(owner,1);
        Find.CameraDriver.SetRootPosAndSize(At(1,61,60).ToVector3Shifted(),24);
        stage=280;ticks=0;
    }
    private static void UsabilityKeysOnGUI()
    {
        if(scenario!="usability"||usabilityKeys||stage!=280||Event.current.type!=EventType.Repaint)return;
        usabilityKeys=true;
        var previous=Event.current;
        bool transitions=RimLayersMod.Settings.LayerTransitions;RimLayersMod.Settings.LayerTransitions=false;
        var position=pawn.Position;var job=pawn.CurJob;
        LayerView.Switch(owner,0);
        try
        {
            Event.current=new Event{type=EventType.KeyDown,keyCode=KeyCode.F8};
            ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
            Check("native-ui-keydown-f8-switches-floor-before-camera-input",owner.ViewedLayer==1&&Event.current.type==EventType.Used);
            Event.current=new Event{type=EventType.KeyDown,keyCode=KeyCode.F7};
            ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
            Check("native-ui-keydown-f7-returns-without-moving-pawn-or-job",owner.ViewedLayer==0&&pawn.Position==position&&pawn.CurJob==job);
        }
        finally{Event.current=previous;RimLayersMod.Settings.LayerTransitions=transitions;}
        LayerView.Switch(owner,1);
        Check("background-target-keeps-real-thing-identity",LowerFloorBackdrop.Pick(owner,LowerFloorBackdrop.Project(owner,backdropTarget.DrawPos),.8f,TargetingParameters.ForAttackAny(),null!).Contains(backdropTarget));
        Check("foreground-solid-floor-hides-lower-pawn-from-picking",!LowerFloorBackdrop.Exposed(owner,At(0,54,54)));
        var mouseHook=new Harmony("rimlayers.diagnostics.usability-mouse");
        try
        {
            Find.Targeter.BeginTargeting(TargetingParameters.ForAttackAny(),_=>{});
            mouseHook.Patch(AccessTools.Method(typeof(UI),nameof(UI.MouseMapPosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(UsabilityMouse)));
            var target=(LocalTargetInfo)AccessTools.Method(typeof(Targeter),"CurrentTargetUnderMouse").Invoke(Find.Targeter,new object[]{false})!;
            Check("native-targeter-picks-real-lower-floor-pawn",target.Thing==backdropTarget);
            GenDraw.DrawTargetHighlight(target);
        }
        finally {mouseHook.UnpatchAll(mouseHook.Id);Find.Targeter.StopTargeting();}
        Find.DesignatorManager.Select(new Designator_Build(RimLayersDefOf.RL_StructuralFloor));
        pendingScreenshot=Path.Combine(directory,"usability-planning-backdrop.png");screenshotHold=3;
    }
    private static void UsabilityStep()
    {
        if(!usabilityKeys)return;
        if(usabilityImages==0&&LowerFloorBackdrop.LastMeshes>0)
        {
            Check("real-lower-floor-meshes-and-actor-are-submitted",LowerFloorBackdrop.LastMeshes>0&&LowerFloorBackdrop.LastActors>0,$"meshes={LowerFloorBackdrop.LastMeshes},actors={LowerFloorBackdrop.LastActors}");usabilityImages=1;
        }
        if(slabPlan.All(c=>owner.Structure.HasSurface(At(1,c.x,c.z))))
        {
            Check("native-construction-finishes-whole-batch-from-real-frontier",true,$"ticks={ticks}");
            Check("batch-finishing-leaves-no-frames-or-blueprints",slabPlan.All(c=>!At(1,c.x,c.z).GetThingList(map).Any(t=>t is Blueprint||t is Frame)));
            pawn.drafter.Drafted=true;
            usabilityNanami.jobs.StopAll();usabilityNanami.Position=At(1,90,90);usabilityNanami.Notify_Teleported();
            Check("legacy-bad-air-pawn-fixture-is-real",!owner.Structure.HasSurface(usabilityNanami.Position));
            File.WriteAllText(Path.Combine(directory,"usability-baseline.json"),JsonSerializer.Serialize(new{nanami=usabilityNanami.ThingID,
                equipment=usabilityNanami.equipment.AllEquipmentListForReading.Select(t=>t.ThingID).ToArray()}));
            GameDataSaveLoader.SaveGame("RimLayers_V1Usability");
            // Restore the generation run too, using the same production load-recovery action.
            SpawnSafety.Recover(map);
            Check("recovery-preserves-nanami-instance-and-safe-surface",owner.Structure.HasSurface(usabilityNanami.Position));
            pendingScreenshot=Path.Combine(directory,"usability-built-backdrop.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(ticks>65000){Check("batch-construction-settles",false,$"built={slabPlan.Count(c=>owner.Structure.HasSurface(At(1,c.x,c.z)))}/81;"+DescribePawn());Finish();}
    }
    private static void RestoreUsability()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"usability-baseline.json")));
        usabilityNanami=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==baseline.RootElement.GetProperty("nanami").GetString());
        Check("load-rescues-existing-air-pawn-with-original-id",owner.Structure.HasSurface(usabilityNanami.Position));
        Check("rescue-keeps-real-mod-equipment",usabilityNanami.equipment.AllEquipmentListForReading.Select(t=>t.ThingID).SequenceEqual(baseline.RootElement.GetProperty("equipment").EnumerateArray().Select(t=>t.GetString())));
        Check("all-batch-built-floors-survive-restart",slabPlan.All(c=>owner.Structure.HasSurface(At(1,c.x,c.z))));
        pawn=usabilityNanami;LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,61,60).ToVector3Shifted(),24);
        pendingScreenshot=Path.Combine(directory,"usability-loaded-backdrop.png");screenshotHold=3;stage=11;ticks=0;
    }
}
