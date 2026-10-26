using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Dubs Skylights on layered maps.
///
/// <para><b>The problem.</b> <c>MapComp_Skylights</c> keeps a flat <c>bool[] SkylightGrid</c> of "this
/// storage cell sees the sky", built only from each skylight's own footprint, and
/// <c>Patch_GameGlowAt</c> raises <c>GlowGrid.GroundGlowAt</c> to <c>CurSkyGlow</c> wherever the flag is
/// set. Nothing in that chain asks whether a floor was built above the skylight, so a skylight on the
/// ground floor under an upper deck keeps lighting and heating the room as if it were open air — even
/// though RimLayers' own <c>SkyExposure</c> already knows the column is blocked.</para>
///
/// <para><b>What this does.</b> Clearing the flag is the single point that fixes every consumer at
/// once, because they all read this one grid:</para>
/// <list type="bullet">
/// <item>Dubs' own <c>Patch_GameGlowAt</c> postfix (sky light on the glow grid);</item>
/// <item>Dubs' <c>Patch_SectionLayer_LightingOverlay_Regenerate</c>, which temporarily removes the roofl
/// for exactly the flagged cells while the lighting overlay is rebuilt;</item>
/// <item>Dubs Central Heating's <c>CompBoilerSolar.RoofedHeatOutputFactor</c>, which
/// <c>Traverse</c>s the same array to decide whether a roofed cell still gets sun;</item>
/// <item>Dubs Bad Hygiene's reflected <c>skylights</c> field, used for rain and sun checks.</item>
/// </list>
/// <para><c>SkyExposure.IsExposed</c> already implements the exact rule the player asked for — walk the
/// column from the top, and any roof or actual floor above blocks the sky — so this adapter only has
/// to copy that answer into the flag grid after Dubs rebuilds it.</para>
///
/// <para><b>Placement stays native.</b> Dubs' <c>Patch_BlocksConstruction</c> already lets a skylight
/// be built over anything; RimLayers' own <c>Structure.Validate</c> is what requires a real surface
/// under it, so no placement hook is needed here.</para>
/// </summary>
internal static class DubsSkylights
{
    private const string ComponentType = "Dubs_Skylight.MapComp_Skylights";
    private const string LightType = "Dubs_Skylight.Building_skyLight";

    private static Type? component;
    private static Type? light;

    internal static bool Install(Harmony harmony)
    {
        if (!DubsCompatibility.ActiveMod(DubsCompatibility.SkylightsPackage)) return false;
        component = AccessTools.TypeByName(ComponentType);
        light = AccessTools.TypeByName(LightType);
        if (component == null)
        {
            Log.Warning($"[RimLayers] {ComponentType} not found; skylights keep ignoring upper floors.");
            return false;
        }
        if (AccessTools.Field(component, "SkylightGrid")?.FieldType != typeof(bool[]))
        {
            Log.Warning($"[RimLayers] {ComponentType}.SkylightGrid changed shape; skylights keep ignoring upper floors.");
            return false;
        }
        if (AccessTools.Method(component, "RegenGrid") is not { } regen)
        {
            Log.Warning($"[RimLayers] {ComponentType}.RegenGrid missing; skylights keep ignoring upper floors.");
            return false;
        }
        harmony.Patch(regen, postfix: new HarmonyMethod(typeof(DubsSkylights), nameof(AfterRegenGrid)));

        // The whole body of TickRare is a heat push computed from outdoor temperature and sky glow;
        // there is nothing else to preserve, so a blocked column can skip it outright.
        if (light != null && AccessTools.Method(light, "TickRare") is { } tick)
            harmony.Patch(tick, prefix: new HarmonyMethod(typeof(DubsSkylights), nameof(SkipTickRareWhenBlocked)));

        return true;
    }

    /// <summary>Re-applies the occlusion answer to a freshly rebuilt grid.</summary>
    private static void AfterRegenGrid(MapComponent __instance)
    {
        try { Clear(__instance); }
        catch (Exception e) { Log.Warning($"[RimLayers] Could not mask blocked Dubs skylights; they keep lighting upper-layer shadow.\n{e}"); }
    }

    private static void Clear(MapComponent instance)
    {
        Map map = instance.map;
        if (map == null || map.Disposed) return;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return;
        if (AccessTools.Field(component!, "SkylightGrid")?.GetValue(instance) is not bool[] grid) return;
        int blocked = 0;
        int cells = Math.Min(grid.Length, map.cellIndices.NumGridCells);
        for (int index = 0; index < cells; index++)
        {
            if (!grid[index]) continue;
            // The grid is storage-indexed, so the same index the game uses converts back to a cell.
            IntVec3 cell = CellIndicesUtility.IndexToCell(index, map.Size.x);
            if (!owner.TryDecode(cell, out _)) continue;
            if (owner.Sky.IsExposed(cell)) continue;
            grid[index] = false;
            touched.Add(cell);
            blocked++;
        }
        if (blocked == 0) return;
        Log.Message($"[RimLayers] Masked {blocked} block-covered Dubs skylight cells.");
        // Blocked cells no longer emit sky glow; the glow grid caches per-cell glow, so it has to be
        // told for exactly those cells rather than rebuilt for the whole map.
        foreach (IntVec3 cell in touched) map.glowGrid.DirtyCell(cell);
        touched.Clear();
    }

    // Only the cells this pass cleared are re-dirtied; the loop above cannot use the map query
    // because clearing happens while iterating a flat storage-indexed array.
    [System.ThreadStatic] private static List<IntVec3>? touchedCells;
    private static List<IntVec3> touched => touchedCells ??= new List<IntVec3>();

    /// <summary>A blocked skylight must not push heat from air it cannot see.</summary>
    private static bool SkipTickRareWhenBlocked(Thing __instance)
    {
        Map map = __instance.Map;
        if (map == null) return true;
        if (LayerMapComponent.For(map) is not { } owner) return true;
        return owner.Sky.IsExposed(__instance.Position);
    }

    /// <summary>
    /// A live expansion appends bands that have no skylights, and keeps the flag grid the same length
    /// as the map. Rebuilding is cheap and is the only thing Dubs itself does here.
    /// </summary>
    internal static void OnLayersChanged(Map map, LayerMapComponent owner)
    {
        if (map.components == null) return;
        foreach (MapComponent? instance in map.components)
        {
            if (instance == null || !component!.IsInstanceOfType(instance)) continue;
            AccessTools.Method(component, "RegenGrid")?.Invoke(instance, null);
            return;
        }
    }
}
