using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Cross-layer support for the five Dubwise mods.
///
/// <para>Pipes, wires and data lines are handled by <see cref="LayerPipePorts"/> (the riser defs live in
/// <c>Patches/LayerPorts.xml</c>); this class owns the two remaining halves of the contract:</para>
///
/// <list type="bullet">
/// <item><b>Ground-bound resources.</b> Bad Hygiene rolls groundwater / deep water / ice and Rimefeller
/// rolls oil and deep oil with <c>foreach (IntVec3 c in map.AllCells)</c>. A layered map stores every
/// band inside one map, so those loops seed the upper floors too. See <see cref="DubsWater"/> and
/// <see cref="RimefellerOil"/>; both keep elevation &lt;= 0 only, which is exactly "the surface and the
/// dug strata, never the 2nd floor or above".</item>
/// <item><b>Skylights.</b> Dubs Skylights answers sky light from its own grid without asking what is
/// above the cell. See <see cref="DubsSkylights"/>.</item>
/// </list>
///
/// <para><b>Why a static constructor and not the Mod constructor.</b> <c>Mod</c> constructors run before
/// other mods' assemblies are reachable through <see cref="AccessTools.TypeByName(string)"/>, and every
/// binding here is optional. <c>StaticConstructorOnStartup</c> runs after all mods load and all defs
/// resolve, so a missing or reshaped third-party type costs one skipped adapter instead of a broken
/// RimLayers.</para>
/// </summary>
[StaticConstructorOnStartup]
public static class DubsCompatibility
{
    internal const string HygienePackage = "dubwise.dubsbadhygiene";
    internal const string HeatingPackage = "dubwise.dubscentralheating";
    internal const string RimatomicsPackage = "dubwise.rimatomics";
    internal const string RimefellerPackage = "dubwise.rimefeller";
    internal const string SkylightsPackage = "dubwise.dubsskylights";

    internal const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static bool installed;

    internal static bool Active => installed;
    internal static bool WaterActive { get; private set; }
    internal static bool OilActive { get; private set; }
    internal static bool SkylightsActive { get; private set; }

    static DubsCompatibility()
    {
        try { Install(new Harmony(RimLayersMod.HarmonyId + ".dubs")); }
        catch (Exception e) { Log.Error($"[RimLayers] Dubwise compatibility setup failed: {e}"); }
    }

    internal static void Install(Harmony harmony)
    {
        if (installed) return;
        installed = true;
        // Registered before the adapters so a map that grows during this same frame is repaired once
        // the adapters have bound their fields.
        LiveMapExpansion.Committed += OnLayersCommitted;
        WaterActive = DubsWater.Install(harmony);
        OilActive = RimefellerOil.Install(harmony);
        SkylightsActive = DubsSkylights.Install(harmony);
        if (WaterActive || OilActive || SkylightsActive)
            Log.Message($"[RimLayers] Dubwise layers: groundwater={WaterActive}, oil={OilActive}, skylights={SkylightsActive}.");
    }

    internal static bool ActiveMod(string packageId) =>
        ModLister.GetActiveModWithIdentifier(packageId, false) != null;

    /// <summary>
    /// A live (or generated) map grew additional storage bands. Native managers are resized by
    /// <see cref="LiveMapExpansion"/> itself; third-party state is not, because those components never
    /// see <c>MapComponentUtility</c> callbacks on that path.
    /// </summary>
    private static void OnLayersCommitted(Map map, LayerLayout layout)
    {
        if (map == null || map.Disposed) return;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return;
        Run("drawer sections", () => RepairDrawers(map));
        Run("pipe nets", () => LayerPipePorts.ExpansionCompleted(map));
        if (WaterActive) Run("groundwater", () => DubsWater.OnLayersChanged(map, owner));
        if (OilActive) Run("oil", () => RimefellerOil.OnLayersChanged(map, owner));
        if (SkylightsActive) Run("skylights", () => DubsSkylights.OnLayersChanged(map, owner));

        static void Run(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Log.Warning($"[RimLayers] Could not re-arm {what} for the expanded map; that part stays on the old size.\n{e}"); }
        }
    }

    /// <summary>Every storage cell on a layer above the ground (elevation &gt; 0), skipping isolation rows.</summary>
    internal static void ForEachOnUpperLayers(Map map, LayerMapComponent owner, Action<IntVec3> visit)
    {
        LayerLayout? layout = owner.Layout;
        if (layout == null) return;
        foreach (int layer in layout.OrderedLayers)
        {
            if (owner.Elevation(layer) <= 0) continue;
            CellRect rect = owner.LayerRect(layer);
            for (int z = rect.minZ; z <= rect.maxZ; z++)
                for (int x = rect.minX; x <= rect.maxX; x++)
                    visit(new IntVec3(x, 0, z));
        }
    }

    /// <summary>
    /// The Dubs and Rimefeller value grids are <c>GridLayer</c> instances that hold a
    /// <c>bool[SectionCount.x, SectionCount.z]</c> dirty map allocated at construction time. A live
    /// expansion replaces <c>map.mapDrawer</c> with a larger one, so that rectangle (and the drawer's
    /// own size) has to follow, otherwise the first write into a new section throws.
    /// </summary>
    internal static void RepairDrawers(Map map)
    {
        MapDrawer? drawer = map.mapDrawer;
        if (drawer == null || map.components == null) return;
        // SectionCount is private on Verse.MapDrawer, and it is the only authority on how many
        // 17-cell sections the (now taller) storage rectangle has.
        if (AccessTools.Property(typeof(MapDrawer), "SectionCount") is not { } property
            || property.GetValue(drawer) is not IntVec2 sections) return;
        IntVec3 size = map.Size;
        int cells = map.cellIndices.NumGridCells;
        foreach (MapComponent? component in map.components)
        {
            if (component == null) continue;
            foreach (FieldInfo field in AllFields(component.GetType()))
            {
                object? layer;
                try { layer = field.GetValue(component); }
                catch { continue; }
                if (layer == null || layer.GetType().Name != "GridLayer") continue;
                RepairLayer(layer, sections, size, cells);
            }
        }
    }

    private static void RepairLayer(object layer, IntVec2 sections, IntVec3 size, int cells)
    {
        Type type = layer.GetType();
        if (AccessTools.Field(type, "Grid") is { } grid && grid.GetValue(layer) is float[] values && values.Length != cells)
        {
            float[] grown = new float[cells];
            Array.Copy(values, grown, Math.Min(values.Length, cells));
            grid.SetValue(layer, grown);
        }
        if (!ReferenceEquals(AccessTools.Field(type, "DirtyFlags")?.GetValue(layer), null)
            && AccessTools.Field(type, "DirtyFlags")!.GetValue(layer) is bool[,] flags
            && (flags.GetLength(0) != sections.x || flags.GetLength(1) != sections.z))
            AccessTools.Field(type, "DirtyFlags")!.SetValue(layer, new bool[sections.x, sections.z]);
        if (AccessTools.Field(type, "Drawer")?.GetValue(layer) is CellBoolDrawer drawer)
        {
            // The drawer meshes a CellRect(0, 0, mapSizeX, mapSizeZ) and indexes it with
            // CellIndicesUtility.CellToIndex(x, z, mapSizeX). The constructor seeds both from
            // map.Size while the map was shrunk for generation, so they must be restored to the
            // full storage rectangle: map width by every band plus its padding.
            AccessTools.Field(typeof(CellBoolDrawer), "mapSizeX")?.SetValue(drawer, size.x);
            AccessTools.Field(typeof(CellBoolDrawer), "mapSizeZ")?.SetValue(drawer, size.z);
            AccessTools.Field(typeof(CellBoolDrawer), "dirty")?.SetValue(drawer, true);
        }
    }

    internal static IEnumerable<FieldInfo> AllFields(Type type)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            foreach (FieldInfo field in t.GetFields(Fields | BindingFlags.DeclaredOnly))
                yield return field;
    }
}
