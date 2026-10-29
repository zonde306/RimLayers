using System.IO;
using System.Text.Json;
using RimLayers.Core;
using RimLayers.Structure;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Drives the real refusal path and the real window, so the report a player would paste is verified
/// instead of assumed. The scenario runs with the dialog gate forced open: the v1 test switches would
/// otherwise silence every window by design.
/// </summary>
internal static partial class V1Integration
{
    private static Dialog_Rejection? rejectionDialog;
    private static string rejectionCopy = "";
    private static RejectionReport? rejectionReport;
    private static int rejectionStage;
    private static int rejectionTicks;
    private static string rejectionLastCopy = "";
    private static Building_Elevator? rejectionShaft;
    private static Pawn? rejectionPawn;

    /// <summary>The path the real map-expansion failure uses when an unknown native buffer appears.</summary>
    private const string UnmappedBufferPath = "RimLayers.Diagnostics.V1Rejection.NativeBuffers";

    private static void BeginRejectionDialog()
    {
        RejectionReporter.DialogGateOverride = true;
        foreach(var p in map.mapPawns.FreeColonistsSpawned)
        {
            p.jobs.StopAll();
            p.drafter.Drafted=true;
            p.Position=new IntVec3(30,0,30);
            p.Notify_Teleported();
        }
        foreach(var c in new CellRect(45,45,32,32))
        {
            foreach(var t in c.GetThingList(map).ToList())
                if(t is not Pawn)
                {
                    if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);
                    else t.DeSpawn();
                }
            map.roofGrid.SetRoof(c,null);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Soil);
            map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        rejectionPawn=map.mapPawns.FreeColonistsSpawned.First();
        rejectionShaft=(Building_Elevator)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_LiftVertical"));
        rejectionShaft.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(rejectionShaft,new IntVec3(60,0,60),map);
        // The generated fixture map already carries floors, so the only thing this scenario needs is
        // a station that has not started an extension in either direction yet.
        Check("rejection-fixture-starts-with-no-extension",
            !rejectionShaft.Expanding(1)&&!rejectionShaft.Expanding(-1));
        rejectionStage=1;rejectionTicks=0;stage=590;
    }

    private static void RejectionStep()
    {
        rejectionTicks++;
        switch(rejectionStage)
        {
            case 1:
                // The real gate: the shaft reports through StartExtension exactly like a gizmo click,
                // and a roof-less upward extension is a rule refusal with no exception involved.
                if(!rejectionShaft!.StartExtension(1))
                {
                    rejectionStage=2;
                    return;
                }
                Check("rejection-upward-extension-refused-without-roof",false,"StartExtension(1) was accepted");
                Finish();
                return;
            case 2:
                rejectionDialog=Find.WindowStack.WindowOfType<Dialog_Rejection>();
                Check("rejection-window-opened-by-real-refusal",rejectionDialog!=null);
                if(rejectionDialog==null){Finish();return;}
                rejectionReport=rejectionDialog.Report;
                Check("rejection-window-names-the-action",rejectionReport.Action==RejectionAction.LiftShaft,$"action={rejectionReport.Action}");
                Check("rejection-window-carries-the-translated-reason",
                    !rejectionReport.Reason.NullOrEmpty()&&rejectionReport.Reason.Contains("RL_")==false,
                    $"reason={rejectionReport.Reason}");
                Check("rejection-window-keeps-the-definition",rejectionReport.DefName=="RL_LiftVertical",$"def={rejectionReport.DefName}");
                Check("rejection-window-records-the-layer-elevation",rejectionReport.LayerElevation==0,$"elevation={rejectionReport.LayerElevation}");
                Check("rule-refusal-blames-no-other-mod",rejectionReport.CulpritName.NullOrEmpty(),$"culprit={rejectionReport.CulpritName}");
                rejectionCopy=rejectionReport.BuildClipboardText();
                Check("clipboard-text-identifies-rimlayers",rejectionCopy.Contains("rimlayers.core")&&rejectionCopy.Contains("RimLayers"));
                Check("clipboard-text-carries-the-reason",rejectionCopy.Contains("Reason: "+rejectionReport.Reason));
                Check("clipboard-text-lists-active-mods",rejectionCopy.Contains("--- Active mods ("));
                File.WriteAllText(Path.Combine(directory,"rejection-rule-refusal.txt"),rejectionCopy);
                // Click the real copy button. The window is drawn after this update, so the click is
                // re-issued on the next frame from the same screen position.
                pendingScreenshot=Path.Combine(directory,"rejection-rule-refusal.png");
                screenshotHold=6;
                rejectionStage=3;
                return;
            case 3:
                if(screenshotHold>0)return;
                // Same call the button makes, from the same object the player is looking at.
                rejectionDialog!.CopyToClipboard();
                rejectionLastCopy=GUIUtility.systemCopyBuffer??"";
                Check("copy-button-writes-the-report-to-the-clipboard",rejectionLastCopy.Contains("=== RimLayers rejection report ==="),
                    $"clipboardLength={rejectionLastCopy.Length}; head={rejectionLastCopy[..Math.Min(40,rejectionLastCopy.Length)]}");
                rejectionDialog?.Close(false);
                // Now the third-party conflict path: a real expansion failure whose exception names a
                // buffer nobody declared. Attribution must come out of the exception text.
                rejectionReport=null;
                var culprit=new NotSupportedException(
                    "Unmapped native per-cell buffer: "+UnmappedBufferPath+". Expansion was not applied.");
                RejectionReporter.Reject(RejectionAction.Expansion,culprit,true,"RL_StairExpansionFailed".Translate(),
                    "RL_LiftVertical","电梯竖井",map,new IntVec3(60,0,60),0);
                rejectionStage=4;
                return;
            case 4:
                var dialog=Find.WindowStack.WindowOfType<Dialog_Rejection>();
                Check("expansion-failure-opens-its-own-window",dialog!=null&&ReferenceEquals(dialog,rejectionDialog)==false);
                if(dialog==null){Finish();return;}
                rejectionDialog=dialog;
                var blamed=dialog.Report;
                Check("expansion-failure-keeps-the-exception-text",!blamed.Exception.NullOrEmpty());
                Check("expansion-failure-splits-the-member-path",
                    blamed.CulpritType==UnmappedBufferPath[..UnmappedBufferPath.LastIndexOf('.')]
                    &&blamed.CulpritField=="NativeBuffers",
                    $"type={blamed.CulpritType}; field={blamed.CulpritField}");
                Check("unknown-type-falls-back-to-the-type-name-instead-of-a-wrong-mod",
                    blamed.CulpritName==blamed.CulpritType,$"culprit={blamed.CulpritName}");
                // The window sizes itself from a measured height; the exception block must fit in it
                // instead of being cut off by the scroll view's bottom edge.
                Check("window-height-covers-the-drawn-body",
                    dialog.ContentHeight<=dialog.ViewportHeight,
                    $"content={dialog.ContentHeight}; viewport={dialog.ViewportHeight}");
                var text=blamed.BuildClipboardText();
                Check("clipboard-text-carries-the-involved-type",
                    text.Contains("Involved type (no mod matched): "+blamed.CulpritType));
                File.WriteAllText(Path.Combine(directory,"rejection-third-party.txt"),text);
                pendingScreenshot=Path.Combine(directory,"rejection-third-party.png");
                screenshotHold=6;
                rejectionStage=5;
                return;
            case 5:
                if(screenshotHold>0)return;
                rejectionDialog?.Close(false);
                // A refusal raised again within the cooldown must not stack a second window.
                RejectionReporter.Reject(RejectionAction.LiftShaft,"RL_NeedsRoof".Translate(),true,
                    "RL_LiftVertical","电梯竖井",map,new IntVec3(60,0,60),0);
                rejectionStage=6;rejectionTicks=0;
                return;
            case 6:
                var stacked=Find.WindowStack.Windows.OfType<Dialog_Rejection>().Count();
                Check("one-window-per-refusal",stacked<=1,$"open={stacked}");
                Check("cooldown-suppresses-an-identical-refusal",stacked==0,$"open={stacked}");
                Check("refusal-is-still-written-to-the-log",
                    Log.Messages.Any(m=>m.text!=null&&m.text.Contains("[RimLayers] Rejected")),
                    "no matching log line");
                RejectionReporter.DialogGateOverride=null;
                Finish();
                return;
        }
    }
}
