using System.Collections.Generic;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Ships;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Moves a multi-deck SOS2 hull inside one map: rotation, flip and reposition, without going through
/// <c>ShipInteriorMod2.MoveShip</c>.
/// </summary>
/// <remarks>
/// SOS2's own move is written for a hull that fits in one map band. It mirrors cells against
/// <c>Map.Size</c>, which on a layered map spans every band, and it walks all of this mod's bookkeeping
/// through the same table it rewrites, so any disagreement between the footprint it is given and the
/// table it reads lands as a half-moved hull. Correcting the geometry it sees (a flattened
/// <c>Area</c>, a rewritten <c>Map.Size</c>) only moves the disagreement, because the mover reads those
/// same values again to decide what belongs to the ship. That produced hulls split across two decks,
/// twice, on a player save.
/// <para>
/// This type does the move itself and therefore owns every side of it. The order is what makes it safe:
/// plan every cell first, refuse the whole move if any destination is blocked, and only then write.
/// A caller never sees a half-applied relocation, which is the one failure mode a save cannot recover
/// from.
/// </para>
/// <para>
/// Destination rules are the player's: a building or a colonist (either side) in the way refuses the
/// move and names the cell; a neutral pawn or loose items are overwritten, because a hull landing on
/// that cell is a decision the player already made by clicking.
/// </para>
/// </remarks>
internal static class Sos2ShipRelocation
{
    /// <summary>What one hull cell carries across the move.</summary>
    private readonly record struct Load(IntVec3 From, IntVec3 To, List<Thing> Things, TerrainDef? Deck);

    /// <summary>
    /// Rotates <paramref name="core"/> by <paramref name="rotNum"/> (0 none, 1 quarter turn, 2 flip)
    /// and shifts it to sit on <paramref name="anchor"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="anchor"/> is a layer-local cell on the destination deck — the cell the player
    /// clicked, already decoded. Everything else is derived from the hull's own footprint, so no caller
    /// has to know a band offset or a storage height.
    /// </remarks>
    internal static AcceptanceReport Relocate(Building core, byte rotNum, IntVec3 anchor)
    {
        if (!Sos2Compatibility.Active) return "RimLayers is not managing this map.";
        if (core == null || !core.Spawned) return "No ship core.";
        Map map = core.Map;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return "Not a layered map.";
        if (Sos2Compatibility.ShipCellsOf(map) is not { } cells) return "SOS2 has no ship table on this map.";
        if (Sos2Compatibility.ShipsOnMapOf(map) is not { } ships) return "SOS2 has no ships on this map.";

        int index = Sos2Compatibility.ShipIndexAt(map, core.Position);
        if (index < 0) return "That core is not on a ship.";

        // The hull is defined by the table, never by Area: Area is a second copy that other code edits,
        // and a cell in one but not the other is exactly what made SOS2's own move throw.
        var hull = new List<IntVec3>();
        foreach (object? key in cells.Keys)
        {
            if (key is not IntVec3 cell) continue;
            if (cells[cell] is not System.Tuple<int, int> claim) continue;
            if (claim.Item1 != index) continue;
            hull.Add(cell);
        }
        if (hull.Count == 0) return "That ship has no cells.";

        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        int minLayer = int.MaxValue, maxLayer = int.MinValue;
        foreach (IntVec3 cell in hull)
        {
            if (!owner.TryDecode(cell, out LayerCell logical)) continue;
            minX = System.Math.Min(minX, logical.X);
            maxX = System.Math.Max(maxX, logical.X);
            minZ = System.Math.Min(minZ, logical.Z);
            maxZ = System.Math.Max(maxZ, logical.Z);
            minLayer = System.Math.Min(minLayer, logical.LayerId);
            maxLayer = System.Math.Max(maxLayer, logical.LayerId);
        }
        if (minX > maxX) return "The ship has no decodable cells.";

        int width = maxX - minX + 1;
        int depth = maxZ - minZ + 1;
        // A quarter turn changes the footprint's aspect; the flipped rectangle has to be used for both
        // the rotation and the placement check or a non-square hull hangs off its own anchor.
        bool quarter = rotNum == 1;
        int outWidth = quarter ? depth : width;
        int outDepth = quarter ? width : depth;
        if (anchor.x < 0 || anchor.z < 0 || anchor.x + outWidth > layout.Width || anchor.z + outDepth > layout.Height)
            return "The ship does not fit there.";

        var plan = new List<Load>(hull.Count);
        foreach (IntVec3 cell in hull)
        {
            if (!owner.TryDecode(cell, out LayerCell logical)) continue;
            (int x, int z) = Rotate(logical.X - minX, logical.Z - minZ, width, depth, rotNum);
            IntVec3 to = owner.ToStorage(new LayerCell(logical.LayerId, anchor.x + x, anchor.z + z));
            var things = new List<Thing>();
            foreach (Thing thing in cell.GetThingList(map))
                if (!thing.Destroyed && !(thing is Mote) && !(thing is Filth)) things.Add(thing);
            TerrainDef? deck = StructuralSupport.IsSos2DeckTerrain(map.terrainGrid.FoundationAt(cell))
                ? map.terrainGrid.FoundationAt(cell)
                : StructuralSupport.IsSos2DeckTerrain(map.terrainGrid.TerrainAt(cell))
                    ? map.terrainGrid.TerrainAt(cell)
                    : null;
            plan.Add(new Load(cell, to, things, deck));
        }
        if (plan.Count == 0) return "The ship has nothing to move.";

        // Refuse before writing anything. Every cell of the hull is a destination, including the ones it
        // already occupies: those are vacated first when the write starts.
        var occupied = new HashSet<IntVec3>();
        foreach (Load load in plan) occupied.Add(load.From);
        foreach (Load load in plan)
        {
            if (!load.To.InBounds(map)) return "Part of the ship would leave the map.";
            if (occupied.Contains(load.To)) continue;
            foreach (Thing blocker in load.To.GetThingList(map))
            {
                if (blocker.Destroyed) continue;
                if (blocker is Building) return $"Blocked by {blocker.def.label} at {load.To}.";
                if (blocker is Pawn pawn && pawn.Faction != null && pawn.Faction != Faction.OfPlayer
                    && pawn.HostileTo(Faction.OfPlayer)) return $"Blocked by {pawn.LabelShort} at {load.To}.";
                if (blocker is Pawn colonist && colonist.Faction == Faction.OfPlayer)
                    return $"Blocked by {colonist.LabelShort} at {load.To}.";
            }
        }

        // A multi-cell building occupies several hull cells; the plan carries it once per cell it stands
        // on, but it can only be respawned once, and only at its ORIGIN. Spawning it at whichever of its
        // cells happened to come first shifts the whole building by its internal offset - a 5x5 reactor
        // lands up to four cells away from where it belongs, which is both visibly wrong and how a
        // relocated hull ends up with two transmitters on one cell.
        var origin = new Dictionary<Thing, IntVec3>();
        foreach (Load load in plan)
            foreach (Thing thing in load.Things)
            {
                if (origin.ContainsKey(thing)) continue;
                if (!thing.Position.InBounds(map) || !owner.TryDecode(thing.Position, out LayerCell at))
                {
                    origin[thing] = load.To;
                    continue;
                }
                (int ox, int oz) = Rotate(at.X - minX, at.Z - minZ, width, depth, rotNum);
                origin[thing] = owner.ToStorage(new LayerCell(at.LayerId, anchor.x + ox, anchor.z + oz));
            }

        // From here on the move happens. Everything that can throw is scoped so a failure leaves the
        // tables describing the ship it actually moved, not the one it was going to.
        Sos2Compatibility.BeginRelocation();
        bool flagBefore = Sos2Compatibility.MoveFlag;
        // SOS2 reads this flag to skip detach detection, which is what stops a relocation from being
        // read as the hull being dismantled (PreDeSpawn drops cells from MapShipCells, then throws
        // reading the entry it removed). Restored in the finally so a failed move does not leave the
        // flag latched and every later ship action inert.
        Sos2Compatibility.MoveFlag = true;
        try
        {
            // 1. Everything out. Despawning the whole hull first means no cell is ever written twice,
            //    which is what made a rotated non-square ship collide with itself.
            foreach (Load load in plan)
                foreach (Thing thing in load.Things)
                    if (thing.Spawned) thing.DeSpawn();

            // 2. The table and the footprint, rewritten to the destination.
            foreach (Load load in plan)
            {
                cells.Remove(load.From);
                cells[load.To] = new System.Tuple<int, int>(index, -1);
            }

            // 3. The hull's own Area, rebuilt from the same plan. Cleared rather than edited, because a
            //    stale cell here is a cell SOS2 will later try to index the table with.
            //
            //    A null Area is NOT "nothing to do": it is the state in which every SOS2 call that walks
            //    the hull sees an empty ship while the table still claims the cells, and the first one
            //    that indexes the table by an Area cell then throws. Failing loudly here beats moving a
            //    hull into that state and finding out on the next ship tick.
            if (Sos2Compatibility.ShipAreaOf(ships, index) is not { } area)
                throw new System.InvalidOperationException(
                    $"RimLayers relocation: ship {index} has no Area set to rewrite (cells planned={plan.Count}).");
            area.Clear();
            foreach (Load load in plan) area.Add(load.To);

            // 4. Decks. Foundation writes on the source are cleared first so a stationary cell is laid
            //    once, not removed by a later cell's cleanup.
            foreach (Load load in plan)
                if (StructuralSupport.IsSos2DeckTerrain(map.terrainGrid.FoundationAt(load.From)))
                    map.terrainGrid.RemoveFoundation(load.From, false);
            foreach (Load load in plan)
                if (load.Deck != null) ShipDeckTerrain.InstallDeck(owner, load.To, load.Deck);

            // 5. Everything back in, on the far side, at its own origin and facing the same way.
            //
            //    Rotation has to turn with the hull. A wall attachment (a lamp, a vent) finds the wall it
            //    belongs to by looking one cell along its own Rotation; keep the old Rotation and it looks
            //    at open space, GetWallAttachedTo answers null, and vanilla's TryConnectToAnyPowerNet
            //    dereferences that null on every power update for the rest of the save.
            foreach (KeyValuePair<Thing, IntVec3> entry in origin)
            {
                Thing thing = entry.Key;
                if (thing.Destroyed || thing.Spawned) continue;
                GenSpawn.Spawn(thing, entry.Value, map, Turn(thing.Rotation, rotNum));
            }

            // 6. The caches that cached the old positions. Heat and power nets are rebuilt lazily from
            //    their own grids, which key on cells, so both have to be told the cells moved.
            Sos2Compatibility.InvalidateShipCaches(map);
        }
        finally
        {
            Sos2Compatibility.MoveFlag = flagBefore;
            Sos2Compatibility.EndRelocation();
        }
        return AcceptanceReport.WasAccepted;
    }

    /// <summary>
    /// The rotation a thing ends up with after the hull is turned. Must agree with <see cref="Rotate"/>,
    /// or a thing keeps facing the direction it used to face in a hull that no longer has anything
    /// there.
    /// </summary>
    private static Rot4 Turn(Rot4 rotation, byte rotNum) => rotNum switch
    {
        // Rotate maps (x,z) -> (depth-1-z, x), which sends north to west: a counter-clockwise quarter
        // turn, the opposite direction from the cell walk.
        1 => rotation.Rotated(RotationDirection.Counterclockwise),
        // A flip mirrors east and west and leaves north and south alone.
        2 => new Rot4(rotation.AsInt switch { 1 => 3, 3 => 1, int other => other }),
        _ => rotation,
    };

    /// <summary>
    /// Layer-local rotation. <paramref name="rotNum"/> matches SOS2's buttons: 1 is a quarter turn,
    /// 2 is a flip, anything else leaves the footprint alone.
    /// </summary>
    private static (int X, int Z) Rotate(int x, int z, int width, int depth, byte rotNum) => rotNum switch
    {
        // Clockwise quarter turn: the old north edge becomes the new east edge.
        1 => (depth - z - 1, x),
        // Mirror across the north/south axis.
        2 => (width - x - 1, z),
        _ => (x, z),
    };
}
