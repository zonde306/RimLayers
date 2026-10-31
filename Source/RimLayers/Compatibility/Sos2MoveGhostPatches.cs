using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Lets SOS2's ship-move ghost validate only the deck that actually touches the ground.
/// </summary>
/// <remarks>
/// <c>PlaceWorker_MoveShip.AllowsPlacing</c> walks every sketch cell and demands it be
/// <c>InBounds</c>, unroofed and not spawn-blocked. That is correct for a one-deck ship, whose whole
/// footprint lands on existing ground. A multi-deck hull puts its upper decks in empty space: those
/// cells have no floor until the hull itself arrives, so every one of them reports blocked and the
/// ghost paints red over the entire ship on every floor, with no message.
/// <para>
/// Only the lowest deck needs a claim on the destination. The upper decks travel with the hull, and
/// <c>MoveShip</c> writes them into their own storage bands. So this prefix answers the question for
/// the bottom band and accepts the rest as carried.
/// </para>
/// <para>
/// This deliberately does not touch <c>SpaceShipCache.Area</c>. An earlier version swapped that field
/// for a flattened copy and restored it afterwards; the swap made <c>Area.MarkDirty</c> run against a
/// set that did not match the ship, and region registration failed with "&gt; 2 regions on link".
/// A ghost is a display decision, so it is decided here and nowhere else.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2MoveGhostPatches
{
    private static MethodBase? TargetMethod()
    {
        System.Type? worker = AccessTools.TypeByName("SaveOurShip2.PlaceWorker_MoveShip");
        MethodInfo? original = worker == null
            ? null
            : AccessTools.Method(worker, "AllowsPlacing", new[]
            {
                typeof(BuildableDef), typeof(IntVec3), typeof(Rot4), typeof(Map), typeof(Thing), typeof(Thing)
            });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    /// <summary>
    /// Translates the ghost into one band before the native check runs, and puts the real geometry
    /// back afterwards. <c>originMap</c> is left alone: it is read by the move itself.
    /// </summary>
    private static bool Prefix(BuildableDef def, IntVec3 loc, Map map, Thing? thing, ref AcceptanceReport __result)
    {
        if (!Sos2Compatibility.Active) return true;
        // AllowsPlacing is handed the blueprint, not the minified ship inside it. SOS2 reads
        // shipSketch/extenderSketch/canBlastLand off ShipMoveBlueprint and so must this.
        if (thing == null || thing.GetType().Name != "ShipMoveBlueprint") return true;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return true;

        if (AccessTools.Field(thing.GetType(), "shipSketch")?.GetValue(thing) is not Sketch sketch) return true;
        List<SketchEntity> entities = Entities(sketch, thing);
        if (entities.Count == 0) return true;

        // The lowest sketch row is what the hull stands on. Everything above it is carried.
        int floor = int.MaxValue;
        foreach (SketchEntity entity in entities)
            if (entity.pos.z < floor) floor = entity.pos.z;

        bool accept = true;
        string reason = string.Empty;
        foreach (SketchEntity entity in entities)
        {
            // Only cells in the bottom band are asked to prove they can host the ship. A cell in a band
            // the map does not even have yet is also fine: the target is grown to the hull's height
            // before the move (see LiveMapExpansion.EnsureRequirements).
            if (entity.pos.z > floor || entity.pos.z >= layout.Height) continue;
            // The sketch is drawn relative to the ghost's cursor cell. Without loc the whole test is
            // run against the map origin, which is what made the preview red almost everywhere.
            var cell = loc + new IntVec3(entity.pos.x, 0, entity.pos.z);
            if (!cell.InBounds(map))
            {
                accept = false;
                reason = string.Empty;
                break;
            }
            if (GenGrid.InNoBuildEdgeArea(cell, map))
            {
                accept = false;
                continue;
            }
            if (!entity.IsSpawningBlocked(cell, map)) continue;
            if (map.terrainGrid.TerrainAt(cell) == TerrainDefOf.Space) continue;
            // A shore on a space map is normal: the hull brings its own deck. Only a ground map's
            // occupied cell is a real refusal.
            if (map.Tile.Layer.Def.isSpace && map.roofGrid.RoofAt(cell) == null) continue;
            accept = false;
            if (reason.Length == 0) reason = "SoS.BlastLandingUnavailable";
        }

        __result = accept ? AcceptanceReport.WasAccepted : reason.Length == 0 ? AcceptanceReport.WasRejected : new AcceptanceReport(reason.Translate());
        return false;
    }

    private static List<SketchEntity> Entities(Sketch sketch, Thing mover)
    {
        var entities = new List<SketchEntity>(sketch.Entities);
        if (AccessTools.Field(mover.GetType(), "extenderSketch")?.GetValue(mover) is Sketch extender)
            entities.AddRange(extender.Entities);
        return entities;
    }
}
