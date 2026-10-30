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
        Map source = core.Map;
        LayerMapComponent? owner = LayerMapComponent.For(source);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return;
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
        TerrainGrid source = __state.Source.terrainGrid;
        LayerMapComponent? owner = LayerMapComponent.For(__state.Source);
        using (Sos2TerrainPatches.Suppressed())
            foreach (IntVec3 cell in __state.Cells)
            {
                if (!StructuralSupport.IsSos2DeckTerrain(source.FoundationAt(cell))) continue;
                source.RemoveFoundation(cell, false);
                owner?.Structure.TerrainChanged(cell, source.TerrainAt(cell));
            }
    }

    private sealed record Move(Map Source, Map Target, IntVec3 Adjustment, byte RotNum, List<IntVec3> Cells);
}
