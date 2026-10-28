using HarmonyLib;
using RimLayers.Core;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Areas;

/// <summary>
/// Shared gate for every vanilla area/zone paint designator.
/// A managed map stores all layers in one flat grid, so <c>map.cellIndices</c> happily indexes the
/// stride isolation band and the neighbouring layer. Vanilla designators only test
/// <c>InBounds</c>, which lets a drag that starts on one layer paint open air, another layer, or
/// the unused band. Every gate below funnels through <see cref="Check"/>.
/// </summary>
internal static class AreaLayerGate
{
    internal const string OffLayerKey = "RL_AreaOffLayer";

    internal static AcceptanceReport Check(Map? map, IntVec3 cell)
    {
        var owner = LayerMapComponent.For(map);
        if (owner == null) return AcceptanceReport.WasAccepted;
        if (!owner.TryDecode(cell, out var logical) || logical.LayerId != owner.ViewedLayer)
            return OffLayerKey.Translate();
        if (!owner.Structure.HasSurface(cell)) return "RL_NoSurface".Translate();
        return AcceptanceReport.WasAccepted;
    }

    /// <summary>Home-area and roof spread: same layer as the anchor, and a real floor.</summary>
    internal static bool AcceptsSpread(LayerMapComponent owner, IntVec3 cell, int anchorLayer)
    {
        if (!owner.TryDecode(cell, out var logical)) return false;
        return AreaLayerRules.AcceptsSpread(logical.LayerId, anchorLayer, owner.Structure.HasSurface(cell));
    }

    internal static int LayerOf(LayerMapComponent owner, IntVec3 cell, int fallback)
        => owner.TryDecode(cell, out var logical) ? logical.LayerId : fallback;

    /// <summary>
    /// Vanilla guards every automatic home-area write with a private <c>ShouldAdd()</c>
    /// (<c>Find.PlaySettings.autoHomeArea &amp;&amp; Current.ProgramState == Playing</c>). The prefixes
    /// below replace the whole vanilla body, so they must re-apply that guard or a player who turned
    /// the automatic home area off would still get one.
    /// </summary>
    internal static readonly Func<bool> ShouldAdd =
        AccessTools.MethodDelegate<Func<bool>>(AccessTools.Method(typeof(AutoHomeAreaMaker), "ShouldAdd"));
}

[HarmonyPatch(typeof(Designator_AreaHome), nameof(Designator_AreaHome.CanDesignateCell))]
internal static class AreaHomeGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

[HarmonyPatch(typeof(Designator_AreaSnowClear), nameof(Designator_AreaSnowClear.CanDesignateCell))]
internal static class AreaSnowClearGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

[HarmonyPatch(typeof(Designator_AreaPollutionClear), nameof(Designator_AreaPollutionClear.CanDesignateCell))]
internal static class AreaPollutionClearGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

// The allowed-area expand/clear designators each override CanDesignateCell, so there is no shared
// base entry point to patch.
[HarmonyPatch(typeof(Designator_AreaAllowedExpand), nameof(Designator_AreaAllowedExpand.CanDesignateCell))]
internal static class AreaAllowedExpandGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

[HarmonyPatch(typeof(Designator_AreaAllowedClear), nameof(Designator_AreaAllowedClear.CanDesignateCell))]
internal static class AreaAllowedClearGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

[HarmonyPatch(typeof(Designator_AreaBuildRoof), nameof(Designator_AreaBuildRoof.CanDesignateCell))]
internal static class AreaBuildRoofGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

[HarmonyPatch(typeof(Designator_AreaNoRoof), nameof(Designator_AreaNoRoof.CanDesignateCell))]
internal static class AreaNoRoofGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

[HarmonyPatch(typeof(Designator_AreaIgnoreRoof), nameof(Designator_AreaIgnoreRoof.CanDesignateCell))]
internal static class AreaIgnoreRoofGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

/// <summary>
/// Covers every zone-placement designator: stockpile/growing/fishing and their *_Expand variants
/// all funnel through <see cref="Designator_ZoneAdd.CanDesignateCell"/>. Vanilla
/// <c>IsZoneableCell</c> never checks whether there is a floor at all, so zones can be drawn in
/// mid-air on an upper layer.
/// </summary>
[HarmonyPatch(typeof(Designator_ZoneAdd), nameof(Designator_ZoneAdd.CanDesignateCell))]
internal static class ZoneAddGate
{
    static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, c);
        if (!report.Accepted) __result = report;
    }
}

// Native signature is CanDesignateCell(IntVec3 sq), not (IntVec3 c): Harmony binds by name, so the
// postfix must use the same name or patching throws and the whole class is skipped.
[HarmonyPatch(typeof(Designator_ZoneDelete), nameof(Designator_ZoneDelete.CanDesignateCell))]
internal static class ZoneDeleteGate
{
    static void Postfix(IntVec3 sq, Designator __instance, ref AcceptanceReport __result)
    {
        if (!__result.Accepted) return;
        var report = AreaLayerGate.Check(__instance.Map, sq);
        if (!report.Accepted) __result = report;
    }
}

/// <summary>
/// Vanilla grows the home area in a fixed rectangle around a new building: on a managed map the
/// rectangle is indexed in flat storage space, so the far rows land on the neighbouring layer or
/// in the unused band. Rewrite the loop so it only touches the anchor's own layer.
/// </summary>
[HarmonyPatch(typeof(AutoHomeAreaMaker), nameof(AutoHomeAreaMaker.MarkHomeAroundThing))]
internal static class HomeSpreadAroundThing
{
    static bool Prefix(Thing t)
    {
        var owner = LayerMapComponent.For(t.Map);
        if (owner == null) return true;
        if (!AreaLayerGate.ShouldAdd()) return false;
        var map = t.Map;
        int anchor = AreaLayerGate.LayerOf(owner, t.Position, owner.ViewedLayer);
        var rect = new CellRect(t.Position.x - t.RotatedSize.x / 2 - 4,
            t.Position.z - t.RotatedSize.z / 2 - 4, t.RotatedSize.x + 8, t.RotatedSize.z + 8);
        rect.ClipInsideMap(map);
        foreach (var cell in rect)
            if (AreaLayerGate.AcceptsSpread(owner, cell, anchor)) map.areaManager.Home[cell] = true;
        return false;
    }
}

/// <summary>Same problem for the 9×9 home-area blob added around every new zone cell.</summary>
[HarmonyPatch(typeof(AutoHomeAreaMaker), nameof(AutoHomeAreaMaker.Notify_ZoneCellAdded))]
internal static class HomeSpreadAroundZoneCell
{
    static bool Prefix(IntVec3 c, Zone zone)
    {
        var owner = LayerMapComponent.For(zone.Map);
        if (owner == null) return true;
        if (!AreaLayerGate.ShouldAdd()) return false;
        var map = zone.Map;
        int anchor = AreaLayerGate.LayerOf(owner, c, owner.ViewedLayer);
        foreach (var cell in CellRect.CenteredOn(c, 4).ClipInsideMap(map))
            if (AreaLayerGate.AcceptsSpread(owner, cell, anchor)) map.areaManager.Home[cell] = true;
        return false;
    }
}

/// <summary>
/// The automatic roof setter writes <c>BuildRoof</c> straight into the grid. Its candidate set can
/// reach past the room's layer, which later satisfies <c>HasRequiredRoof</c> on cells that do not
/// exist. Undo whatever it seeded off-layer.
/// </summary>
[HarmonyPatch(typeof(AutoBuildRoofAreaSetter), "TryGenerateAreaNow")]
internal static class AutoRoofSpreadGate
{
    private static readonly AccessTools.FieldRef<AutoBuildRoofAreaSetter, List<IntVec3>> JustRoofed =
        AccessTools.FieldRefAccess<AutoBuildRoofAreaSetter, List<IntVec3>>("justRoofedCells");

    static void Postfix(AutoBuildRoofAreaSetter __instance, Room room)
    {
        var map = room.Map;
        var owner = LayerMapComponent.For(map);
        if (owner == null) return;
        var cells = JustRoofed(__instance);
        if (cells == null || cells.Count == 0) return;
        int anchor = AreaLayerGate.LayerOf(owner, room.Cells.First(), owner.ViewedLayer);
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (AreaLayerGate.AcceptsSpread(owner, cell, anchor)) continue;
            if (map.areaManager.BuildRoof[cell]) map.areaManager.BuildRoof[cell] = false;
        }
    }
}

/// <summary>
/// "Invert" flips the whole backing grid in one go, which would switch on the isolation band and
/// every other layer and desynchronise the path finder's area cache. Flip only real floor cells of
/// the layer the player is looking at, through the normal per-cell setter.
/// </summary>
[HarmonyPatch(typeof(Area), nameof(Area.Invert))]
internal static class AreaInvertGate
{
    static bool Prefix(Area __instance)
    {
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner == null) return true;
        var live = new List<IntVec3>();
        foreach (var cell in owner.LayerRect(owner.ViewedLayer))
            if (owner.Structure.HasSurface(cell)) live.Add(cell);
        foreach (var cell in live) __instance[cell] = AreaLayerRules.InvertValue(__instance[cell], true);
        return false;
    }
}
