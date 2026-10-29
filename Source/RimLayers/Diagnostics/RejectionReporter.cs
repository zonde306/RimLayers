using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Central entry point for "RimLayers refused to do this" feedback. Every refusal point reports here
/// instead of only printing a one-line red message, so the player gets a readable explanation, the
/// mod responsible when there is one, and text they can paste into a bug report.
/// </summary>
public static class RejectionReporter
{
    /// <summary>Ticks between two dialogs for the same refusal (~4 seconds at normal speed).</summary>
    private const int DialogCooldownTicks = 250;

    private static readonly Dictionary<string, int> lastDialogTick = new();
    private static bool? automatedRun;

    /// <summary>
    /// Diagnostics only: force the dialog gate on or off. The acceptance scenarios pass test command
    /// line switches that would otherwise silence every window, so they set this to exercise the real
    /// window instead of a stub.
    /// </summary>
    internal static bool? DialogGateOverride;

    /// <summary>
    /// True while a real left-click is being processed. Blueprint validation is also polled every frame
    /// for the placement ghost, so only a click may open a window.
    /// </summary>
    internal static bool PlayerClickInProgress =>
        Event.current != null && Event.current.type == EventType.mouseDown && Event.current.button == 0;

    private static bool DialogsAllowed =>
        RimLayersMod.Settings.RejectionDialogs
        && Current.Game != null
        && !DialogsDisabledForTests;

    private static bool DialogsDisabledForTests
    {
        get
        {
            if(DialogGateOverride.HasValue)return !DialogGateOverride.Value;
            automatedRun??=
                GenCommandLine.CommandLineArgPassed("rimlayersv1test")
                || GenCommandLine.CommandLineArgPassed("quicktest")
                || GenCommandLine.CommandLineArgPassed("rimlayerstest");
            return automatedRun.Value;
        }
    }

    /// <summary>Layer elevation of a cell, or null when the map has no layer component or the cell is outside.</summary>
    public static int? ElevationAt(Map? map, IntVec3 cell)
    {
        if(map==null||!cell.IsValid)return null;
        var owner=LayerMapComponent.For(map);
        return owner!=null&&owner.TryDecode(cell,out var at)?owner.Elevation(at.LayerId):null;
    }

    /// <summary>Report a refusal caused by a plain acceptance check (no exception involved).</summary>
    public static void Reject(RejectionAction action, string reason, bool dialog, string? defName = null,
        string? defLabel = null, Map? map = null, IntVec3? cell = null, int? elevation = null, string? detail = null)
    {
        var report=new RejectionReport
        {
            Action=action,
            Reason=reason ?? "",
            DefName=defName,
            DefLabel=defLabel,
            MapName=map?.Parent?.LabelCap,
            CellX=cell?.x,
            CellZ=cell?.z,
            LayerElevation=elevation,
            Detail=detail,
        };
        Submit(report,dialog);
    }

    /// <summary>
    /// Report a refusal caused by an exception. Most layer-expansion failures are raised by
    /// <see cref="Generation.LiveMapExpansion"/> after it rolled the map back, so the exception text is
    /// the only place the real cause survives.
    /// </summary>
    public static void Reject(RejectionAction action, Exception exception, bool dialog, string? reason = null,
        string? defName = null, string? defLabel = null, Map? map = null, IntVec3? cell = null, int? elevation = null)
    {
        var report=new RejectionReport
        {
            Action=action,
            Reason=reason.NullOrEmpty()?exception.Message:(reason??""),
            DefName=defName,
            DefLabel=defLabel,
            MapName=map?.Parent?.LabelCap,
            CellX=cell?.x,
            CellZ=cell?.z,
            LayerElevation=elevation,
            Exception=exception.ToString(),
        };
        Blame(report,exception);
        Submit(report,dialog);
    }

    /// <summary>
    /// Detect the well-known "another mod added a per-cell buffer we do not know how to grow" failure
    /// and attribute it to that mod.
    /// </summary>
    private static void Blame(RejectionReport report, Exception exception)
    {
        const string marker="Unmapped native per-cell buffer: ";
        string? text=exception.Message;
        if(text==null)return;
        int at=text.IndexOf(marker,StringComparison.Ordinal);
        if(at<0)return;
        // The path is followed by ". Expansion was not applied."; the member path itself never contains a space.
        string tail=text[(at+marker.Length)..];
        int end=tail.IndexOf(' ');
        string path=(end<0?tail:tail[..end]).TrimEnd('.');
        if(!ModOrigin.TrySplitMemberPath(path,out string typeName,out string fieldName))return;
        report.CulpritType=typeName;
        report.CulpritField=fieldName;
        report.CulpritMod=ModOrigin.NameOf(ModOrigin.TypeFromFullName(typeName));
    }

    /// <summary>Log the refusal and, when allowed, show the dialog.</summary>
    public static void Submit(RejectionReport report, bool dialog)
    {
        if(report==null)return;
        Log.Warning("[RimLayers] Rejected "+report.ActionLabel+" ("+report.Action+"): "+report.Reason
            +(report.CulpritName.NullOrEmpty()?"":" ["+(report.CulpritMod.NullOrEmpty()?"unattributed type: ":"involved mod: ")+report.CulpritName+"]"));
        if(!dialog||!DialogsAllowed)return;
        // A window for this refusal is already on screen: never stack a second copy on top of it.
        if(Find.WindowStack.WindowOfType<Dialog_Rejection>() is {} open&&open.Report.Key==report.Key)return;
        int now=GenTicks.TicksGame;
        // A tick counter that went backwards means a different game session: old cooldowns are void.
        if(lastDialogTick.TryGetValue(report.Key,out int last)&&now>=last&&now-last<DialogCooldownTicks)return;
        lastDialogTick[report.Key]=now;
        if(lastDialogTick.Count>64)PruneCooldowns(now);
        Find.WindowStack.Add(new Dialog_Rejection(report));
    }

    private static void PruneCooldowns(int now)
    {
        var stale=new List<string>();
        foreach(var pair in lastDialogTick)
            if(now-pair.Value>=DialogCooldownTicks||now<pair.Value)stale.Add(pair.Key);
        foreach(var key in stale)lastDialogTick.Remove(key);
    }
}
