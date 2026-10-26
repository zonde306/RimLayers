using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Groundwater for Dubs Bad Hygiene on layered maps.
///
/// <para><b>The problem.</b> <c>MapComponent_Hygiene.RegenWaterGrid</c>,
/// <c>RegenDeepWaterGrid</c> and <c>RegenIceGrid</c> all end with
/// <c>foreach (IntVec3 current in map.AllCells)</c>. On a layered map <c>AllCells</c> covers every
/// stored band, so the noise pass happily seeds a hidden aquifer and an ice lens on the 2nd floor,
/// which then lets a vanilla well be placed in mid-air.</para>
///
/// <para><b>What this does.</b> After each generator runs, every storage cell whose layer sits above
/// the ground (elevation &gt; 0) is zeroed in the value grid, so only the surface and the dug strata
/// keep water. Placement is gated with the same rule so the player gets a reason string instead of a
/// silently empty well.</para>
///
/// <para><b>Why re-running the generators is safe.</b> The terrain pass uses
/// <c>GridLayer.AddRadial</c>, which takes a <em>maximum</em> (<c>if (Grid[i] &lt; depth) Grid[i] = depth</c>),
/// and the noise pass uses <c>SetAt</c>, which writes an absolute value. Neither accumulates, so
/// regenerating on an already generated map reproduces the surface values instead of doubling them.
/// That is what lets the expansion hook reseed only the bands that did not exist yet.</para>
/// </summary>
internal static class DubsWater
{
    private const string ComponentType = "DubsBadHygiene.MapComponent_Hygiene";

    /// <summary>Value grids derived from terrain and noise. Sewage, fertilizer and irrigation are
    /// player-driven and stay untouched.</summary>
    private static readonly string[] Generated = { "WaterGrid", "DeepWaterGrid", "IceGrid" };

    private static Type? component;
    private static bool clamping;

    internal static bool Install(Harmony harmony)
    {
        if (!DubsCompatibility.ActiveMod(DubsCompatibility.HygienePackage)) return false;
        component = AccessTools.TypeByName(ComponentType);
        if (component == null)
        {
            Log.Warning($"[RimLayers] {ComponentType} not found; Dubs groundwater keeps generating on every layer.");
            return false;
        }
        if (AccessTools.Field(component, "WaterGrid")?.FieldType is not { } gridLayer)
        {
            Log.Warning($"[RimLayers] {ComponentType}.WaterGrid changed shape; Dubs groundwater keeps generating on every layer.");
            return false;
        }
        if (AccessTools.Field(gridLayer, "Grid")?.FieldType != typeof(float[]))
        {
            Log.Warning($"[RimLayers] {gridLayer.Name}.Grid is no longer a float[]; Dubs groundwater keeps generating on every layer.");
            return false;
        }

        int patched = 0;
        var postfix = new HarmonyMethod(typeof(DubsWater), nameof(AfterGenerate));
        // FinalizeInit covers saves written before this adapter existed: it reuses the serialized
        // grids without regenerating, so a stale upper-floor aquifer would otherwise survive forever.
        foreach (string name in new[] { "RegenWaterGrid", "RegenDeepWaterGrid", "RegenIceGrid", "FinalizeInit" })
            if (AccessTools.Method(component, name) is { } target)
            {
                harmony.Patch(target, postfix: postfix);
                patched++;
            }

        foreach (string worker in new[] { "PlaceWorker_WaterGrid", "PlaceWorker_DeepWaterGrid" })
        {
            if (AccessTools.TypeByName($"DubsBadHygiene.{worker}") is not { } type) continue;
            if (AccessTools.Method(type, "AllowsPlacing") is { } allows)
                harmony.Patch(allows, prefix: new HarmonyMethod(typeof(DubsWater), nameof(AllowsPlacingPrefix)));
        }

        if (patched == 0)
        {
            Log.Warning($"[RimLayers] {ComponentType} has no water generators; Dubs groundwater keeps generating on every layer.");
            return false;
        }
        return true;
    }

    private static void AfterGenerate(MapComponent __instance)
    {
        if (clamping) return;
        try { Clear(__instance, __instance.map); }
        catch (Exception e)
        {
            clamping = false;
            Log.Warning($"[RimLayers] Could not clear upper-layer Dubs groundwater; a hidden aquifer may remain above the ground floor.\n{e}");
        }
    }

    /// <summary>Zeroes every generated water value on a layer above the ground.</summary>
    private static void Clear(MapComponent instance, Map map)
    {
        if (map == null || map.Disposed) return;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return;
        clamping = true;
        try
        {
            foreach (string name in Generated)
            {
                if (AccessTools.Field(component!, name)?.GetValue(instance) is not { } layer) continue;
                if (AccessTools.Field(layer.GetType(), "Grid")?.GetValue(layer) is not float[] values) continue;
                int cleared = 0;
                DubsCompatibility.ForEachOnUpperLayers(map, owner, cell =>
                {
                    int index = map.cellIndices.CellToIndex(cell);
                    if ((uint)index >= (uint)values.Length || values[index] == 0f) return;
                    values[index] = 0f;
                    cleared++;
                });
                if (cleared == 0) continue;
                if (AccessTools.Field(layer.GetType(), "Drawer")?.GetValue(layer) is CellBoolDrawer drawer)
                {
                    // The overlay mesh is rebuilt from GetCellBool, so clearing the value is enough;
                    // SetDirty only makes that happen on the next update instead of never.
                    drawer.SetDirty();
                    if (map == Find.CurrentMap) drawer.MarkForDraw();
                }
                Log.Message($"[RimLayers] Cleared {cleared} upper-layer cells from Dubs {name}.");
            }
        }
        finally { clamping = false; }
    }

    /// <summary>
    /// A live expansion appended new bands with a zero-filled grid, so those dug strata would have
    /// no groundwater at all. Re-run the generators once (they are idempotent, see the type summary)
    /// and clear whatever landed above the ground afterwards.
    /// </summary>
    internal static void OnLayersChanged(Map map, LayerMapComponent owner)
    {
        if (map.components == null) return;
        foreach (MapComponent? instance in map.components)
        {
            if (instance == null || !component!.IsInstanceOfType(instance)) continue;
            foreach (string name in new[] { "RegenWaterGrid", "RegenDeepWaterGrid", "RegenIceGrid" })
                AccessTools.Method(component, name)?.Invoke(instance, null);
            Clear(instance, map);
            return;
        }
    }

    private static bool AllowsPlacingPrefix(IntVec3 loc, Map map, ref AcceptanceReport __result)
    {
        if (map == null || LayerMapComponent.For(map) is not { } owner) return true;
        if (!owner.TryDecode(loc, out var local) || DeepResourceScope.LayerAllowed(owner, local.LayerId)) return true;
        __result = DeepResourceScope.UpperLayerReasonKey.Translate();
        return false;
    }
}
