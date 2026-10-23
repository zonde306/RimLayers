using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using Unity.Collections;
using UnityEngine;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// VVRace (Vivi Race, packageId <c>gguake.race.vivi</c>) keeps three private per-cell buffers in
/// <c>VVRace.ManaMapComponent</c>: <c>_manaReserveGrid</c>, <c>_manaGrid</c> and <c>_tmpGrid</c>,
/// each a <c>NativeArray&lt;float&gt;</c> allocated with <c>map.cellIndices.NumGridCells</c>.
///
/// <para>Before this adapter existed, map expansion refused to run: the generic scan found an
/// unregistered native per-cell buffer, threw <see cref="NotSupportedException"/>, and the whole
/// transaction rolled back, so a stair could never add a floor while VVRace was active.
/// These buffers are indexed exactly like <see cref="CellIndices"/> (z * width + x) and the
/// expansion only appends rows, so the ground band is a prefix and the appended rows are empty:
/// the buffers can be grown instead of refused.</para>
///
/// <para>VVRace also simulates over the whole storage extent (diffusion job, flower scan). A storage
/// extent now contains several bands plus RimLayers' reserved isolation rows, so this adapter
/// re-establishes the "each band is its own map" contract in the two places that matter:
/// the overlay only paints the viewed band, and the reserved isolation rows are cleared after
/// every diffusion cycle so mana can never tunnel between floors. Band edges then behave exactly
/// like the native map edge (neighbour treated as zero), which is what a standalone map did.</para>
/// </summary>
internal static class VVRaceCompatibility
{
    internal const string PackageId = "gguake.race.vivi";
    private const string TypeName = "VVRace.ManaMapComponent";
    // Resolved lazily: a Mod constructor runs before other mods' assemblies are reachable through
    // TypeByName, so a static readonly lookup here would always see null and silently do nothing.
    private static Type? componentType;
    private static bool resolved;
    private static FieldInfo? jobStarted;
    private static MethodInfo? completeDiffusion, applyAllReserved;
    internal static bool Active => Resolve() && jobStarted != null && completeDiffusion != null && applyAllReserved != null;

    /// <summary>
    /// Declares the buffers and the in-transaction settle hook. Safe to call when VVRace is absent.
    /// Called again on the first game load (see <see cref="InstallWhenReady"/>), because the VVRace
    /// assembly is not loaded yet while RimLayers' own Mod constructor runs.
    /// </summary>
    internal static void Install()
    {
        if (ModLister.GetActiveModWithIdentifier(PackageId, false) == null) return;
        if (!Resolve() || componentType == null) return;
        jobStarted = AccessTools.Field(componentType, "_diffusionJobStart");
        completeDiffusion = AccessTools.Method(componentType, "CompleteDiffuseManaJob");
        applyAllReserved = AccessTools.Method(componentType, "ApplyAllReservedMana");
        if (!Active)
        {
            // The fields moved. Keep the old safe refusal rather than growing a buffer we can no longer settle.
            Log.Warning("[RimLayers] VVRace ManaMapComponent layout changed; layered map expansion stays disabled on those maps until the adapter is updated.");
            return;
        }
        LayerCellBufferCompatibility.RegisterNativeCellBuffer(componentType, "VVRace",
            "_manaReserveGrid", "_manaGrid", "_tmpGrid");
        LayerCellBufferCompatibility.RegisterBeforeResize(componentType, Settle);
        Log.Message("[RimLayers] VVRace mana per-cell buffers are registered for layered map expansion.");
    }

    private static bool Resolve()
    {
        if (resolved) return componentType != null;
        componentType = AccessTools.TypeByName(TypeName);
        resolved = componentType != null;
        return resolved;
    }

    /// <summary>
    /// Runs inside the expansion transaction, before any buffer is read or replaced.
    /// The diffusion job still holds the old <c>_manaGrid</c>/<c>_tmpGrid</c> allocations and writes
    /// through raw pointers, so it must finish first. Reserve mana is then folded into the
    /// authoritative grid, which makes the moved prefix lossless and leaves the reserve all zeros.
    /// </summary>
    private static void Settle(object component)
    {
        try
        {
            if (jobStarted?.GetValue(component) is true) completeDiffusion!.Invoke(component, null);
            applyAllReserved!.Invoke(component, null);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("[RimLayers] Could not settle VVRace mana buffers before expansion.", e);
        }
    }

    /// <summary>
    /// Storage index to <paramref name="map"/>'s viewed band, false for reserved isolation rows and
    /// for every other floor. A layered map's <see cref="Map.Size"/> covers all bands, so native
    /// whole-map loops reach cells that are not part of the floor being played.
    /// </summary>
    internal static bool InViewedBand(Map? map, int index)
    {
        if (map == null || index < 0) return false;
        var owner = LayerMapComponent.For(map);
        var layout = owner?.Layout;
        if (layout == null) return true;
        int width = map.Size.x;
        if (width <= 0 || index >= map.cellIndices.NumGridCells) return false;
        int storageZ = index / width;
        int layer = storageZ / layout.Stride, localZ = storageZ % layout.Stride;
        return localZ < layout.Height && layer == owner!.ViewedLayer;
    }

    /// <summary>
    /// True for a cell that exists in storage but belongs to no floor: the reserved isolation rows
    /// between bands. Ordinary maps have none, so unmanaged maps are never rejected.
    /// </summary>
    internal static bool IsIsolationCell(Map? map, IntVec3 cell)
    {
        if (map == null || !cell.InBounds(map)) return false;
        var layout = LayerMapComponent.For(map)?.Layout;
        if (layout == null) return false;
        if (cell.z < 0 || cell.z >= layout.StorageHeight) return false;
        return cell.z % layout.Stride >= layout.Height;
    }

    /// <summary>Zeroes mana stored in the reserved isolation rows so diffusion cannot cross floors.</summary>
    internal static void ClearIsolation(Map map, NativeArray<float> grid)
    {
        var layout = LayerMapComponent.For(map)?.Layout;
        if (layout == null) return;
        int width = map.Size.x;
        for (int z = 0; z < layout.StorageHeight; z++)
        {
            if (z % layout.Stride < layout.Height) continue;
            int start = z * width;
            for (int x = 0; x < width; x++) grid[start + x] = 0f;
        }
    }
}

/// <summary>
/// Runs once after every active mod's assembly is loaded and all defs are resolved.
/// <see cref="Mod"/> constructors run before that point, so <c>AccessTools.TypeByName</c> cannot see
/// other mods' types from there; this is the earliest safe place to detect VVRace, register its
/// buffers and let its <c>Prepare()</c> gates evaluate to true so the patches actually attach.
/// </summary>
[StaticConstructorOnStartup]
internal static class VVRaceBootstrap
{
    static VVRaceBootstrap()
    {
        try { VVRaceCompatibility.Install(); }
        catch (Exception e) { Log.Error($"[RimLayers] VVRace compatibility setup failed: {e}"); }
    }
}

/// <summary>
/// VVRace's overlay drawer spans the whole storage extent; keep it on the floor being viewed.
/// Both getters are patched separately: Harmony binds one specifically named prefix per target.
/// </summary>
[HarmonyPatch]
internal static class VVRaceManaOverlayBoolPatch
{
    private static bool Prepare() => VVRaceCompatibility.Active;

    private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("VVRace.ManaMapComponent"),
        "GetCellBool", new[] { typeof(int) })
        ?? throw new InvalidOperationException("[RimLayers] VVRace ManaMapComponent.GetCellBool disappeared.");

    private static bool Prefix(MapComponent __instance, int index, ref bool __result)
    {
        if (VVRaceCompatibility.InViewedBand(__instance.map, index)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class VVRaceManaOverlayColorPatch
{
    private static bool Prepare() => VVRaceCompatibility.Active;

    private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("VVRace.ManaMapComponent"),
        "GetCellExtraColor", new[] { typeof(int) })
        ?? throw new InvalidOperationException("[RimLayers] VVRace ManaMapComponent.GetCellExtraColor disappeared.");

    private static bool Prefix(MapComponent __instance, int index, ref Color __result)
    {
        if (VVRaceCompatibility.InViewedBand(__instance.map, index)) return true;
        __result = default;
        return false;
    }
}

/// <summary>
/// VVRace's manawave ritual iterates <c>map.AllCells</c> and writes mana per cell. A layered map's
/// <c>AllCells</c> spans every storage band including the reserved isolation rows, which have no
/// logical coordinate on any floor. Rejecting writes into those rows at the single write entry point
/// covers the ritual, flux rules, emitters and any future caller, instead of chasing each writer.
/// </summary>
[HarmonyPatch]
internal static class VVRaceManaChangeGuardPatch
{
    private static bool Prepare() => VVRaceCompatibility.Active;

    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("VVRace.ManaMapComponent"),
        "ChangeEnvironmentMana", new[] { typeof(IntVec3), typeof(float) })
        ?? throw new InvalidOperationException("[RimLayers] VVRace ManaMapComponent.ChangeEnvironmentMana changed.");

    private static bool Prefix(MapComponent __instance, IntVec3 cell) =>
        !VVRaceCompatibility.IsIsolationCell(__instance.map, cell);
}

/// <summary>
/// After each diffusion cycle the output grid covers every band plus the isolation rows.
/// Clearing only the isolation rows gives each floor a hard boundary, matching a standalone map.
/// </summary>
[HarmonyPatch]
internal static class VVRaceManaDiffusionPatch
{
    private static readonly Type? Type = AccessTools.TypeByName("VVRace.ManaMapComponent");
    private static readonly FieldInfo? ManaGrid = Type == null ? null : AccessTools.Field(Type, "_manaGrid");
    private static readonly AccessTools.FieldRef<MapComponent, Map> MapRef =
        AccessTools.FieldRefAccess<MapComponent, Map>("map");

    private static bool Prepare() => VVRaceCompatibility.Active && ManaGrid != null;

    private static MethodBase TargetMethod() => AccessTools.Method(Type, "CompleteDiffuseManaJob")
        ?? throw new InvalidOperationException("[RimLayers] VVRace ManaMapComponent.CompleteDiffuseManaJob disappeared.");

    private static void Postfix(object __instance)
    {
        var map = MapRef((MapComponent)__instance);
        if (ManaGrid!.GetValue(__instance) is not NativeArray<float> grid) return;
        VVRaceCompatibility.ClearIsolation(map, grid);
    }
}
