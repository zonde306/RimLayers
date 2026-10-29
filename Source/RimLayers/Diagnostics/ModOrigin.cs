using System.Reflection;
using RimLayers.Compatibility;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Maps a runtime type or assembly back to the mod that shipped it. A rejection caused by another mod
/// (most often an undeclared native per-cell buffer) then names that mod in the report instead of only
/// printing an internal type name the player cannot act on.
/// </summary>
public static class ModOrigin
{
    private static readonly Dictionary<Assembly, ModContentPack> cache = new();

    /// <summary>The mod that loaded <paramref name="assembly"/>, or null when nothing claims it.</summary>
    public static ModContentPack? PackOf(Assembly? assembly)
    {
        if(assembly==null)return null;
        if(cache.TryGetValue(assembly,out var cached))return cached;
        ModContentPack? found=null;
        var mods=LoadedModManager.RunningModsListForReading;
        if(mods!=null)
            for(int i=0;i<mods.Count&&found==null;i++)
            {
                var loaded=mods[i]?.assemblies?.loadedAssemblies;
                if(loaded==null)continue;
                for(int j=0;j<loaded.Count;j++)
                    if(ReferenceEquals(loaded[j],assembly)){found=mods[i];break;}
            }
        // Only cache hits: a miss before mods finish loading must not poison later lookups.
        if(found!=null)cache[assembly]=found;
        return found;
    }

    /// <summary>
    /// Player-facing name of the mod that owns <paramref name="type"/>. Prefers an explicit
    /// <see cref="LayerCellBufferCompatibility"/> registration, because that name is chosen by the
    /// mod author rather than derived from a folder.
    /// </summary>
    public static string? NameOf(Type? type)
    {
        if(type==null)return null;
        var declared=LayerCellBufferCompatibility.DeclaringMod(type);
        if(!declared.NullOrEmpty())return declared;
        return Describe(PackOf(type.Assembly));
    }

    public static string? Describe(ModContentPack? pack)
    {
        if(pack==null)return null;
        string name=pack.Name;
        string id=pack.PackageIdPlayerFacing.NullOrEmpty()?pack.PackageId:pack.PackageIdPlayerFacing;
        return id.NullOrEmpty()?name:$"{name} [{id}]";
    }

    /// <summary>Resolve a type from a full name printed inside an exception message.</summary>
    public static Type? TypeFromFullName(string? fullName)
    {
        if(fullName.NullOrEmpty())return null;
        try { return GenTypes.GetTypeInAnyAssembly(fullName,null); }
        catch { return null; }
    }

    /// <summary>
    /// Split "Namespace.Type+Inner.FieldName" into its declaring type name and field name. Returns
    /// false when the text does not look like a field path.
    /// </summary>
    public static bool TrySplitMemberPath(string? path,out string typeName,out string fieldName)
    {
        typeName="";fieldName="";
        if(path==null||path.Length<3)return false;
        int dot=path.LastIndexOf('.');
        if(dot<=0||dot>=path.Length-1)return false;
        typeName=path[..dot];
        fieldName=path[(dot+1)..];
        return true;
    }
}
