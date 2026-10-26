using HarmonyLib;
using RimLayers.Core;
using RimLayers.Environment;
using RimLayers.Generation;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Cross-layer pipe risers and deep resource / geyser suppression for:
/// - Vanilla Chemfuel Expanded (packageId: VanillaExpanded.VChemfuelE)
/// - Vanilla Helixien Gas Expanded (packageId: VanillaExpanded.HelixienGas)
/// - Vanilla Nutrient Paste Expanded (packageId: VanillaExpanded.VNutrientE)
///
/// Ensures pipe networks can bridge layers via vertical risers (CompResourcePort), while
/// deep-crust resources (Helixien gas deposits, gas geysers, deepchem pumps) are restricted
/// to ground and dug strata (elevation &lt;= 0) so they never float on upper floors (elevation &gt; 0).
/// </summary>
[StaticConstructorOnStartup]
public static class VEPipeSystemCompatibility
{
    private static bool installed;

    public static bool Active => installed;

    static VEPipeSystemCompatibility()
    {
        try { Install(new Harmony(RimLayersMod.HarmonyId + ".vepipes")); }
        catch (Exception e) { Log.Error($"[RimLayers] VE PipeSystem compatibility setup failed: {e}"); }
    }

    public static void Install(Harmony harmony)
    {
        if (installed) return;

        // 1. Ensure VEF PipeLinks is installed (cross-deck adjacency for all VEF pipe networks)
        PipeLinks.Install(harmony);
        PipeLinks.RegisterRiser("RL_ChemfuelPipeRiser");
        PipeLinks.RegisterRiser("RL_DeepchemPipeRiser");
        PipeLinks.RegisterRiser("RL_HelixienPipeRiser");
        PipeLinks.RegisterRiser("RL_NutrientPastePipeRiser");

        // 2. Hook DeepResourceScope.PlacementReport to catch VEF CompDeepExtractor & VHGE Placeworker_Pump
        var placementReportMethod = AccessTools.Method(typeof(DeepResourceScope), nameof(DeepResourceScope.PlacementReport));
        if (placementReportMethod != null)
        {
            harmony.Patch(placementReportMethod,
                postfix: new HarmonyMethod(typeof(VEPipeSystemCompatibility), nameof(PlacementReportPostfix)));
        }

        // 3. Hook PlaceWorker_DeepExtractor.AllowsPlacing if PipeSystem is loaded
        if (AccessTools.TypeByName("PipeSystem.PlaceWorker_DeepExtractor") is { } deepExtractorType)
        {
            var allowsPlacing = AccessTools.Method(deepExtractorType, "AllowsPlacing");
            if (allowsPlacing != null)
            {
                harmony.Patch(allowsPlacing,
                    prefix: new HarmonyMethod(typeof(VEPipeSystemCompatibility), nameof(AllowsPlacingPrefix)));
            }
        }

        // 4. Hook VHGE Placeworker_Pump and HelixienGasHandler if VHGE is loaded
        if (AccessTools.TypeByName("VHelixienGasE.Placeworker_Pump") is { } vhgePumpType)
        {
            var allowsPlacing = AccessTools.Method(vhgePumpType, "AllowsPlacing");
            if (allowsPlacing != null)
            {
                harmony.Patch(allowsPlacing,
                    prefix: new HarmonyMethod(typeof(VEPipeSystemCompatibility), nameof(AllowsPlacingPrefix)));
            }
        }

        if (AccessTools.TypeByName("VHelixienGasE.HelixienGasHandler") is { } gasHandlerType)
        {
            var canScatter = AccessTools.Method(gasHandlerType, "CanScatterAt", new[] { typeof(IntVec3), typeof(Map) });
            if (canScatter != null)
            {
                harmony.Patch(canScatter,
                    prefix: new HarmonyMethod(typeof(VEPipeSystemCompatibility), nameof(CanScatterAtPrefix)));
            }

            var canSpawn = AccessTools.Method(gasHandlerType, "CanSpawnAt");
            if (canSpawn != null)
            {
                harmony.Patch(canSpawn,
                    prefix: new HarmonyMethod(typeof(VEPipeSystemCompatibility), nameof(CanSpawnAtPrefix)));
            }
        }

        // 5. Subscribe to layer expansion commit so PipeNetManager grids grow to cover all layers
        LiveMapExpansion.Committed += OnLayerExpansionCommitted;

        installed = true;
    }

    private static void OnLayerExpansionCommitted(Map map, LayerLayout layout)
    {
        if (map == null) return;
        try
        {
            RepairVefGrids(map);
        }
        catch (Exception e)
        {
            Log.Error($"[RimLayers] Failed to resize VEF pipe grids on layer expansion: {e}");
        }
    }

    internal static void RepairVefGrids(Map map)
    {
        if (map?.components == null) return;

        // 1. Repair PipeNetManager: valveGrid and pipeNet.networkGrid for all active nets
        foreach (var comp in map.components)
        {
            if (comp == null) continue;
            var typeName = comp.GetType().Name;

            if (typeName == "PipeNetManager")
            {
                if (AccessTools.Field(comp.GetType(), "valveGrid")?.GetValue(comp) is BoolGrid vg &&
                    (vg.Width != map.Size.x || vg.Height != map.Size.z))
                {
                    var on = new List<IntVec3>(vg.ActiveCells);
                    vg.ClearAndResizeTo(map);
                    foreach (var c in on) vg[c] = true;
                }

                if (AccessTools.Field(comp.GetType(), "pipeNets")?.GetValue(comp) is System.Collections.IEnumerable nets)
                {
                    foreach (var net in nets)
                    {
                        if (net == null) continue;
                        if (AccessTools.Field(net.GetType(), "networkGrid")?.GetValue(net) is BoolGrid ng &&
                            (ng.Width != map.Size.x || ng.Height != map.Size.z))
                        {
                            var on = new List<IntVec3>(ng.ActiveCells);
                            ng.ClearAndResizeTo(map);
                            foreach (var c in on) ng[c] = true;
                        }
                    }
                }
            }
            else if (typeName == "HelixienGasHandler")
            {
                if (AccessTools.Field(comp.GetType(), "infiniteGasGrid")?.GetValue(comp) is BoolGrid ig &&
                    (ig.Width != map.Size.x || ig.Height != map.Size.z))
                {
                    var on = new List<IntVec3>(ig.ActiveCells);
                    ig.ClearAndResizeTo(map);
                    foreach (var c in on) ig[c] = true;
                }
            }
        }
    }

    /// <summary>
    /// Checks whether a building extracts deep underground resources (e.g. deepchem pumpjack or helixien gas pump).
    /// </summary>
    public static bool IsDeepExtractorBuilding(ThingDef def)
    {
        if (def == null) return false;
        if (def.defName is "VCHE_DeepchemPumpjack" or "VHGE_HelixienPump") return true;

        if (def.placeWorkers != null)
        {
            foreach (var pw in def.placeWorkers)
            {
                if (pw.Name is "PlaceWorker_DeepExtractor" or "Placeworker_Pump") return true;
            }
        }

        if (def.comps != null)
        {
            foreach (var comp in def.comps)
            {
                if (comp.compClass?.Name == "CompDeepExtractor" ||
                    comp.GetType().Name == "CompProperties_DeepExtractor") return true;
            }
        }

        return false;
    }

    private static void PlacementReportPostfix(BuildableDef? def, IntVec3 center, Map? map, ref AcceptanceReport? __result)
    {
        if (__result != null || def is not ThingDef thing || map == null) return;
        if (IsDeepExtractorBuilding(thing))
        {
            var owner = LayerMapComponent.For(map);
            if (owner != null && owner.TryDecode(center, out var local) && !DeepResourceScope.LayerAllowed(owner, local.LayerId))
            {
                __result = DeepResourceScope.UpperLayerReasonKey.Translate();
            }
        }
    }

    /// <summary>
    /// Note: Both PlaceWorker_DeepExtractor and Placeworker_Pump are exclusively used by deep extractor
    /// buildings (deepchem pumpjacks, helixien pumps). We additionally filter with IsDeepExtractorBuilding
    /// to guarantee normal pipes or non-deep structures are never inadvertently blocked if placeWorkers are shared.
    /// </summary>
    private static bool AllowsPlacingPrefix(BuildableDef checkingDef, IntVec3 loc, Map map, ref AcceptanceReport __result)
    {
        if (checkingDef is ThingDef thing && !IsDeepExtractorBuilding(thing)) return true;

        if (map != null && LayerMapComponent.For(map) is { } owner &&
            owner.TryDecode(loc, out var local) && !DeepResourceScope.LayerAllowed(owner, local.LayerId))
        {
            __result = DeepResourceScope.UpperLayerReasonKey.Translate();
            return false;
        }
        return true;
    }

    private static bool CanScatterAtPrefix(IntVec3 pos, Map map, ref bool __result)
    {
        if (map != null && LayerMapComponent.For(map) is { } owner &&
            owner.TryDecode(pos, out var local) && !DeepResourceScope.LayerAllowed(owner, local.LayerId))
        {
            __result = false;
            return false;
        }
        return true;
    }

    private static bool CanSpawnAtPrefix(IntVec3 x, Map map, ref bool __result)
    {
        if (map != null && LayerMapComponent.For(map) is { } owner &&
            owner.TryDecode(x, out var local) && !DeepResourceScope.LayerAllowed(owner, local.LayerId))
        {
            __result = false;
            return false;
        }
        return true;
    }
}
