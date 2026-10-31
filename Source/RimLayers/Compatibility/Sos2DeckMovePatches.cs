using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Ships;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Carries <c>RL_Sos2Deck</c> foundations along when SOS2 moves a ship to another map.
/// </summary>
/// <remarks>
/// <c>MoveShip</c> copies terrain through <c>terrainToCopy</c>, which it only fills from
/// <c>if (sourceTerrain.layerable)</c>. Both RimLayers decks are <c>isFoundation=true</c> +
/// <c>layerable=false</c> - that is what makes them real floors rather than a paint layer - so they are
/// silently skipped and the hull arrives in space with its decks left behind.
/// <para>
/// The native loop cannot be reused: <c>SetTerrain</c> on a foundation def writes the top layer and
/// never reaches <c>foundationGrid</c>. This records the deck cells before the move, lays them on the
/// target map with <see cref="ShipDeckTerrain.InstallDeck"/>, and clears them from the source so the
/// ship actually leaves.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2DeckMovePatch
{
    private static MethodBase? TargetMethod()
    {
        MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipInteriorType, "MoveShip", new[]
        {
            typeof(Building), typeof(Map), typeof(IntVec3), typeof(Faction), typeof(byte), typeof(bool), typeof(bool)
        });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static MethodInfo? transform;

    private static void Prefix(Building core, Map targetMap, IntVec3 adjustment, byte rotNum, out Move? __state)
    {
        __state = null;
        if (!Sos2Compatibility.Active || core?.Map == null || targetMap == null) return;

        // The target must be at least as tall as the hull before anything is written into it.
        // MapShipCells, the home area and the terrain loop all index the target with storage
        // coordinates taken from the source, so a hull that gained a deck upstairs and then lands on a
        // map generated with fewer bands throws on the first out-of-range cell - mid-move, with half
        // the hull already transferred. Growing here covers launch, landing, and in-place moves alike,
        // because every one of them goes through MoveShip.
        Generation.LiveMapExpansion.EnsureRequirements(targetMap);

        Map source = core.Map;
        LayerMapComponent? owner = LayerMapComponent.For(source);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return;
        // The requirement queries answer "how tall is this map", and the hull that needs the height is
        // still on the source map, so landing on a map generated with fewer bands reports zero and
        // grows nothing. Ask for the elevation the source hull actually occupies instead.
        int hullElevation = 0;
        if (Sos2Compatibility.ShipCellsOf(source) is { } claims)
            foreach (object? key in claims.Keys)
                if (key is IntVec3 claimCell && owner.TryDecode(claimCell, out LayerCell claimed))
                    hullElevation = Math.Max(hullElevation, owner.Elevation(claimed.LayerId));
        if (hullElevation > 0) Generation.LiveMapExpansion.EnsureElevationFor(targetMap, hullElevation);
        var decks = new List<IntVec3>();
        for (int layer = 1; layer < layout.LayerCount; layer++)
            foreach (IntVec3 cell in owner.LayerRect(layer))
                if (StructuralSupport.IsSos2DeckTerrain(source.terrainGrid.FoundationAt(cell)))
                    decks.Add(cell);
        if (decks.Count == 0) return;
        __state = new Move(source, targetMap, adjustment, rotNum, decks);
    }

    private static void Postfix(Move? __state)
    {
        if (__state == null || !Sos2Compatibility.Active) return;
        TerrainDef? deck = StructuralSupport.Sos2Deck;
        if (deck == null) return;
        LayerMapComponent? target = LayerMapComponent.For(__state.Target);
        if (target?.Layout is not { LayerCount: > 1 }) return;
        transform ??= AccessTools.Method(Sos2Compatibility.ShipInteriorType, "TransformForShipMove",
            new[] { typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(int) });
        if (transform == null) return;

        foreach (IntVec3 cell in __state.Cells)
        {
            // The same band-preserving transform every other part of the hull uses. Our prefix owns
            // TransformForShipMove, so a rotated move keeps each deck on its own layer.
            if (transform.Invoke(null, new object[] { __state.Target, cell, __state.Adjustment, 4 - __state.RotNum })
                is not IntVec3 moved) continue;
            if (!target.TryDecode(moved, out LayerCell logical) || logical.LayerId == 0) continue;
            ShipDeckTerrain.InstallDeck(target, moved, deck);
        }

        // A deck that stays behind would let the player build on a hull that has already left, so the
        // source loses it. The bookkeeping has to go through the source map's own component: the cell
        // coordinates are the source's, not the target's.
        //
        // The wider suppression matters here. <c>RemoveFoundation</c> ends in
        // <c>DoTerrainChangedEffects</c>, and Vehicle Framework attaches a prefix to that method which
        // dereferences per-map vehicle pathing state. A departing ship's map is about to be disposed, so
        // there is no pathing state to update and no blueprint, designation or region worth refreshing -
        // letting the chain run only gives it a chance to throw and abort the flight.
        TerrainGrid source = __state.Source.terrainGrid;
        LayerMapComponent? owner = LayerMapComponent.For(__state.Source);
        // A move inside one map can put the hull down where part of it already stood. Removing the deck
        // from every source cell afterwards would then strip the deck that was just installed on the
        // overlapping cells, and the player is left with a floating hull frame - walls and turrets on
        // bare open air. Cells the destination took over are therefore dropped from the cleanup list.
        var vacated = new HashSet<IntVec3>(__state.Cells);
        if (ReferenceEquals(__state.Source, __state.Target))
            foreach (IntVec3 cell in __state.Cells)
                if (transform.Invoke(null, new object[] { __state.Target, cell, __state.Adjustment, 4 - __state.RotNum })
                    is IntVec3 moved) vacated.Remove(moved);
        // Three layers of protection, each for a different reader. SuppressedEffects() keeps vanilla's
        // terrain-change chain from running at all; Settling() keeps RimLayers' own per-cell path-cost
        // refresh out of the way too, since the map is being abandoned and Vehicle Framework's postfix
        // on Pathing reads vehicle pathing context that this map never had. Both are scoped to the cell
        // loop so anything outside it behaves normally.
        using (Sos2TerrainPatches.SuppressedEffects())
        using (StructuralSupport.Settling())
            foreach (IntVec3 cell in vacated)
            {
                if (!StructuralSupport.IsSos2DeckTerrain(source.FoundationAt(cell))) continue;
                source.RemoveFoundation(cell, false);
                owner?.Structure.TerrainChanged(cell, source.TerrainAt(cell));
            }
    }

    private sealed record Move(Map Source, Map Target, IntVec3 Adjustment, byte RotNum, List<IntVec3> Cells);
}
