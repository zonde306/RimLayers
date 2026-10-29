using System.Text;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>Version and mod-list facts stamped onto every rejection report.</summary>
public static class ModInfo
{
    private const string FallbackVersion = "0.1.5";

    public static string PackageId => RimLayersMod.SettingsContent?.PackageId ?? "rimlayers.core";

    /// <summary>Mod version as declared in About.xml, falling back to the shipped build number.</summary>
    public static string Version
    {
        get
        {
            var declared=RimLayersMod.SettingsContent?.ModMetaData?.ModVersion;
            return declared.NullOrEmpty()?FallbackVersion:declared!;
        }
    }

    public static string Display => "RimLayers " + Version + " (" + PackageId + ")";

    /// <summary>How many mods are loaded, for the report header.</summary>
    public static int ActiveModCount() => LoadedModManager.RunningModsListForReading?.Count ?? 0;

    /// <summary>
    /// Active mods, one per line with package ids. This is what a maintainer needs to reproduce a
    /// conflict, and the player cannot reasonably type it out themselves.
    /// </summary>
    public static string ActiveModsList()
    {
        var text=new StringBuilder();
        var mods=LoadedModManager.RunningModsListForReading;
        if(mods==null)return "";
        foreach(var mod in mods)
        {
            if(mod==null)continue;
            text.Append("  - ").Append(mod.Name);
            string id=mod.PackageIdPlayerFacing.NullOrEmpty()?mod.PackageId:mod.PackageIdPlayerFacing;
            if(!id.NullOrEmpty())text.Append(" [").Append(id).Append(']');
            text.AppendLine();
        }
        return text.ToString();
    }
}
