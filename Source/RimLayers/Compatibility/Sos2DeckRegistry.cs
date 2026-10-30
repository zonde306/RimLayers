using System;
using System.Collections;
using System.Collections.Generic;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Structure;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Teaches SOS2 that an <c>RL_Sos2Deck</c> cell above a hull belongs to that hull.
/// </summary>
/// <remarks>
/// SOS2 builds its notion of "which ship owns this cell" (<c>MapShipCells</c>) exclusively from
/// buildings carrying <c>CompShipCachePart</c>. A RimLayers deck is pure terrain: nothing stands on
/// the cell, so it never enters that table, <c>ShipIndexOnVec</c> answers -1 for it, and every part
/// standing there takes the "not on a ship" branch of <c>CompShipCachePart.PostSpawnSetup</c>. The
/// part is therefore never added to <c>SpaceShipCache.Buildings</c>, its <c>ShipIndex</c> stays -1,
/// and reading the ship name or the dev-mode ship index on it dereferences
/// <c>ShipsOnMap[-1]</c> - the <c>KeyNotFoundException</c> a bridge on an upper deck throws.
/// <para>
/// Rather than teach forty-odd call sites about decks, this registers the deck cells into the two
/// caches SOS2 already reads: the cell table and the owning ship's <c>Area</c>. Everything downstream
/// (heat nets, foam, breach, pressurisation, ship extents, facility linking) then sees the upper decks
/// as ordinary ship cells.
/// </para>
/// </remarks>
internal static class Sos2DeckRegistry
{
    /// <summary>
    /// The cells this class itself added to SOS2's table.
    /// </summary>
    /// <remarks>
    /// Ownership has to be tracked explicitly. A sweep cannot ask "is this cell an upper layer?" and
    /// sweep the rest, because SOS2 legitimately claims upper cells too: a hull tile laid on the deck
    /// above registers itself through <c>CompShipCachePart</c>. Removing those dropped the whole upper
    /// hull out of the ship (upperCells=0), which then split the heat nets because the vertical edge's
    /// ship-identity check compared an owned cell against an unclaimed one.
    /// </remarks>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Map, HashSet<IntVec3>> Owned = new();

    private static HashSet<IntVec3> OwnedOn(Map map) =>
        Owned.GetValue(map, _ => new HashSet<IntVec3>());

    /// <summary>Registers every deck cell on the map. Used after SOS2 rebuilds its caches.</summary>
    internal static void SyncAll(Map? map)
    {
        if (!Sos2Compatibility.Active || map == null) return;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return;
        for (int layer = 1; layer < layout.LayerCount; layer++)
            foreach (IntVec3 cell in owner.LayerRect(layer))
                Register(map, owner, cell);
    }

    /// <summary>Re-resolves one cell. Used when a deck is laid, removed or replaced.</summary>
    internal static void SyncCell(Map? map, IntVec3 cell)
    {
        if (!Sos2Compatibility.Active || map == null) return;
        Register(map, LayerMapComponent.For(map), cell);
    }

    private static void Register(Map map, LayerMapComponent? owner, IntVec3 cell)
    {
        if (owner?.Layout is not { LayerCount: > 1 } layout) return;
        if (!owner.TryDecode(cell, out LayerCell logical) || logical.LayerId == 0) return;
        IDictionary? cells = Sos2Compatibility.ShipCellsOf(map);
        IDictionary? ships = Sos2Compatibility.ShipsOnMapOf(map);
        if (cells == null || ships == null) return;

        bool deck = StructuralSupport.IsSos2DeckTerrain(map.terrainGrid.TerrainAt(cell)) ||
                    StructuralSupport.IsSos2DeckTerrain(map.terrainGrid.FoundationAt(cell));
        HashSet<IntVec3> owned = OwnedOn(map);
        if (!deck)
        {
            if (owned.Contains(cell)) Unregister(cells, ships, cell, owned);
            return;
        }

        // Walk down the column to the first cell SOS2 already claims. Looking at the table directly
        // (rather than asking ShipIndexOnVec) keeps this non-recursive and independent of any patch.
        int index = -1;
        int path = -1;
        for (int layer = logical.LayerId - 1; layer >= 0; layer--)
        {
            IntVec3 below = owner.ToStorage(new LayerCell(layer, logical.X, logical.Z));
            if (!cells.Contains(below)) continue;
            if (cells[below] is not Tuple<int, int> claim) continue;
            if (!ships.Contains(claim.Item1)) continue;
            index = claim.Item1;
            path = claim.Item2 >= 0 ? claim.Item2 + 1 : -1;
            break;
        }
        if (index == -1)
        {
            if (owned.Contains(cell)) Unregister(cells, ships, cell, owned);
            return;
        }

        cells[cell] = new Tuple<int, int>(index, path);
        owned.Add(cell);
        if (Sos2Compatibility.ShipAreaOf(ships, index) is { } area) area.Add(cell);
        RejoinBuildings(map, cell, ships, index);
    }

    /// <summary>
    /// Adopts whatever already stands on a freshly registered deck cell.
    /// </summary>
    /// <remarks>
    /// On a save load SOS2 rebuilds from the bridge by walking adjacency, and the deck cells only
    /// join the walk once this class has registered them - which happens after that walk finished. A
    /// part that has been standing on the upper deck since the last save is not picked up by it, so
    /// it keeps <c>ShipIndex == -1</c> and the very inspection that reported the bug still throws.
    /// <c>AddToCache</c> is the same entry point the walk uses and guards with <c>Buildings.Add</c>,
    /// so re-adopting an already cached part is a no-op.
    /// </remarks>
    private static void RejoinBuildings(Map map, IntVec3 cell, IDictionary ships, int index)
    {
        object? ship = Sos2Compatibility.ShipOf(ships, index);
        if (ship == null) return;
        foreach (Thing thing in cell.GetThingList(map))
        {
            if (thing is Building building && Sos2Compatibility.HasShipCachePart(building))
                Sos2Compatibility.AddBuildingToShip(ship, building);
        }
    }

    private static void Unregister(IDictionary cells, IDictionary ships, IntVec3 cell, HashSet<IntVec3> owned)
    {
        owned.Remove(cell);
        if (!cells.Contains(cell)) return;
        object? claim = cells[cell];
        cells.Remove(cell);
        if (claim is Tuple<int, int> tuple && Sos2Compatibility.ShipAreaOf(ships, tuple.Item1) is { } area)
            area.Remove(cell);
    }
}
