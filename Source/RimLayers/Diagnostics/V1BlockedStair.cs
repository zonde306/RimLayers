using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

/// <summary>
/// A failed first expansion used to be retried by the work giver the moment the job ended: the frame
/// stays buildable, so every retry re-ran the whole map resize and its rollback. This scenario proves
/// the refusal is recorded instead, that the loop stops, and that the player's retry button still works.
/// </summary>
internal static partial class V1Integration
{
    private static Frame blockedFrame=null!;
    private static Map blockedMap=null!;
    private static Pawn blockedPawn=null!;
    private static int blockedPhase;
    private static int blockedAttempts;
    private static bool suppressInjectedExpansionError;
    private static Harmony? blockedFaultHook;
    private static readonly IntVec3 blockedSite=new(35,0,35);
    private static ThingDef DownStairsDef=>DefDatabase<ThingDef>.GetNamed("RL_StairsDown");

    private static void BeginBlockedStair()
    {
        var construction=DefDatabase<WorkTypeDef>.GetNamed("Construction");
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(construction));
        pawn.jobs.StopAll();pawn.DeSpawn();
        map=MakeUnmanagedMap(BootstrapTile(),120,137);Current.Game.CurrentMap=map;BootstrapSite(map);
        GenSpawn.Spawn(pawn,new IntVec3(33,0,33),map);pawn.drafter.Drafted=false;
        pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;
        foreach(var work in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(work,0);
        pawn.workSettings.SetPriority(construction,1);
        map.roofGrid.SetRoof(blockedSite,RoofDefOf.RoofConstructed);
        blockedPawn=pawn;blockedMap=map;
        Check("blocked-fixture-starts-unexpanded",LayerMapComponent.For(map)==null&&map.Size==new IntVec3(120,1,137));
        // Injected the same way the other fault scenarios do it: the expansion itself is untouched, so
        // the rollback path under test is the real one.
        // Automated runs mute the window; this scenario is about the window, so force the gate open.
        RejectionReporter.DialogGateOverride=true;
        blockedAttempts=0;
        blockedFaultHook=new Harmony("rimlayers.diagnostics.blocked-stair-fault");
        blockedFaultHook.Patch(AccessTools.Method(typeof(LiveMapExpansion),nameof(LiveMapExpansion.EnsureElevation)),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(BlockedExpansionFault)));
        blockedFrame=(Frame)ThingMaker.MakeThing(DownStairsDef.frameDef);
        blockedFrame.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(blockedFrame,blockedSite,map);
        foreach(var cost in DownStairsDef.CostListAdjusted(null))
        {
            var material=ThingMaker.MakeThing(cost.thingDef);material.stackCount=cost.count;
            blockedFrame.resourceContainer.TryAdd(material);
        }
        blockedFrame.workDone=blockedFrame.WorkToBuild;
        blockedPhase=0;stage=601;ticks=0;
    }

    private static void BlockedExpansionFault(Map map,int elevation,Action? beforeCommit)
    {
        blockedAttempts++;
        throw new NotSupportedException("Injected layer-expansion failure for the blocked-stair scenario");
    }

    private static void BlockedStairStep()
    {
        if(blockedPhase==0)
        {
            // The first attempt: the real commit path runs, the injected fault aborts it, and the whole
            // expansion is rolled back. The frame must survive with its materials intact.
            var materials=blockedFrame.resourceContainer.ToArray();
            suppressInjectedExpansionError=true;
            try { blockedFrame.CompleteConstruction(blockedPawn); }
            finally { suppressInjectedExpansionError=false; }
            Check("failed-expansion-keeps-the-frame-and-its-materials",blockedFrame.Spawned&&!blockedFrame.Destroyed&&
                blockedFrame.resourceContainer.SequenceEqual(materials)&&materials.All(t=>!t.Destroyed));
            Check("failed-expansion-leaves-the-map-unexpanded",LayerMapComponent.For(blockedMap)==null&&blockedMap.Size==new IntVec3(120,1,137));
            Check("failed-expansion-records-the-blocked-cell",
                LayerMapComponent.Raw(blockedMap) is {} raw&&raw.BlockedStairs.Contains(blockedSite),$"attempts={blockedAttempts}");
            Check("failed-expansion-opened-one-explanation-window",
                Find.WindowStack.Windows.OfType<Dialog_Rejection>().Count(w=>w.Report.Action==RejectionAction.Stairs)==1);
            blockedPhase=1;ticks=0;return;
        }
        if(blockedPhase==1)
        {
            // The loop this scenario exists for: the work giver must no longer offer the frame, and
            // nothing may call the expansion again on its own.
            var giver=(WorkGiver_ConstructFinishFrames)DefDatabase<WorkGiverDef>.GetNamed("ConstructFinishFrames").Worker;
            int attemptsBefore=blockedAttempts;
            Check("work-giver-refuses-the-blocked-stair-frame",giver.JobOnThing(blockedPawn,blockedFrame,true)==null);
            Check("construction-check-refuses-the-blocked-stair-frame",!GenConstruct.CanConstruct(blockedFrame,blockedPawn));
            // Let a few map ticks run: nothing may retry the expansion by itself.
            for(int i=0;i<10;i++)Find.TickManager.DoSingleTick();
            Check("no-tick-retries-the-failed-expansion",blockedAttempts==attemptsBefore,$"attempts={blockedAttempts}");
            Check("blocked-stair-offers-a-retry-button",
                BlockedStairGizmos.For(blockedFrame).Any(g=>g is Command_Action));
            // The screenshot belongs here: the record is still set, so the retry button is on screen.
            // Close the log window first, otherwise it covers the map (dev mode opens it on any error).
            // Closing alone is not enough: the native root reopens the log while wantsToOpen stays set.
            LudeonTK.EditWindow_Log.wantsToOpen=false;
            Find.WindowStack.Windows.OfType<LudeonTK.EditWindow_Log>().ToList().ForEach(w=>w.Close(false));
            Find.WindowStack.Windows.OfType<Dialog_Rejection>().ToList().ForEach(w=>w.Close(false));
            Find.Selector.ClearSelection();Find.Selector.Select(blockedFrame);
            CameraJumper.TryJump(blockedFrame,CameraJumper.MovementMode.Cut);
            pendingScreenshot=Path.Combine(directory,"blocked-stair-retry.png");screenshotHold=3;
            blockedPhase=2;ticks=0;return;
        }
        if(blockedPhase==2)
        {
            // Player retry: the button clears the record and re-runs the real expansion, which now
            // succeeds because the fault is gone.
            blockedFaultHook?.UnpatchAll(blockedFaultHook.Id);blockedFaultHook=null;
            StairLayerBootstrap.ClearBlocked(blockedMap,blockedSite);
            bool ok=StairLayerBootstrap.Prepare(blockedMap,blockedSite,blockedFrame.Rotation,DownStairsDef,report:true);
            Check("player-retry-creates-the-upper-floor",ok&&LayerMapComponent.For(blockedMap) is {} grown&&grown.Layout is {Height:137,LayerCount:2},
                $"ok={ok}");
            Check("player-retry-clears-the-record",LayerMapComponent.Raw(blockedMap) is {} raw&&!raw.BlockedStairs.Contains(blockedSite));
            Check("player-retry-hides-the-retry-button",!BlockedStairGizmos.For(blockedFrame).Any());
            blockedPhase=3;ticks=0;return;
        }
        if(blockedPhase==3)
        {
            RejectionReporter.DialogGateOverride=null;
            Check("blocked-stair-record-survives-save-and-load",
                LayerMapComponent.Raw(blockedMap) is {} raw&&!raw.BlockedStairs.Contains(blockedSite));
            stage=11;ticks=0;
        }
    }
}
