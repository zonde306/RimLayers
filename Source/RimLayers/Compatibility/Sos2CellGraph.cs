using System.Collections.Generic;
using RimLayers.Core;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Drop-in replacements for <c>GenAdj.CellsAdjacentCardinal</c> inside SOS2's ship algorithms.
/// </summary>
/// <remarks>
/// SOS2 walks a ship with same-layer cardinal adjacency everywhere. On a layered map that stops at
/// the deck boundary, so an upper deck never joins the ship: its parts are not cached, they are not
/// repaired by foam, they get no heat net and they are not pressurised. Rather than rewrite SOS2's
/// breadth-first searches, the adapter swaps only the neighbour enumeration, which is the single
/// point where the deck boundary is decided. Callers keep their own membership filters
/// (<c>MapShipCells.ContainsKey</c>, <c>Area.Contains</c>), so the extra vertical pairs cannot claim
/// cells SOS2 did not already track.
/// </remarks>
internal static class Sos2CellGraph
{
    /// <summary>
    /// The map whose layout answers the band arithmetic. Set by the enclosing SOS2 method's prefix,
    /// because the original call sites pass no map to the adjacency helper.
    /// </summary>
    [System.ThreadStatic] private static Map? current;

    internal static Map? Current
    {
        get => current;
        set => current = value;
    }

    internal static void Enter(Map? map) => current = map;
    internal static void Leave() => current = null;

    private static bool TryLayout(out LayerMapComponent owner)
    {
        owner = null!;
        Map? map = current;
        if (map == null) return false;
        LayerMapComponent? component = LayerMapComponent.For(map);
        if (component?.Layout == null || component.Layout.LayerCount < 2) return false;
        owner = component;
        return true;
    }

    private static IEnumerable<IntVec3> Vertical(Map map, IntVec3 cell)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout == null || owner.Layout.LayerCount < 2) yield break;
        IntVec3 above = owner.Adjacent(cell, 1);
        if (above.IsValid) yield return above;
        IntVec3 below = owner.Adjacent(cell, -1);
        if (below.IsValid) yield return below;
    }

    /// <summary>Cardinal neighbours of a cell, plus the two cells stacked on it.</summary>
    internal static IEnumerable<IntVec3> Cardinal(IntVec3 cell, Rot4 rot, IntVec2 size)
    {
        foreach (IntVec3 at in GenAdj.CellsAdjacentCardinal(cell, rot, size)) yield return at;
        if (!TryLayout(out var owner)) yield break;
        foreach (IntVec3 at in Vertical(owner.map, cell)) yield return at;
    }

    /// <summary>Cardinal neighbours of every occupied cell of a thing, plus the stacked cells.</summary>
    internal static IEnumerable<IntVec3> Cardinal(Thing thing)
    {
        foreach (IntVec3 at in GenAdj.CellsAdjacentCardinal(thing)) yield return at;
        if (!TryLayout(out var owner)) yield break;
        // Every occupied cell, not just the anchor: a 2x2 reactor must reach the deck above from
        // whichever of its cells is actually under the hatch.
        foreach (IntVec3 occupied in GenAdj.CellsOccupiedBy(thing))
        foreach (IntVec3 at in Vertical(owner.map, occupied))
            yield return at;
    }

    /// <summary>Read-only view of the band the cell belongs to, for patch bodies that need it.</summary>
    internal static bool BandOf(Map? map, out LayerMapComponent owner, out int stride, out int height, out int layers)
    {
        owner = null!;
        stride = height = layers = 0;
        if (map == null) return false;
        LayerMapComponent? component = LayerMapComponent.For(map);
        if (component?.Layout == null) return false;
        owner = component;
        stride = component.Layout.Stride;
        height = component.Layout.Height;
        layers = component.Layout.LayerCount;
        return layers > 1;
    }
}
