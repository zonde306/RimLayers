using System.Text;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>Which player action was refused. Kept coarse: it only drives the report headline.</summary>
public enum RejectionAction
{
    Build,
    Stairs,
    LiftShaft,
    ShipLanding,
    ShipTakeoff,
    Expansion,
}

/// <summary>
/// One refused action, as shown to the player. Built where the mod decides to say no, so the report
/// always carries the same facts: what was refused, why, which layer, and which other mod (if any) is
/// implicated.
/// </summary>
public sealed class RejectionReport
{
    public RejectionAction Action;
    public string Reason = "";
    public string? DefName;
    public string? DefLabel;
    public string? MapName;
    public int? CellX, CellZ;
    public int? LayerElevation;
    public string? CulpritType;
    public string? CulpritField;
    public string? CulpritMod;
    public string? Exception;
    public string? Detail;

    /// <summary>The translated sentence naming what was refused ("创建上层").</summary>
    public string ActionLabel => ("RL_Reject_Action_"+Action).Translate();

    /// <summary>
    /// The mod responsible, if the refusal was caused by another mod rather than by vanilla terrain,
    /// quota or an in-mod rule.
    /// </summary>
    public string? CulpritName
    {
        get
        {
            if(!CulpritMod.NullOrEmpty())return CulpritMod;
            var type=ModOrigin.TypeFromFullName(CulpritType);
            var name=ModOrigin.NameOf(type);
            if(!name.NullOrEmpty())return name;
            return CulpritType;
        }
    }

    /// <summary>Short one-line summary used by the message log and the window subtitle.</summary>
    public string Headline
    {
        get
        {
            var text=new StringBuilder();
            text.Append(ActionLabel);
            if(!DefLabel.NullOrEmpty())text.Append(": ").Append(DefLabel);
            else if(!DefName.NullOrEmpty())text.Append(": ").Append(DefName);
            return text.ToString();
        }
    }

    /// <summary>Stable identity used for de-duplication, so a per-frame refusal does not spam windows.</summary>
    public string Key => $"{Action}|{DefName}|{Reason}|{CulpritType}|{CulpritField}";

    /// <summary>The full text copied to the clipboard and pasted into a bug report.</summary>
    public string BuildClipboardText()
    {
        var text=new StringBuilder();
        text.AppendLine("=== RimLayers rejection report ===");
        text.AppendLine("Mod: RimLayers " + (ModInfo.Version ?? "?") + " (" + ModInfo.PackageId + ")");
        text.AppendLine("Game: RimWorld " + VersionControl.CurrentVersionStringWithRev);
        text.AppendLine();
        text.AppendLine("Action: " + ActionLabel);
        text.AppendLine("Reason: " + Reason);
        if(!DefLabel.NullOrEmpty()||!DefName.NullOrEmpty())
            text.AppendLine("Definition: " + (DefLabel.NullOrEmpty()?DefName:DefLabel + " (" + DefName + ")"));
        if(!MapName.NullOrEmpty())text.AppendLine("Map: " + MapName);
        if(CellX.HasValue&&CellZ.HasValue)text.AppendLine("Cell: " + CellX.Value + "," + CellZ.Value);
        if(LayerElevation.HasValue)text.AppendLine("Layer elevation: " + LayerElevation.Value);
        if(!CulpritName.NullOrEmpty())
        {
            // Only a real mod name belongs on this line; an unresolved type is reported as a type so
            // a maintainer is not sent looking for a mod that does not exist.
            text.AppendLine((CulpritMod.NullOrEmpty()?"Involved type (no mod matched): ":"Involved mod: ")+CulpritName);
            if(!CulpritMod.NullOrEmpty()&&!CulpritType.NullOrEmpty())text.AppendLine("Involved type: " + CulpritType);
            if(!CulpritField.NullOrEmpty())text.AppendLine("Involved member: " + CulpritField);
        }
        if(!Detail.NullOrEmpty())text.AppendLine("Detail: " + Detail);
        if(!Exception.NullOrEmpty())
        {
            text.AppendLine();
            text.AppendLine("--- Exception ---");
            text.AppendLine(Exception);
        }
        // The full mod list is the single most useful thing for tracking a conflict down, and the
        // player cannot reasonably type it out by hand.
        string mods=ModInfo.ActiveModsList();
        if(!mods.NullOrEmpty())
        {
            text.AppendLine();
            text.AppendLine("--- Active mods (" + ModInfo.ActiveModCount() + ") ---");
            text.Append(mods);
        }
        return text.ToString();
    }
}
