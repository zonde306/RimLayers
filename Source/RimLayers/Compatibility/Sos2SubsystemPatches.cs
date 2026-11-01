using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using UnityEngine;
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
    /// <c>CompEngineTrail.PostDraw</c> paints its exhaust trail as a <c>Graphic_Multi</c> at
    /// <c>parent.DrawPos + offset</c> with <c>MoteGlow</c> — outside the map mesh. That bypasses
    /// <see cref="EngineExhaustPatch"/>, which only trims <c>GetExhaustArea</c>'s cell set, so an engine on
    /// the deck below still paints its flame over the deck above. Skip the draw while the player is
    /// looking at another floor; the engine's own floor still gets it.
    /// </summary>
    [HarmonyPatch]
    private static class EngineTrailDrawPatch
    {
        // Discovered rather than listed: SOS2 ships `CompEngineTrail`, and `CompEngineTrailEnergy` exists
        // in its sources but is absent from the shipped assembly because no def references it. A hardcoded
        // list silently decays the moment either side changes, so the painters are found by shape - a
        // ThingComp named *EngineTrail* that paints in PostDraw - and every one found is scoped.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Assembly? sos2 = AccessTools.TypeByName("SaveOurShip2.CompEngineTrail")?.Assembly;
            if (sos2 == null) yield break;
            foreach (Type type in sos2.GetTypes())
            {
                if (!typeof(ThingComp).IsAssignableFrom(type)) continue;
                if (!type.Name.Contains("EngineTrail")) continue;
                MethodInfo? draw = AccessTools.Method(type, "PostDraw", Type.EmptyTypes);
                if (draw != null) yield return draw;
            }
        }

        private static bool Prefix(ThingComp? __instance)
        {
            if (__instance?.parent?.Map is not { } map) return true;
            LayerMapComponent? owner = LayerMapComponent.For(map);
            if (owner?.Layout is not { LayerCount: > 1 } layout) return true;
            if (!owner.TryDecode(__instance.parent.Position, out LayerCell engine)) return true;
            return layout.ElevationOf(engine.LayerId) == layout.ElevationOf(owner.ViewedLayer);
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
            // The space map must keep the ship's band structure: MoveShip walks the source decks cell by
            // cell and mirrors them onto the target. Generated flat, the second deck has nowhere to go.
            Sos2Compatibility.ExpectSpaceLayout(size);
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

    /// <summary>
    /// A shield has to see incoming fire on every deck, not only the one it stands on.
    /// </summary>
    /// <remarks>
    /// SOS2 catches unmodified projectiles in <c>HarmonyPatches.ShieldsWithoutDLC</c>, a postfix on
    /// <c>Projectile.CheckForFreeInterceptBetween</c>. It measures the distance from the projectile to
    /// <c>shield.parent.Position.ToVector3Shifted()</c>, which is <c>new Vector3(x + 0.5f, y, z + 0.5f)</c>
    /// - so the y of that vector is the cell's y. On a flat map y is always 0 and the measurement is
    /// horizontal, which is what the code was written for. On a layered map y carries the band, so a
    /// shield on deck 2 measures 375 units above a projectile flying at y = 0: the distance can never be
    /// inside the radius and the shield silently stops protecting anything. The very next line of that
    /// postfix sets <c>pos.y = lastExactPos.y</c>, which is the author meeting the same problem and
    /// patching the view instead of the measure.
    ///
    /// The transpiler replaces the vector the postfix builds with the shield's cell centre at the
    /// projectile's own altitude, and leaves everything else native: the <c>Projectile_ShipFake</c>
    /// early-out, the radius test, and the call to <c>HitShield</c> that draws the impact cone, plays
    /// the sound, heats the shield's net and explodes it when it gives.
    /// </remarks>
    [HarmonyPatch]
    private static class ShieldSeesEveryDeckPatch
    {
        private static MethodBase? TargetMethod()
        {
            Type? postfixType = AccessTools.TypeByName("SaveOurShip2.ShieldsWithoutDLC");
            MethodInfo? postfix = postfixType == null
                ? null
                : AccessTools.Method(postfixType, "Postfix",
                    new[] { typeof(Projectile), typeof(Vector3), typeof(Vector3), typeof(bool).MakeByRefType() });
            return postfix == null ? null : new HarmonyMethod(postfix).method;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> FlatTheVector(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            MethodInfo toShifted = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.ToVector3Shifted));
            MethodInfo helper = AccessTools.Method(typeof(ShieldSeesEveryDeckPatch), nameof(Flatten));
            int hits = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Call || list[i].operand is not MethodInfo called || called != toShifted) continue;
                hits++;
                // The native call left the shield's corner on the stack with y carrying the band.
                // Hand it the projectile's own altitude and it becomes the same measurement a flat
                // map makes. Nothing else on the stack needs the original value: SOS2 stores the
                // result into a local at IL_0073 and overwrites its y on the very next line.
                list.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Vector3), nameof(Vector3.y))),
                    new CodeInstruction(OpCodes.Call, helper),
                });
                i += 3;
            }
            if (hits != 1)
                throw new InvalidOperationException($"RimLayers SOS2 shield anchor changed: expected 1 call to ToVector3Shifted, found {hits}.");
            return list;
        }

        /// <summary>
        /// The corner of the shield's cell at the projectile's own altitude. y is what carries the band
        /// on a layered map, and SOS2 only overwrites it on the next line, so flattening it here is the
        /// whole fix: the horizontal distance then matches what a flat map would have measured.
        /// </summary>
        private static Vector3 Flatten(Vector3 corner, float altitude)
        {
            corner.y = altitude;
            return corner;
        }
    }
}
