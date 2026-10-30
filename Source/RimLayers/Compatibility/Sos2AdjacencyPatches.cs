using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Makes SOS2's ship walks cross deck boundaries, and keeps its whole-map sweeps inside the
/// logical rectangle.
/// </summary>
/// <remarks>
/// Harmony binds <c>__instance</c> by name, so these prefixes declare the base
/// <see cref="MapComponent"/> / <see cref="ThingComp"/> types instead of SOS2's own: the adapter has
/// no compile-time reference to ShipsHaveInsides.dll. Every member used is reached through
/// <see cref="Sos2Compatibility"/>'s reflection cache.
/// </remarks>
internal static class Sos2AdjacencyPatches
{
    /// <summary>
    /// Swaps every <c>GenAdj.CellsAdjacentCardinal</c> call inside one SOS2 method for the layered
    /// enumerator. Both overloads are matched because SOS2 mixes them freely.
    /// </summary>
    /// <param name="expected">
    /// How many call sites the method must contain. A silent miss would leave that ship walk on the
    /// old deck-only adjacency with no error anywhere, which is exactly the bug this adapter exists
    /// to fix, so the count is asserted the same way the power and mechanitor transpilers do.
    /// </param>
    internal static IEnumerable<CodeInstruction> ReplaceAdjacency(IEnumerable<CodeInstruction> instructions, string method, int expected)
    {
        MethodInfo? cellOverload = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.CellsAdjacentCardinal), new[] { typeof(IntVec3), typeof(Rot4), typeof(IntVec2) });
        MethodInfo? thingOverload = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.CellsAdjacentCardinal), new[] { typeof(Thing) });
        MethodInfo? cellReplacement = AccessTools.Method(typeof(Sos2CellGraph), nameof(Sos2CellGraph.Cardinal), new[] { typeof(IntVec3), typeof(Rot4), typeof(IntVec2) });
        MethodInfo? thingReplacement = AccessTools.Method(typeof(Sos2CellGraph), nameof(Sos2CellGraph.Cardinal), new[] { typeof(Thing) });
        if (cellOverload == null || thingOverload == null || cellReplacement == null || thingReplacement == null)
            throw new InvalidOperationException("[RimLayers] SOS2 adjacency anchors are missing.");

        var result = new List<CodeInstruction>();
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(cellOverload))
            {
                result.Add(new CodeInstruction(OpCodes.Call, cellReplacement));
                replaced++;
                continue;
            }
            if (instruction.Calls(thingOverload))
            {
                result.Add(new CodeInstruction(OpCodes.Call, thingReplacement));
                replaced++;
                continue;
            }
            result.Add(instruction);
        }
        if (replaced != expected)
            throw new InvalidOperationException($"Unexpected SOS2 adjacency entry count in {method}: {replaced} (expected {expected}).");
        return result;
    }

    /// <summary>
    /// The single place an SOS2 part joins a ship: <c>CompShipCachePart.PostSpawnSetup</c> looks for
    /// an existing ship on a cardinally adjacent cell and merges into it. On a layered map the deck
    /// above is never adjacent, so the part starts a brand new ship instead.
    /// </summary>
    [HarmonyPatch]
    private static class CachePartJoinPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipCachePartType, "PostSpawnSetup", new[] { typeof(bool) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static void Prefix(ThingComp __instance) => Sos2CellGraph.Enter(__instance?.parent?.Map);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceAdjacency(instructions, "CompShipCachePart.PostSpawnSetup", 1);

        [HarmonyPostfix]
        [HarmonyFinalizer]
        private static void Finalizer() => Sos2CellGraph.Leave();
    }

    /// <summary>
    /// <c>ShipMapComp.AttachAll</c> spreads a ship index outward with cardinal adjacency; without the
    /// vertical pairs the spread stops at the deck the core sits on.
    /// </summary>
    [HarmonyPatch]
    private static class AttachAllPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipMapCompType, "AttachAll", new[] { typeof(IntVec3), typeof(int) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static void Prefix(MapComponent __instance) => Sos2CellGraph.Enter(__instance?.map);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceAdjacency(instructions, "ShipMapComp.AttachAll", 2);

        [HarmonyPostfix]
        [HarmonyFinalizer]
        private static void Finalizer() => Sos2CellGraph.Leave();
    }

    /// <summary>
    /// <c>SpaceShipCache.RebuildCache</c> re-derives a ship from its bridge on load. It is the reason
    /// SOS2 never serialises <c>MapShipCells</c>, and therefore the reason a deck the walk cannot
    /// reach silently disappears after a save/load.
    /// </summary>
    [HarmonyPatch]
    private static class RebuildCachePatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "RebuildCache");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static void Prefix(Building origin) => Sos2CellGraph.Enter(origin?.Map);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceAdjacency(instructions, "SpaceShipCache.RebuildCache", 1);

        [HarmonyPostfix]
        [HarmonyFinalizer]
        private static void Finalizer() => Sos2CellGraph.Leave();
    }

    /// <summary>
    /// <c>SpaceShipCache.RebuildCorePath</c> re-derives the distance-to-core number SOS2 uses to pick
    /// which hull hole foam gets poured into first. It walks cardinal adjacency from the bridge, so on
    /// a layered ship it only numbers the bridge's own deck; the upper decks keep whatever path they
    /// had. The prefix also seeds the core cell when it is missing: the native first statement is an
    /// unguarded <c>MapShipCells[Core.Position]</c>, which throws and takes the whole attach with it
    /// whenever a bridge is placed before the plating under it.
    /// </summary>
    [HarmonyPatch]
    private static class RebuildCorePathPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "RebuildCorePath");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance)
        {
            if (__instance == null) return;
            Type type = __instance.GetType();
            object? core = AccessTools.Field(type, "Core")?.GetValue(__instance);
            object? mapComp = AccessTools.Field(type, "mapComp")?.GetValue(__instance);
            if (core is not Building bridge || mapComp is not MapComponent comp) return;
            Sos2CellGraph.Enter(bridge.Map);
            if (Sos2Compatibility.ShipCellsOf(comp) is not { } cells) return;
            if (cells.Contains(bridge.Position)) return;
            int index = AccessTools.Field(type, "Index")?.GetValue(__instance) is int value ? value : -1;
            cells[bridge.Position] = new Tuple<int, int>(index, -1);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceAdjacency(instructions, "SpaceShipCache.RebuildCorePath", 1);

        [HarmonyPostfix]
        [HarmonyFinalizer]
        private static void Finalizer() => Sos2CellGraph.Leave();
    }

    /// <summary>
    /// <c>SpaceShipCache.CheckForDetach</c> decides whether a chunk that lost its hull is still tied to
    /// the ship by walking cardinal adjacency back toward the core. On a layered ship the walk cannot
    /// see the deck above, so losing one hull tile on the lower deck reports the whole upper deck as
    /// detached and tears it off as a separate ship.
    /// </summary>
    [HarmonyPatch]
    private static class CheckForDetachPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "CheckForDetach", new[] { typeof(List<IntVec3>) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance)
        {
            if (__instance == null) return;
            Map? map = AccessTools.Field(__instance.GetType(), "map")?.GetValue(__instance) as Map;
            Sos2CellGraph.Enter(map);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceAdjacency(instructions, "SpaceShipCache.CheckForDetach", 3);

        [HarmonyPostfix]
        [HarmonyFinalizer]
        private static void Finalizer() => Sos2CellGraph.Leave();
    }

    /// <summary>
    /// <c>ShipMapComp.KillAllOffShip</c> destroys every thing in <c>map.AllCells</c> that is not in
    /// <c>MapShipCells</c>. On a layered map <c>AllCells</c> also covers the storage padding bands and
    /// every deck, so the sweep would delete the mod's isolation cells and everything standing on
    /// them. The prefix replaces the body with the same sweep restricted to the ground band.
    /// </summary>
    [HarmonyPatch]
    private static class KillAllOffShipPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipMapCompType, "KillAllOffShip");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(MapComponent __instance)
        {
            Map map = __instance.map;
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out _, out _, out _)) return true;
            if (Sos2Compatibility.ShipCellsOf(__instance) is not { } cells) return true;

            var pawns = new List<Pawn>();
            var things = new HashSet<Thing>();
            foreach (IntVec3 v in owner.LayerRect(0))
            {
                if (cells.Contains(v)) continue;
                foreach (Thing t in v.GetThingList(map))
                {
                    if (t is Pawn p) pawns.Add(p);
                    things.Add(t);
                }
            }
            foreach (Thing t in things) t.Destroy();
            foreach (Pawn p in pawns) p.Kill(new DamageInfo(DamageDefOf.Crush, 10000));
            return false;
        }
    }
}
