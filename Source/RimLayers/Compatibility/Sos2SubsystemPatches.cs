using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Per-subsystem SOS2 fixes. Everything here exists because one SOS2 subsystem treats a storage
/// coordinate as if it were a logical one, or walks a ship with same-layer adjacency.
/// </summary>
/// <remarks>
/// Each class is applied in isolation by <see cref="Sos2Compatibility.Install"/>, so a reshaped SOS2
/// costs one subsystem instead of the whole adapter. The pattern is always the same: reject the
/// cross-deck answer the native code would give, and let the native code keep owning everything else.
/// </remarks>
internal static class Sos2SubsystemPatches
{
    /// <summary>
    /// Engine exhaust is a 2D <c>CellRect</c> grown off the engine footprint. Near the top of a band
    /// it spills into the padding row and, past that, into the next band — where it would burn a
    /// completely different deck. Keep only the cells on the engine's own deck.
    /// </summary>
    [HarmonyPatch]
    private static class EngineExhaustPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.CompEngineTrail"),
                "GetExhaustArea", new[] { typeof(IntVec3), typeof(Rot4), typeof(ThingDef), typeof(Map) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPostfix]
        private static void Postfix(IntVec3 position, Map map, ref HashSet<IntVec3> __result)
        {
            if (__result == null || __result.Count == 0) return;
            LayerMapComponent? owner = LayerMapComponent.For(map);
            if (owner?.Layout == null || owner.Layout.LayerCount < 2) return;
            if (!owner.TryDecode(position, out LayerCell origin)) return;
            __result.RemoveWhere(cell =>
                !owner.TryDecode(cell, out LayerCell logical) || logical.LayerId != origin.LayerId);
        }
    }

    /// <summary>
    /// Heat sinks bleed into the room they touch. <c>TryPushHeat</c> pushes into an arbitrary cell via
    /// <c>GenTemperature.PushHeat</c>, which is a storage-cell write: without this guard a sink on the
    /// deck above warms the room on the deck below, and the <c>CellsAdjacent8Way</c> walk in
    /// <c>PushHeat</c> can even pick a neighbour in the band above.
    /// </summary>
    [HarmonyPatch]
    private static class HeatSinkPushPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.CompShipHeatSink"),
                "TryPushHeat", new[] { typeof(IntVec3), typeof(float) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(ThingComp? __instance, IntVec3 vec, ref bool __result)
        {
            if (__instance?.parent?.Map is not { } map) return true;
            if (Sos2Compatibility.SameLayer(map, __instance.parent.Position, vec)) return true;
            // The sink is a wall and the room is on another deck. Report "no room accepted the heat"
            // so the caller keeps looking at its own deck instead of silently heating the one below.
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// A shuttle bay only holds a shuttle on its own deck. <c>CanLaunchShuttle</c> tests a same-layer
    /// <c>CellRect</c>, which on a layered map can only ever be false — but the containment test is an
    /// accident of the storage layout, not a decision, so state it explicitly.
    /// </summary>
    [HarmonyPatch]
    private static class BayShuttleScopePatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.CompShipBay"),
                "CanLaunchShuttle", new[] { AccessTools.TypeByName("Vehicles.VehiclePawn")! });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(ThingComp? __instance, Thing? vehicle, ref bool __result)
        {
            if (__instance?.parent?.Map is not { } map || vehicle == null) return true;
            if (Sos2Compatibility.SameLayer(map, __instance.parent.Position, vehicle.Position)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Every SOS2 caller of <c>GeneratePlayerShipMap</c> passes <c>Map.Size</c>, which on a layered map
    /// is the storage height (every band plus the padding). The space map would then be generated
    /// several times too tall. Substitute the logical length before the generator sees it.
    /// </summary>
    [HarmonyPatch]
    private static class BridgeMapSizePatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipInteriorType,
                "GeneratePlayerShipMap", new[] { typeof(IntVec3) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static void Prefix(ref IntVec3 size)
        {
            size = Sos2Compatibility.LogicalSize(size);
        }
    }

    /// <summary>
    /// Incoming ship-to-ship fire spawns at a random edge cell of the target map. Both edge helpers
    /// read <c>Map.Size.z</c> as the north/south length, so on a layered map they spawn the projectile
    /// inside the storage gap where it can never reach the ship it is aimed at.
    /// </summary>
    [HarmonyPatch]
    private static class CombatEdgeCellPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipMapCompType,
                "FindClosestEdgeCellLowSpread", new[] { typeof(Map), typeof(IntVec3) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(Map map, IntVec3 targetCell, ref IntVec3 __result)
        {
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            // Aim from the edge of the band the ship is actually in, not from the map's storage edge.
            int layer = owner.TryDecode(targetCell, out LayerCell logical) ? logical.LayerId : 0;
            int bandStart = layer * stride;
            int width = owner.Layout!.Width;
            int side = Sos2Compatibility.ProjectileSide(map, targetCell);
            int spread = side <= 1 ? height : width;
            int jitter = spread <= 1 ? 0 : Rand.Range(-spread / 4, spread / 4);
            (int x, int y, int z) = Sos2Geometry.EdgeCell(
                targetCell.x, 0, bandStart + Sos2Geometry.LocalZ(targetCell.z, stride),
                width, height, stride, layers, jitter, side);
            __result = new IntVec3(x, y, z);
            return false;
        }
    }

    /// <summary>
    /// The same edge defect for <c>FindClosestEdgeCell</c>, which asks <c>CellFinder.RandomEdgeCell</c>
    /// for a random cell on a map edge. On a layered map that is an edge of the whole storage
    /// rectangle, so the cell can sit in any band.
    /// </summary>
    [HarmonyPatch]
    private static class CombatRandomEdgeCellPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipMapCompType,
                "FindClosestEdgeCell", new[] { typeof(Map), typeof(IntVec3) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(Map map, IntVec3 targetCell, ref IntVec3 __result)
        {
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            int layer = owner.TryDecode(targetCell, out LayerCell logical) ? logical.LayerId : 0;
            int width = owner.Layout!.Width;
            int side = Sos2Compatibility.ProjectileSide(map, targetCell);
            (int x, int y, int z) = Sos2Geometry.EdgeCell(
                targetCell.x, 0, layer * stride + Sos2Geometry.LocalZ(targetCell.z, stride),
                width, height, stride, layers, 0, side);
            __result = new IntVec3(x, y, z);
            return false;
        }
    }
}
