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
    private readonly record struct Load(
        IntVec3 From, IntVec3 To, List<Thing> Things, TerrainDef? Deck, TerrainDef? Top, RoofDef? Roof);

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
            // A hull cell's floor can live in either of two places, and both have to be carried.
            //
            // A RimLayers deck is a FOUNDATION. SOS2's own hull floor is not: SetShipTerrain writes
            // FakeFloorInsideShip through SetTerrain, which puts a layerable terrain in the TOP grid. Only
            // looking at the foundation therefore found half the cases and cleared none of the other half,
            // which is a ship that arrives floored and leaves its old floor exactly where it was.
            TerrainDef? foundation = map.terrainGrid.FoundationAt(cell);
            TerrainDef? top = map.terrainGrid.TopTerrainAt(cell);
            TerrainDef? deck = StructuralSupport.IsSos2DeckTerrain(foundation)
                ? foundation
                : StructuralSupport.IsSos2DeckTerrain(map.terrainGrid.TerrainAt(cell))
                    ? map.terrainGrid.TerrainAt(cell)
                    : null;
            // A finish or a hull floor sitting above the deck, as opposed to empty air. Carried separately
            // so it lands back on top of the deck at the destination instead of being lost.
            TerrainDef? surface = top != null && top.layerable && !StructuralSupport.IsOpenAir(top)
                ? top
                : null;
            plan.Add(new Load(cell, to, things, deck, surface, RoofAtShipHeight(map, cell)));
        }
        if (plan.Count == 0) return "The ship has nothing to move.";

        // Refuse before writing anything. Every cell of the hull is a destination, including the ones it
        // already occupies: those are vacated first when the write starts.
        var occupied = new HashSet<IntVec3>();
        foreach (Load load in plan) occupied.Add(load.From);
        // Natural ore the hull is going to land on. Unlike a player's building this is map scenery, and a
        // rotation that covers a rectangle including cells the hull never covered will land on it
        // depending on which corner of the map the ship happens to be in. Refusing would make a rotation
        // work on one map and fail on another for no reason the player can see, so the ore is crushed the
        // way Vanilla's own ship move clears its destination. Solid, unmined rock still refuses: a hull
        // cannot rotate through a mountain.
        var ore = new List<Building>();
        foreach (Load load in plan)
        {
            if (!load.To.InBounds(map)) return "Part of the ship would leave the map.";
            if (occupied.Contains(load.To)) continue;
            foreach (Thing blocker in load.To.GetThingList(map))
            {
                if (blocker.Destroyed) continue;
                if (blocker is Building { def.building.isNaturalRock: true } rock)
                {
                    if (rock.def.building.isResourceRock) ore.Add(rock);
                    else return $"Solid rock at {load.To}.";
                    continue;
                }
                if (blocker is Building) return $"Blocked by {blocker.def.label} at {load.To}.";
                if (blocker is Pawn pawn && pawn.Faction != null && pawn.Faction != Faction.OfPlayer
                    && pawn.HostileTo(Faction.OfPlayer)) return $"Blocked by {pawn.LabelShort} at {load.To}.";
                if (blocker is Pawn colonist && colonist.Faction == Faction.OfPlayer)
                    return $"Blocked by {colonist.LabelShort} at {load.To}.";
            }
        }

        // Where each thing is respawned, found by solving rather than by arithmetic about a point.
        //
        // `Thing.Position` is not the centre of a footprint in general: GenAdj.OccupiedRect shifts it by
        // (size-1)/2 for odd sizes and, through GenAdj.AdjustForRotation, by a further cell on any even
        // axis whose rotation faces south or west. Guessing the shift is how three versions of this got a
        // 2x2 hardpoint, a bed or a battery one cell off while everything else looked right.
        //
        // So it is solved: rotate every cell the thing covers, and then find the position whose
        // OccupiedRect exactly reproduces that rotated footprint. That uses vanilla's own definition of
        // where a thing sits instead of a copy of the formula.
        var origin = new Dictionary<Thing, IntVec3>();
        foreach (Load load in plan)
            foreach (Thing thing in load.Things)
            {
                if (origin.ContainsKey(thing)) continue;
                Rot4 turned = Turn(thing.Rotation, rotNum);
                var target = new HashSet<IntVec3>();
                var sources = new List<IntVec3>();
                foreach (IntVec3 c in thing.OccupiedRect())
                {
                    if (!owner.TryDecode(c, out LayerCell cell)) continue;
                    sources.Add(c);
                    (int rx, int rz) = Rotate(cell.X - minX, cell.Z - minZ, width, depth, rotNum);
                    int height = layout.Height;
                    if (rx < 0 || rz < 0 || rx >= layout.Width || rz >= height) continue;
                    target.Add(new IntVec3(anchor.x + rx, 0, anchor.z + rz));
                }
                if (target.Count == 0 || sources.Count == 0)
                {
                    origin[thing] = load.To;
                    continue;
                }
                int loX = int.MaxValue, loZ = int.MaxValue, hiX = int.MinValue, hiZ = int.MinValue;
                foreach (IntVec3 c in target)
                {
                    loX = System.Math.Min(loX, c.x);
                    loZ = System.Math.Min(loZ, c.z);
                    hiX = System.Math.Max(hiX, c.x);
                    hiZ = System.Math.Max(hiZ, c.z);
                }
                IntVec3 found = IntVec3.Invalid;
                // Searched one cell beyond the footprint on every side: for an even axis facing south or
                // west, GenAdj.AdjustForRotation moves the centre a further -1, so the position that
                // covers the wanted cells sits just OUTSIDE their bounding box.
                for (int px = loX - 1; px <= hiX + 1 && !found.IsValid; px++)
                    for (int pz = loZ - 1; pz <= hiZ + 1 && !found.IsValid; pz++)
                    {
                        var candidate = new IntVec3(px, 0, pz);
                        var covered = new HashSet<IntVec3>();
                        foreach (IntVec3 c in GenAdj.OccupiedRect(candidate, turned, thing.def.Size)) covered.Add(c);
                        if (covered.SetEquals(target)) found = candidate;
                    }
                // No exact fit means the thing straddles the hull's edge; the min corner is the closest
                // honest answer and the caller reports any part that ends up outside the hull.
                if (!found.IsValid) found = new IntVec3(loX, 0, loZ);
                // The layer comes from the thing's own cells, never from the found cell's z: on a layered
                // map z is a storage coordinate, so deriving a layer from a local z answers 0 for every
                // deck and collapses the whole hull onto the ground floor.
                int layer = Sos2Geometry.LayerOf(sources[0].z, layout.Stride, layout.LayerCount);
                origin[thing] = owner.ToStorage(new LayerCell(layer, found.x, found.z));
            }

        // Temperature and vacuum live on Room objects, and a Room is derived state: it is rebuilt from
        // regions, which despawning the whole hull destroys. The hull therefore arrives with whatever the
        // new map's ambient is - on a space map that is -100 C, which is a frozen crew rather than a
        // moved one. Same story for power: CompPower.PostDeSpawn cuts every net, and the trader's
        // PowerOn flag does not survive a respawn, so a life-support unit comes back switched off.
        //
        // Both are captured here, before anything is despawned. Rooms are keyed by the storage cell so
        // the destination can be asked which room it inherited.
        // regions, which despawning the whole hull destroys. The hull therefore arrives with whatever the
        // new map's ambient is - on a space map that is -100 C, which is a frozen crew rather than a
        // moved one. Same story for power: CompPower.PostDeSpawn cuts every net, and the trader's
        // PowerOn flag does not survive a respawn, so a life-support unit comes back switched off.
        //
        // Both are captured here, before anything is despawned. Rooms are keyed by the storage cell so
        // the destination can be asked which room it inherited.
        var roomPlan = new List<(IntVec3 To, float Temperature, float Vacuum)>();
        var seenRooms = new HashSet<Room>();
        foreach (Load load in plan)
        {
            Room? room = load.From.GetRoom(map);
            if (room == null || room.UsesOutdoorTemperature || !seenRooms.Add(room)) continue;
            // One destination cell per room is enough: the whole room is being rebuilt on the far side.
            roomPlan.Add((load.To, room.Temperature, room.Vacuum));
        }
        // Parts whose power state has to survive the respawn. CompPower.PostDeSpawn cuts every net and
        // CompPowerTrader.ResetPowerVars clears powerOnInt, so nothing comes back on by itself.
        var powered = new HashSet<Thing>();
        foreach (Load load in plan)
            foreach (Thing thing in load.Things)
            {
                if (thing.Destroyed) continue;
                if (thing.TryGetComp<CompPowerTrader>() != null) powered.Add(thing);
            }

        var zonePlan = new List<(Zone Zone, IntVec3 To)>();
        foreach (Load load in plan)
            if (map.zoneManager.ZoneAt(load.From) is { } zone)
                zonePlan.Add((zone, load.To));

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
            // 0. Map scenery the hull is landing on. Destroying it here rather than before the refusal
            //    check above is deliberate: nothing is written until the whole move is known to be
            //    possible, so a refused move never leaves a crater behind. Vanished rather than
            //    deconstructed: this ore is being crushed by a hull, and dropping its yield would carpet
            //    the deck the ship has just landed on. SOS2's own move does the same.
            foreach (Building rock in ore)
                if (!rock.Destroyed) rock.Destroy();

            // 1. Everything out. Despawning the whole hull first means no cell is ever written twice,
            //    which is what made a rotated non-square ship collide with itself.
            //
            //    The despawn runs with terrain effects suppressed: CompShipCachePart.PostDeSpawn tears
            //    the top layer off every cell under a departing part and drops that terrain's resource
            //    cost as loose items - which is right for a hull being torn apart and wrong for one being
            //    rotated in place, where it leaves a pile of steel and plasteel on the old footprint.
            using (Sos2TerrainPatches.SuppressedEffects())
            {
                foreach (Load load in plan)
                    foreach (Thing thing in load.Things)
                        if (thing.Spawned) thing.DeSpawn();
            }

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

            // 4. Decks. The source is cleared first so a stationary cell is laid once, not removed by a
            //    later cell's cleanup.
            //
            //    Three layers can make a cell look floored, and TerrainAt reads them in a fixed order:
            //    tempGrid, then foundationGrid when underGrid is empty, then topGrid. Clearing only the
            //    foundation therefore can leave a visible floor above it - which is exactly what a ship
            //    whose floor is SOS2's FakeFloorInsideShip does, because that one is a layerable TOP
            //    terrain and never was a foundation.
            //
            //    The under terrain is set to open air for the same reason: with underGrid occupied,
            //    TerrainAt answers topGrid, so anything the top grid still holds stays visible no matter
            //    what the foundation says.
            foreach (Load load in plan)
            {
                var grid = map.terrainGrid;
                IntVec3 from = load.From;
                // Top surface first: RemoveTopLayer leaves the under terrain alone, so it can only do its
                // job while the decks below it are still there.
                if (grid.TopTerrainAt(from) is { } top && !StructuralSupport.IsOpenAir(top))
                    grid.RemoveTopLayer(from, false);
                if (grid.FoundationAt(from) != null) grid.RemoveFoundation(from, false);
                // Whatever the cell still answers as its terrain has to become open air: with the under
                // grid occupied, TerrainAt prefers topGrid, and a leftover of either is a floor the hull
                // no longer stands on.
                if (!StructuralSupport.IsOpenAir(grid.TerrainAt(from)))
                    grid.SetTerrain(from, StructuralSupport.OpenAirFor(map));
                if (grid.UnderTerrainAt(from) is { } under && !StructuralSupport.IsOpenAir(under))
                    grid.SetUnderTerrain(from, StructuralSupport.OpenAirFor(map));
            }
            foreach (Load load in plan)
                if (load.Deck != null) ShipDeckTerrain.InstallDeck(owner, load.To, load.Deck);
            // A finish or hull floor goes back on top of the deck, not instead of it.
            foreach (Load load in plan)
                if (load.Top != null && load.Deck != null) map.terrainGrid.SetTerrain(load.To, load.Top);

            // 4b. The ceiling travels with the hull.
            //
            //     Without it the ship arrives roofless, and every one of its rooms reports
            //     OpenRoofCount > 0 - which is exactly the condition SOS2's ExposedToOutside answers true
            //     for and its temperature readout renders as "vacuum". A hull whose walls are intact and
            //     whose floor is intact still reads as open space, and no amount of wall or deck
            //     bookkeeping fixes it, because the missing thing is the roof.
            //
            //     The roof lives on the deck cell itself, not one band up. Both sides agree on that: SOS2's
            //     CompShipCachePart.SetRoof writes it at the cell the part stands on, and this mod's
            //     StructuralSupport.HasRequiredRoof reads RoofAt(lower) for the cell above - so "the roof
            //     over this deck" IS RoofAt(deck). Looking one band up instead finds the upper deck's own
            //     roof, which is a different room's ceiling, and would leave the deck it was meant to
            //     cover still open.
            //
            //     Clearing the source comes with the same overlap rule the decks use, and for the same
            //     reason: a hull rotated in place re-covers some of the cells it just vacated, and
            //     stripping the ceiling off those would leave the ship sealed on three sides only.
            var covered = new HashSet<IntVec3>();
            foreach (Load load in plan) covered.Add(load.To);
            foreach (Load load in plan)
            {
                if (covered.Contains(load.From)) continue;
                if (map.roofGrid.RoofAt(load.From) == null) continue;
                map.roofGrid.SetRoof(load.From, null);
            }
            foreach (Load load in plan)
                if (load.Roof != null) map.roofGrid.SetRoof(load.To, load.Roof);

            // 5. Zones back onto the cells their contents are about to occupy.
            //
            //    This has to happen BEFORE the respawn, and the order is the whole point. Vanilla's
            //    Zone.AddCell refuses any cell holding a thing whose def does not allow zone overlap, and
            //    a deck is exactly such a thing - so adding zone cells after the hull is back logs
            //    "Added zone over zone-incompatible thing" and drops every cell, which is why the storage
            //    layout stayed on the old footprint. Worse, spawning a thing runs
            //    ZoneManager.Notify_NoZoneOverlapThingSpawned, which then strips the cells back out again.
            //
            //    Added to bare floor first, the zone is simply a zone the contents land inside, which is
            //    the state a player builds by painting a stockpile and then placing things on it.
            foreach ((Zone zone, IntVec3 to) in zonePlan)
            {
                if (zone == null) continue;
                // Despawning every part on the footprint strips the zone of all its cells, and a zone
                // with no cells deregisters itself. Re-registering it is what makes the layout survive
                // the move at all; the settings object on it is untouched by deregistration.
                if (!map.zoneManager.AllZones.Contains(zone)) map.zoneManager.RegisterZone(zone);
                if (zone.cells.Contains(to)) continue;
                // Vanilla's AddCell refuses a cell holding a thing that does not allow zone overlap and
                // logs an error. Ask the same question first so a hull part that is somehow already
                // standing there costs a skipped cell instead of an error in the player's log.
                bool blocked = false;
                foreach (Thing thing in map.thingGrid.ThingsListAt(to))
                    if (!thing.def.CanOverlapZones) { blocked = true; break; }
                if (blocked) continue;
                zone.AddCell(to);
            }

            // 5b. The old cells go. Without this the layout exists twice - once where the hull used to be
            //     and once where it now is - which is a stockpile the player never drew and cannot
            //     explain. Only cells that did not also land on the new footprint are removed: a ship
            //     rotated in place keeps the cells it still covers.
            var kept = new HashSet<IntVec3>();
            foreach (Load load in plan) kept.Add(load.To);
            foreach (Load load in plan)
            {
                IntVec3 from = load.From;
                if (kept.Contains(from)) continue;
                if (map.zoneManager.ZoneAt(from) is not { } oldZone) continue;
                if (!oldZone.cells.Contains(from)) continue;
                oldZone.RemoveCell(from);
            }

            // 6. Everything back in, on the far side, at its own origin and facing the same way.
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
                // A pawn's path cost grid and its remembered destination are index-based state from the
                // cell it used to stand on, so a crew member put down on the far side of the map keeps
                // walking towards wherever it was going before the ship moved. Vanilla's own ship move
                // resets it for exactly this reason.
                if (thing is Pawn pawn && pawn.pather != null) pawn.pather.ResetToCurrentPosition();
            }

            // 7. Power back to the state the player left it in.
            //
            //    CompPower.PostDeSpawn disconnects every net and CompPowerTrader.ResetPowerVars clears
            //    powerOnInt, so a respawned life-support unit comes back off even though its flick switch
            //    says on. The caches are invalidated first so the nets are rebuilt from the new cells;
            //    only then can PowerOn be set without PowerNet being the old, emptied one.
            Sos2Compatibility.InvalidateShipCaches(map);
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            // PowerNet is deliberately NOT required here. Rebuilding nets is deferred - the call above
            // only queues the work - so a part's net is still null at this point, and waiting for one
            // would leave every engine, reactor and life-support unit switched off until SOS2's own
            // repower pass happened to run. An unconnected trader with its flick switch on is also a
            // normal vanilla state: the part is simply waiting for a source.
            // Everything in the hull, not only the parts that were on before. A part with its flick switch
            // on and a live net is a part the player expects running; switching on only what was on before
            // left the engines and consoles dark, because SOS2's own power bookkeeping had already turned
            // them off at some earlier point and the player had never noticed.
            foreach (Load load in plan)
                foreach (Thing thing in load.Things)
                {
                    if (!powered.Contains(thing)) continue;
                    if (thing is not Building building || building.Destroyed || !building.Spawned) continue;
                    if (building.TryGetComp<CompPowerTrader>() is not { } power) continue;
                    if (power.PowerOn) continue;
                    if (!FlickUtility.WantsToBeOn(building) || building.IsBrokenDown()) continue;
                    power.PowerOn = true;
                }

            // 8. The interior climate.
            //
            //    Written after the respawn, because room layouts are rebuilt lazily by the region
            //    updater: asking for a room before the hull is back finds nothing, and writing a
            //    temperature to a room the updater is about to discard loses it. The updater is told to
            //    do its work here so the rooms the destination cells belong to actually exist.
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            foreach ((IntVec3 to, float temperature, float vacuum) in roomPlan)
            {
                if (!to.InBounds(map)) continue;
                Room? room = to.GetRoom(map);
                if (room == null || room.UsesOutdoorTemperature) continue;
                room.Temperature = temperature;
                if (map.Biome.inVacuum) room.Vacuum = vacuum;
            }
        }
        finally
        {
            Sos2Compatibility.MoveFlag = flagBefore;
            Sos2Compatibility.EndRelocation();
        }
        return AcceptanceReport.WasAccepted;
    }


    /// <summary>
    /// The roof a hull cell contributes to its room.
    /// </summary>
    /// <remarks>
    /// Read from the deck cell, which is where both SOS2 (<c>CompShipCachePart.SetRoof</c>) and this mod
    /// (<c>StructuralSupport.HasRequiredRoof</c>, via <c>RoofAt(lower)</c>) keep it. A hull that never had a
    /// ceiling answers null and is not given one here.
    /// </remarks>
    private static RoofDef? RoofAtShipHeight(Map map, IntVec3 deck) => map.roofGrid.RoofAt(deck);

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
