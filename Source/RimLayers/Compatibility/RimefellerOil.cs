using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Surface-only oil for Rimefeller on layered maps.
///
/// <para><b>The problem.</b> <c>MapComponent_Rimefeller.RegenSeedGrids</c> writes a Perlin value for
/// every <c>map.AllCells</c> entry, so a layered map grows an invisible oil field on the 2nd floor.
/// <c>GenerateFields</c> then floods those seed cells into <see cref="Rimefeller.OilField"/>
/// instances, and the well-head place workers accept any cell whose <c>GetFieldAt</c> returns a field —
/// so clearing the seeds is enough to make the wells unplaceable too.</para>
///
/// <para><b>What this does.</b> Both generation paths are gated to elevation &lt;= 0 (surface plus dug
/// strata), and existing upper-floor fields are dropped from <c>Fields</c>. Seeds are never serialized
/// (<c>OilGrid.ExposeData</c> only stores <c>Deep</c> and <c>Fields</c>), so this leaves no trace in the
/// save and does not need a migration.</para>
///
/// <para><b>Why not <c>RerollOilGrids</c> on expansion.</b> That method re-rolls both reservoir seeds and
/// rebuilds the grids from scratch, which would silently relocate a colony's existing oil on every
/// expansion. Reseeding with the stored seeds reproduces the previous field byte for byte.</para>
/// </summary>
internal static class RimefellerOil
{
    private const string ComponentType = "Rimefeller.MapComponent_Rimefeller";

    private static Type? component;

    internal static bool Install(Harmony harmony)
    {
        if (!DubsCompatibility.ActiveMod(DubsCompatibility.RimefellerPackage)) return false;
        component = AccessTools.TypeByName(ComponentType);
        if (component == null)
        {
            Log.Warning($"[RimLayers] {ComponentType} not found; Rimefeller oil keeps generating on every layer.");
            return false;
        }
        if (AccessTools.Field(component, "OilGrid") is null || AccessTools.Field(component, "DeepOilGrid") is null)
        {
            Log.Warning($"[RimLayers] {ComponentType}.OilGrid changed shape; Rimefeller oil keeps generating on every layer.");
            return false;
        }

        int patched = 0;
        foreach (string name in new[] { "RegenSeedGrids", "GenerateFields", "RerollOilGrids", "FinalizeInit" })
            if (AccessTools.Method(component, name) is { } target)
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(RimefellerOil), nameof(AfterGenerate)));
                patched++;
            }
        if (patched == 0)
        {
            Log.Warning($"[RimLayers] {ComponentType} has no oil generators; Rimefeller oil keeps generating on every layer.");
            return false;
        }
        return true;
    }

    private static void AfterGenerate(MapComponent __instance)
    {
        try { Clear(__instance); }
        catch (Exception e)
        {
            Log.Warning($"[RimLayers] Could not clear upper-layer Rimefeller oil; a hidden field may remain above the ground floor.\n{e}");
        }
    }

    private static void Clear(MapComponent instance)
    {
        Map map = instance.map;
        if (map == null || map.Disposed) return;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return;

        // Seeds first: GenerateFields derives every field from them, and a later GenerateFields call
        // (or the lazily evaluated OilField.GridArea) must not find a fresh upper-floor depost.
        int seeds = 0;
        foreach (string name in new[] { "OilGrid", "DeepOilGrid" })
        {
            if (AccessTools.Field(component!, name)?.GetValue(instance) is not { } grid) continue;
            var type = grid.GetType();
            if (AccessTools.Field(type, "SeedGrid")?.GetValue(grid) is not float[] values) continue;
            DubsCompatibility.ForEachOnUpperLayers(map, owner, cell =>
            {
                int index = map.cellIndices.CellToIndex(cell);
                if ((uint)index >= (uint)values.Length || values[index] <= 0f) return;
                values[index] = 0f;
                seeds++;
            });
            if (AccessTools.Field(type, "Fields")?.GetValue(grid) is System.Collections.IList fields && fields.Count > 0)
            {
                for (int i = fields.Count - 1; i >= 0; i--)
                    if (fields[i] is { } field && UpperLayer(map, owner, AccessTools.Field(field.GetType(), "RootCell")?.GetValue(field)))
                        fields.RemoveAt(i);
            }
        }
        if (seeds > 0) Log.Message($"[RimLayers] Cleared {seeds} upper-layer cells from Rimefeller oil seeds.");
    }

    private static bool UpperLayer(Map map, LayerMapComponent owner, object? root)
    {
        if (root is not IntVec3 cell) return false;
        return owner.TryDecode(cell, out var local) && !DeepResourceScope.LayerAllowed(owner, local.LayerId);
    }

    /// <summary>
    /// Newly appended bands arrive with a zero-filled seed grid, so the dug strata would be barren.
    /// Reseeding with the stored reservoir seeds (not <c>RerollOilGrids</c>, which would move the
    /// existing oil) reproduces the previous field exactly and fills the new bands; the seeds above
    /// the ground are cleared again immediately afterwards.
    ///
    /// <para><b>Why <c>GenerateFields</c> is not called here.</b> It replaces <c>Fields</c> wholesale,
    /// and <c>Building_WellHead</c> caches its <c>OilField</c> once in <c>TickRare</c> and never
    /// re-resolves it, so a rebuilt list silently orphans every existing well head. Instead the fields
    /// that already exist are kept as-is and only the newly seeded cells grow new ones.</para>
    /// </summary>
    internal static void OnLayersChanged(Map map, LayerMapComponent owner)
    {
        if (map.components == null) return;
        foreach (MapComponent? instance in map.components)
        {
            if (instance == null || !component!.IsInstanceOfType(instance)) continue;
            // During map generation this hook runs before the mod's own FinalizeInit, so the grids
            // may not exist yet; the mod seeds them itself moments later.
            if (AccessTools.Field(component, "OilGrid")?.GetValue(instance) is not { }) return;
            if (AccessTools.Field(component, "DeepOilGrid")?.GetValue(instance) is not { }) return;
            AccessTools.Method(component, "RegenSeedGrids")?.Invoke(instance, null);
            Clear(instance);
            Replant(map, instance);
            return;
        }
    }

    /// <summary>
    /// Gives every seeded cell that no field claims yet a field of its own, keeping the fields that
    /// already exist so cached well-head references stay valid.
    /// </summary>
    private static void Replant(Map map, MapComponent instance)
    {
        Type? fieldType = AccessTools.TypeByName("Rimefeller.OilField");
        if (fieldType == null) return;
        foreach (string name in new[] { "OilGrid", "DeepOilGrid" })
        {
            if (AccessTools.Field(component!, name)?.GetValue(instance) is not { } grid) continue;
            Type type = grid.GetType();
            if (AccessTools.Field(type, "SeedGrid")?.GetValue(grid) is not float[] seeds) continue;
            if (AccessTools.Field(type, "Fields")?.GetValue(grid) is not System.Collections.IList fields) continue;
            ConstructorInfo? ctor = AccessTools.Constructor(fieldType, new[] { type, typeof(Map) });
            if (ctor == null || AccessTools.Method(type, "GenerateFieldAt", new[] { typeof(IntVec3) }) is not { } generate) return;

            var claimed = new HashSet<IntVec3>();
            foreach (object? field in fields)
            {
                if (field == null) continue;
                try { if (AccessTools.Property(field.GetType(), "GridArea")?.GetValue(field) is IEnumerable<IntVec3> area) claimed.UnionWith(area); }
                catch (Exception e) { Log.Warning($"[RimLayers] Could not read a Rimefeller oil field area: {e.Message}"); }
            }

            int added = 0;
            foreach (IntVec3 cell in map.AllCells)
            {
                int index = map.cellIndices.CellToIndex(cell);
                if ((uint)index >= (uint)seeds.Length || seeds[index] <= 0.01f || claimed.Contains(cell)) continue;
                if (generate.Invoke(grid, new object[] { cell }) is not IEnumerable<IntVec3> area) continue;
                var cells = area as HashSet<IntVec3> ?? new HashSet<IntVec3>(area);
                if (cells.Count == 0) continue;
                object fresh = ctor.Invoke(new object[] { grid, map });
                float capacity = 0f;
                foreach (IntVec3 covered in cells)
                {
                    int i = map.cellIndices.CellToIndex(covered);
                    if ((uint)i < (uint)seeds.Length) capacity += seeds[i];
                }
                AccessTools.Field(fieldType, "RootCell")?.SetValue(fresh, cell);
                AccessTools.Field(fieldType, "_GridArea")?.SetValue(fresh, cells);
                AccessTools.Field(fieldType, "MaxCapacity")?.SetValue(fresh, capacity);
                AccessTools.Field(fieldType, "CurrentCap")?.SetValue(fresh, (double)capacity);
                fields.Add(fresh);
                claimed.UnionWith(cells);
                added++;
            }
            if (added > 0) Log.Message($"[RimLayers] Replanted {added} {name} field(s) on the newly added strata.");
        }
    }
}
