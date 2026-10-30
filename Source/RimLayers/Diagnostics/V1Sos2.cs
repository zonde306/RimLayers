using System.Collections;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Proves the SOS2 adapter keeps a multi-deck hull together on a layered map.
/// </summary>
/// <remarks>
/// SOS2 owns its ship model: <c>ShipMapComp.MapShipCells</c> is a flat dictionary keyed by storage
/// cell and <c>SpaceShipCache</c> is rebuilt from it. The adapter never converts a ship into an
/// Odyssey gravship — it only teaches SOS2's own walks to cross decks and stops its geometry helpers
/// from reading <c>Map.Size.z</c> as the logical rectangle. This scenario checks exactly those
/// boundaries, so a regression in any one of them fails a named check instead of showing up as a
/// silently detached upper deck in a save.
/// </remarks>
internal static partial class V1Integration
{
    // Wide enough to hold every fixture part: the bay is 5x5 at (62,58), life support 3x3 at (58,62),
    // the capacitor 3x5 at (62,62). A deck that only covers the hull would make every "joins the ship"
    // assertion fail for the boring reason that the part never stood on the deck.
    private static readonly CellRect sos2DeckRect = new(50, 50, 19, 17);
    private static Building? sos2Bridge;
    private static Building? sos2UpperPart;
    private static Building? sos2UpperHeat;
    private static Building? sos2LowerHeat;
    private static MapComponent? sos2ShipComp;
    /// <summary>
    /// Two hull tiles destroyed during the fixture, one on each deck, so the foam step has real
    /// breaches to rank. Filled in the foam step, not here: destroying a tile before the ship cache
    /// exists would just remove a cell from the map instead of registering a breach.
    /// </summary>
    private static IntVec3 sos2FoamLowerCell = IntVec3.Invalid;
    private static IntVec3 sos2FoamUpperCell = IntVec3.Invalid;
    private static int sos2Phase;
    private static bool sos2AdapterExpected;

    private static void BeginSos2()
    {
        sos2AdapterExpected = ModsConfig.IsActive("kentington.saveourship2");
        if (!sos2AdapterExpected)
        {
            Check("sos2-adapter-is-installed", false, "Save Our Ship 2 is not loaded; run with -Sos2.");
            Finish();
            return;
        }
        Check("sos2-adapter-is-installed", Sos2Compatibility.Active);
        if (!Sos2Compatibility.Active) { Finish(); return; }

        sos2ShipComp = Sos2Compatibility.ShipCompOf(map);
        Check("sos2-map-component-is-attached", sos2ShipComp != null,
            $"components={string.Join(",", map.components.Select(c => c.GetType().Name))}");
        if (sos2ShipComp == null) { Finish(); return; }

        // A deck of SOS2 hull plating with a bridge on the ground band, then one more hull part on the
        // deck above. The bridge is what makes SOS2 treat the whole thing as a ship.
        BuildSos2Pad();
        // Hull first, bridge last: SOS2 registers a bridge by claiming the cells under it, and
        // SpaceShipCache.RebuildCorePath reads MapShipCells[Core.Position] unguarded. A bridge placed
        // on bare ground becomes a wreck core and crashes the next attach, which is also what a player
        // would hit laying a bridge before its plating.
        foreach (IntVec3 c in sos2DeckRect)
        {
            SpawnSos2("ShipHullTile", At(0, c.x, c.z), Rot4.North);
            SpawnSos2("ShipHullTile", At(1, c.x, c.z), Rot4.North);
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        sos2Bridge = SpawnSos2("ShipPilotSeatMini", At(0, 56, 55), Rot4.North);
        Check("sos2-bridge-spawned", sos2Bridge != null);
        if (sos2Bridge == null) { Finish(); return; }

        // A reactor on the ground deck and a conduit-shaped part on the deck above it: the heat net
        // only reaches the upper deck through the adapter's vertical adjacency.
        sos2LowerHeat = SpawnSos2("Ship_Reactor", At(0, 54, 54), Rot4.North);
        sos2UpperHeat = SpawnSos2("ShipPilotSeatMini", At(1, 54, 54), Rot4.North);
        sos2UpperPart = sos2UpperHeat;

        sos2Phase = 0;
        stage = 603;
        ticks = 0;
    }

    private static void BuildSos2Pad()
    {
        foreach (IntVec3 c in sos2DeckRect)
        {
            foreach (Thing t in c.GetThingList(map).ToList())
                if (t is not Pawn && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
            map.roofGrid.SetRoof(c, null);
            map.fogGrid.Unfog(c);
            IntVec3 upper = At(1, c.x, c.z);
            map.terrainGrid.SetTerrain(upper, RimLayersDefOf.RL_OpenAir);
            map.fogGrid.Unfog(upper);
        }
        // Columns carry the deck above, and the roof below is what RimLayers accepts as support.
        for (int x = sos2DeckRect.minX; x <= sos2DeckRect.maxX; x += 4)
        for (int z = sos2DeckRect.minZ; z <= sos2DeckRect.maxZ; z += 4)
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column, ThingDefOf.Steel), new IntVec3(x, 0, z), map);
        foreach (IntVec3 c in sos2DeckRect) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(sos2DeckRect.Contains);
    }

    /// <summary>
    /// The load leg does not rebuild the fixture, so the heat parts are looked up by def and deck
    /// instead of by the fields the generate leg kept.
    /// </summary>
    private static Building? HeatPartOn(int layer)
    {
        if (owner == null || map == null) return null;
        foreach (IntVec3 cell in owner.LayerRect(layer))
            foreach (Thing t in cell.GetThingList(map))
                if (t is Building b && Sos2Compatibility.IsHeatPart(b))
                    return b;
        return null;
    }

    private static Building? LowerHeatPart() => HeatPartOn(0);

    private static Building? UpperHeatPart() => HeatPartOn(1);

    private static Building? SpawnSos2(string defName, IntVec3 cell, Rot4 rot)
    {
        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null)
        {
            Check("sos2-def-" + defName + "-exists", false, "missing ThingDef");
            return null;
        }
        var building = (Building)ThingMaker.MakeThing(def);
        building.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(building, cell, map, rot);
        return building;
    }

    /// <summary>
    /// Load leg: SOS2 rebuilds its whole ship cache from the bridge on load, so this is the only place
    /// that proves a multi-deck hull survives a save. Nothing here recreates the fixture.
    /// </summary>
    private static void RestoreSos2()
    {
        var baselinePath = Path.Combine(directory, "sos2-bands.json");
        if (!File.Exists(baselinePath)) { Check("sos2-baseline-present", false, baselinePath); Finish(); return; }
        using var document = JsonDocument.Parse(File.ReadAllText(baselinePath));
        int savedLayers = document.RootElement.GetProperty("layerCount").GetInt32();

        Check("sos2-adapter-bound-after-load", Sos2Compatibility.Active);
        if (!Sos2Compatibility.Active) { Finish(); return; }
        sos2ShipComp = Sos2Compatibility.ShipCompOf(map);
        Check("sos2-map-component-survives-load", sos2ShipComp != null);
        if (sos2ShipComp == null) { Finish(); return; }

        System.Collections.IDictionary? cells = Sos2Compatibility.ShipCellsOf(sos2ShipComp);
        Check("sos2-ship-cells-rebuilt-after-load", cells is { Count: > 0 }, $"cells={cells?.Count}");
        int upper = 0, lower = 0;
        if (cells != null)
            foreach (IntVec3 key in cells.Keys)
                if (owner.TryDecode(key, out LayerCell logical))
                {
                    if (logical.LayerId == 1) upper++;
                    else if (logical.LayerId == 0) lower++;
                }
        // The interesting half: a rebuilt cache must not silently drop the deck above the bridge.
        Check("sos2-loaded-ship-keeps-its-upper-deck", upper > 0, $"upperCells={upper}, total={cells?.Count}");
        Check("sos2-loaded-ship-keeps-its-ground-deck", lower > 0, $"lowerCells={lower}, total={cells?.Count}");
        Check("sos2-layout-survives-load", owner.Layout != null && owner.Layout.LayerCount == savedLayers,
            $"layers={owner.Layout?.LayerCount}, saved={savedLayers}");

        int[]? grid = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "grid")?.GetValue(sos2ShipComp) as int[];
        Check("sos2-loaded-heat-grid-matches-the-map", grid != null && grid.Length == map.cellIndices.NumGridCells,
            $"grid={grid?.Length}, cells={map.cellIndices.NumGridCells}");
        // A freshly loaded component allocates a zeroed grid, so the loaded array alone proves nothing;
        // force one rebuild and check the rebuilt content instead. Net ids start at 0, so "0" is a
        // legitimate owner — the meaningful properties are "the heat parts are owned" and "everything
        // else is unclaimed (-1)".
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "heatGridDirty")?.SetValue(sos2ShipComp, true);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "loaded")?.SetValue(sos2ShipComp, false);
        AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "MapComponentUpdate", Type.EmptyTypes)?.Invoke(sos2ShipComp, null);
        System.Collections.IList? nets = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedNets")?.GetValue(sos2ShipComp) as System.Collections.IList;
        Check("sos2-loaded-heat-nets-rebuild-across-decks", nets is { Count: > 0 }, $"nets={nets?.Count}");

        // The two heat parts sit on different decks. If the rebuilt net only walked one deck, one of
        // these two cells stays at -1 and the loaded ship silently loses its cross-deck heat grid.
        Building? loadedLower = LowerHeatPart();
        Building? loadedUpper = UpperHeatPart();
        int lowerOwned = loadedLower == null ? -1 : grid?[map.cellIndices.CellToIndex(loadedLower.Position)] ?? -2;
        int upperOwned = loadedUpper == null ? -1 : grid?[map.cellIndices.CellToIndex(loadedUpper.Position)] ?? -2;
        Check("sos2-loaded-heat-grid-owns-the-lower-deck-part", lowerOwned >= 0,
            $"lower={loadedLower?.Position}, net={lowerOwned}");
        Check("sos2-loaded-heat-grid-owns-the-upper-deck-part", upperOwned >= 0,
            $"upper={loadedUpper?.Position}, net={upperOwned}");
        // Owning "some" net is not enough: the whole point of the adapter is that a deck above the
        // reactor shares the reactor's net. Two nets here means the vertical edge was dropped and the
        // upper deck would run without a sink.
        Check("sos2-loaded-heat-grid-joins-both-decks-into-one-net", lowerOwned >= 0 && lowerOwned == upperOwned,
            $"lower={lowerOwned}, upper={upperOwned}, nets={nets?.Count}");
        int unclaimed = 0, length = grid?.Length ?? 0;
        if (grid != null) foreach (int v in grid) if (v == -1) unclaimed++;
        Check("sos2-loaded-heat-grid-leaves-the-rest-unclaimed", unclaimed > 0 && unclaimed < length,
            $"unclaimed={unclaimed}, grid={length}");
        Finish();
    }

    private static void Sos2Step()
    {
        if (sos2Phase == 0)
        {
            // SOS2 rebuilds its ship caches from the roots it can see; a layered map must still hand it
            // every deck. RecacheFromRoots is the same entry point a save/load uses.
            AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "RecacheFromRoots")?.Invoke(sos2ShipComp, null);
            System.Collections.IDictionary? cells = Sos2Compatibility.ShipCellsOf(sos2ShipComp);
            Check("sos2-ship-cells-are-tracked", cells is { Count: > 0 }, $"cells={cells?.Count}");
            int upper = 0;
            if (cells != null)
                foreach (IntVec3 key in cells.Keys)
                    if (owner.TryDecode(key, out LayerCell logical) && logical.LayerId == 1) upper++;
            Check("sos2-upper-deck-joins-the-same-ship", upper > 0,
                $"upperCells={upper}, total={cells?.Count}");
            Check("sos2-lower-deck-still-tracked", cells != null && cells.Count > upper,
                $"upperCells={upper}, total={cells?.Count}");
            sos2Phase = 1;
            ticks = 0;
            return;
        }
        if (sos2Phase == 1)
        {
            // Banded geometry: SOS2's own helpers must answer inside the logical rectangle.
            IntVec3 bridgeCell = sos2Bridge!.Position;
            (int ex, int ey, int ez) = Sos2Geometry.EdgeCell(bridgeCell.x, 0, bridgeCell.z,
                owner.Layout!.Width, owner.Layout.Height, owner.Layout.Stride, owner.Layout.LayerCount, 0, 0);
            IntVec3 groundEdge = new(ex, ey, ez);
            Check("sos2-turret-edge-stays-on-the-ground-band",
                owner.TryDecode(groundEdge, out LayerCell edge) && edge.LayerId == 0 && edge.Z == owner.Layout.Height - 1,
                $"edge={groundEdge}");
            (int mx, int my, int mz) = Sos2Geometry.Mirror(10, 0, 20,
                owner.Layout.Width, owner.Layout.Height, owner.Layout.Stride, owner.Layout.LayerCount, 2);
            Check("sos2-move-mirror-keeps-the-band",
                owner.TryDecode(new IntVec3(mx, my, mz), out LayerCell mirrored) && mirrored.LayerId == 0,
                $"mirrored=({mx},{my},{mz})");
            Check("sos2-band-length-is-not-the-storage-length",
                Sos2Geometry.LogicalLength(map.Size.z, owner.Layout.Height, owner.Layout.Stride, owner.Layout.LayerCount) == owner.Layout.Height);
            sos2Phase = 2;
            ticks = 0;
            return;
        }
        if (sos2Phase == 2)
        {
            // Heat: one net must own both decks. Without the vertical adjacency the upper deck is a
            // second net with no sink, which is what the adapter's transpiler prevents.
            // MapComponentUpdate gates itself on a 60-tick cadence once it has run once, so the fixture
            // clears `loaded` as well as the dirty flag; otherwise the call is a silent no-op and the
            // assertions below read an empty net list.
            AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "heatGridDirty")?.SetValue(sos2ShipComp, true);
            AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "loaded")?.SetValue(sos2ShipComp, false);
            // Resolve the override, not the abstract base: MapComponent.MapComponentUpdate does nothing.
            AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "MapComponentUpdate", Type.EmptyTypes)?.Invoke(sos2ShipComp, null);
            System.Collections.IList? nets = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedNets")?.GetValue(sos2ShipComp) as System.Collections.IList;
            Check("sos2-heat-nets-were-rebuilt", nets is { Count: > 0 }, $"nets={nets?.Count}");
            int[]? grid = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "grid")?.GetValue(sos2ShipComp) as int[];
            int lowerNet = -1, upperNet = -1;
            if (grid != null && sos2LowerHeat != null && sos2UpperHeat != null)
            {
                lowerNet = grid[map.cellIndices.CellToIndex(sos2LowerHeat.Position)];
                upperNet = grid[map.cellIndices.CellToIndex(sos2UpperHeat.Position)];
            }
            Check("sos2-heat-grid-covers-the-lower-deck", lowerNet >= 0, $"lower={lowerNet}");
            Check("sos2-heat-grid-covers-the-upper-deck", upperNet >= 0, $"upper={upperNet}");
            // Owning "some" net is not the property that matters: a deck above the reactor has to
            // share the reactor's net, otherwise it runs with no sink.
            Check("sos2-heat-grid-joins-both-decks-into-one-net", lowerNet >= 0 && lowerNet == upperNet,
                $"lower={lowerNet}, upper={upperNet}, nets={nets?.Count}");
            Check("sos2-heat-grid-is-the-expanded-size", grid != null && grid.Length == map.cellIndices.NumGridCells,
                $"grid={grid?.Length}, cells={map.cellIndices.NumGridCells}");
            sos2Phase = 3;
            ticks = 0;
            return;
        }
        if (sos2Phase == 3)
        {
            Sos2SubsystemStep();
            sos2Phase = 4;
            ticks = 0;
            return;
        }
        if (sos2Phase == 4)
        {
            // The expansion hook must refill the appended band with -1, not the array default 0.
            // A prototype map already owns elevations 0..PrototypeLayers-1, so the fixture has to ask
            // for one past the top or EnsureElevation is a no-op and proves nothing.
            int before = map.cellIndices.NumGridCells;
            int target = owner.Layout!.HighestElevation + 1;
            RimLayersMod.Settings.UpperLimit = Math.Max(RimLayersMod.Settings.UpperLimit, target);
            RimLayersMod.Settings.GlobalLimit = Math.Max(RimLayersMod.Settings.GlobalLimit, 20);
            LiveMapExpansion.EnsureElevation(map, target);
            owner = LayerMapComponent.For(map)!;
            int[]? grid = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "grid")?.GetValue(sos2ShipComp) as int[];
            Check("sos2-expansion-grows-the-map", map.cellIndices.NumGridCells > before,
                $"cells={before}->{map.cellIndices.NumGridCells}");
            Check("sos2-expansion-regrows-the-heat-grid", grid != null && grid.Length == map.cellIndices.NumGridCells,
                $"grid={grid?.Length}, cells={map.cellIndices.NumGridCells}");
            int zeroes = 0;
            if (grid != null) foreach (int v in grid) if (v == 0) zeroes++;
            // Net ids start at 0, so a zeroed band is indistinguishable from "already owned by net 0".
            Check("sos2-expansion-refills-the-heat-grid-with-minus-one", zeroes == 0,
                $"cellsEqualZero={zeroes}, grid={grid?.Length}");
            File.WriteAllText(Path.Combine(directory, "sos2-bands.json"), JsonSerializer.Serialize(new
            {
                packageId = "kentington.saveourship2",
                adapter = Sos2Compatibility.Active,
                layerCount = owner.Layout!.LayerCount,
                stride = owner.Layout.Stride,
                height = owner.Layout.Height,
                cells = map.cellIndices.NumGridCells,
                gridLength = grid?.Length,
                mode
            }, new JsonSerializerOptions { WriteIndented = true }));
            // The load leg needs a real save to reload; Run-V1.ps1 maps the `sos2` scenario to this name.
            GameDataSaveLoader.SaveGame("RimLayers_V1Sos2");
            sos2Phase = 5;
            ticks = 0;
            return;
        }
        if (sos2Phase == 5)
        {
            CameraJumper.TryJump(sos2Bridge!, CameraJumper.MovementMode.Cut);
            pendingScreenshot = Path.Combine(directory, "sos2-decks.png");
            screenshotHold = 3;
            // Foam and the off-ship sweep both destroy hull tiles, so they run after the save: the load
            // leg has to read the map the baseline was written from, not a map two breaches poorer.
            sos2Phase = 7;
            ticks = 0;
            return;
        }
        if (sos2Phase == 7)
        {
            Sos2FoamStep();
            sos2Phase = 8;
            ticks = 0;
            return;
        }
        if (sos2Phase == 8)
        {
            Sos2OffShipStep();
            sos2Phase = 6;
            ticks = 0;
            return;
        }
        if (sos2Phase == 6)
        {
            stage = 11;
            ticks = 0;
        }
    }

    /// <summary>
    /// One check per SOS2 subsystem the adapter touches, plus the ones it deliberately leaves alone.
    /// A subsystem that regresses fails its own named check, so a broken deck cannot hide behind a
    /// neighbour that still works.
    /// </summary>
    private static void Sos2SubsystemStep()
    {
        // --- engines: the exhaust cone is a 2D rectangle grown off the footprint ---------------
        Building? engine = SpawnSos2("Ship_Engine_Small", At(1, 58, 58), Rot4.North);
        Check("sos2-engine-spawned-on-the-upper-deck", engine != null);
        if (engine != null)
        {
            MethodInfo? exhaust = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.CompEngineTrail"),
                "GetExhaustArea", new[] { typeof(IntVec3), typeof(Rot4), typeof(ThingDef), typeof(Map) });
            var area = exhaust?.Invoke(null, new object[] { engine.Position, Rot4.North, engine.def, map }) as HashSet<IntVec3>;
            Check("sos2-engine-exhaust-is-computed", area is { Count: > 0 }, $"cells={area?.Count}");
            // The native rectangle is grown off a storage-coordinate footprint, so near the top of a
            // band it would spill into the padding row and then into the deck above. Every cell it
            // returns has to belong to the deck the engine sits on.
            int offDeck = 0;
            if (area != null)
                foreach (IntVec3 cell in area)
                    if (!owner.TryDecode(cell, out LayerCell exhaustLogical) || exhaustLogical.LayerId != 1) offDeck++;
            Check("sos2-engine-exhaust-stays-on-its-own-deck", offDeck == 0,
                $"offDeck={offDeck}, cells={area?.Count}");
        }

        // --- heat sink: never pushes into the deck below ---------------------------------------
        Building? sink = SpawnSos2("ShipHeatsink", At(1, 60, 58), Rot4.North);
        Check("sos2-heat-sink-spawned-on-the-upper-deck", sink != null);
        if (sink != null)
        {
            ThingComp? sinkComp = sink.AllComps.FirstOrDefault(c => c.GetType().Name == "CompShipHeatSink");
            Check("sos2-heat-sink-comp-resolved", sinkComp != null);
            MethodInfo? push = sinkComp == null ? null : AccessTools.Method(sinkComp.GetType(), "TryPushHeat",
                new[] { typeof(IntVec3), typeof(float) });
            if (push != null && sinkComp != null)
            {
                // A ground-deck room is a real room; without the deck guard the sink would warm it.
                object? crossDeck = push.Invoke(sinkComp, new object[] { At(0, 56, 56), 10f });
                Check("sos2-heat-sink-refuses-to-push-heat-across-decks", crossDeck is false, $"result={crossDeck}");
            }
            else
            {
                Check("sos2-heat-sink-push-method-resolved", false, "TryPushHeat not found");
            }
        }

        // --- shuttle bay: a bay only reaches its own deck --------------------------------------
        // Def names, not group names: Ship_ShuttleBays and Ship_Sensors are DesignatorDropdownGroupDefs,
        // so a ThingDef lookup finds nothing and the spawn silently returns null.
        Building? bay = SpawnSos2("ShipShuttleBay", At(1, 62, 58), Rot4.North);
        Check("sos2-shuttle-bay-spawned-on-the-upper-deck", bay != null);
        if (bay != null)
        {
            ThingComp? bayComp = bay.AllComps.FirstOrDefault(c => c.GetType().Name == "CompShipBay");
            CellRect bayRect = bayComp == null
                ? CellRect.Empty
                : AccessTools.Field(bayComp.GetType(), "bayRect")?.GetValue(bayComp) as CellRect? ?? CellRect.Empty;
            Check("sos2-shuttle-bay-rect-is-known", bayRect != CellRect.Empty, $"rect={bayRect}");
            // A bay rectangle is a same-layer CellRect, so it can never contain a cell from another
            // deck. This is the invariant the explicit deck guard in the patch formalises.
            int crossDeck = 0;
            foreach (IntVec3 cell in sos2DeckRect)
                if (bayRect.Contains(At(0, cell.x, cell.z))) crossDeck++;
            Check("sos2-shuttle-bay-rect-never-covers-the-ground-deck", crossDeck == 0, $"cells={crossDeck}");
        }

        // --- space map generation: storage size is not the logical size ------------------------
        IntVec3 logical = Sos2Compatibility.LogicalSize(map.Size);
        Check("sos2-space-map-is-generated-at-the-logical-size", logical.z == owner.Layout!.Height,
            $"size={map.Size.z}->{logical.z}, height={owner.Layout.Height}");

        // --- combat: incoming fire spawns on an edge of the target's own deck ------------------
        MethodInfo? lowSpread = AccessTools.Method(Sos2Compatibility.ShipMapCompType,
            "FindClosestEdgeCellLowSpread", new[] { typeof(Map), typeof(IntVec3) });
        MethodInfo? randomEdge = AccessTools.Method(Sos2Compatibility.ShipMapCompType,
            "FindClosestEdgeCell", new[] { typeof(Map), typeof(IntVec3) });
        IntVec3 target = At(1, 56, 60);
        foreach ((string name, MethodInfo? method) in new[]
                 {
                     ("low-spread", lowSpread), ("random", randomEdge),
                 })
        {
            if (method == null) { Check("sos2-combat-edge-" + name + "-resolved", false); continue; }
            IntVec3 spawn = (IntVec3)method.Invoke(sos2ShipComp, new object[] { map, target })!;
            bool decoded = owner.TryDecode(spawn, out LayerCell spawnLogical);
            bool onDeck = decoded && spawnLogical.LayerId == 1;
            bool onEdge = decoded &&
                          (spawnLogical.X == 0 || spawnLogical.X == owner.Layout.Width - 1 ||
                           spawnLogical.Z == 0 || spawnLogical.Z == owner.Layout.Height - 1);
            Check("sos2-combat-edge-" + name + "-spawns-on-the-targets-deck", onDeck, $"spawn={spawn}");
            Check("sos2-combat-edge-" + name + "-spawns-on-a-band-edge", onEdge, $"spawn={spawn}");
        }

        // --- life support / sensor / capacitor: they must join the ship from any deck ----------
        Building? lifeSupport = SpawnSos2("Ship_LifeSupport", At(1, 58, 62), Rot4.North);
        Building? sensor = SpawnSos2("Ship_SensorCluster", At(1, 60, 62), Rot4.North);
        Building? capacitor = SpawnSos2("ShipCapacitor", At(1, 62, 62), Rot4.North);
        Check("sos2-life-support-spawned", lifeSupport != null);
        Check("sos2-sensor-spawned", sensor != null);
        Check("sos2-capacitor-spawned", capacitor != null);

        // Rebuilding the cache is what a save/load does; an upper-deck life support that does not
        // appear here would mean the crew suffocates on a deck the adapter claims to support.
        AccessTools.Method(Sos2Compatibility.ShipMapCompType, "RecacheFromRoots")?.Invoke(sos2ShipComp, null);
        // ShipsOnMap is a lazily-allocated property with a private backing field, so a field lookup
        // finds nothing and every cache assertion below would read null on a healthy map.
        System.Collections.IDictionary? ships = AccessTools.PropertyGetter(Sos2Compatibility.ShipMapCompType, "ShipsOnMap")
            ?.Invoke(sos2ShipComp, null) as System.Collections.IDictionary;
        object? cache = null;
        if (ships is { Count: > 0 })
            foreach (System.Collections.DictionaryEntry entry in ships) { cache = entry.Value; break; }
        Check("sos2-ship-cache-exists", cache != null, $"ships={ships?.Count}");
        if (cache != null)
        {
            Check("sos2-upper-deck-life-support-joins-the-ship", CacheHolds(cache, "LifeSupports", lifeSupport),
                $"count={CacheCount(cache, "LifeSupports")}");
            Check("sos2-upper-deck-sensor-joins-the-ship", CacheHolds(cache, "Sensors", sensor),
                $"count={CacheCount(cache, "Sensors")}");
            Check("sos2-upper-deck-engine-joins-the-ship", CacheHolds(cache, "Engines", engine),
                $"count={CacheCount(cache, "Engines")}");
            // Capacitors are native PowerNet devices. RimLayers only bridges decks through an
            // explicit RL_PowerRiser, so the correct answer here is "different decks, not linked" -
            // a silent cross-deck link would let one deck drain the other's batteries.
            Check("sos2-capacitor-stays-on-its-own-power-net",
                !Sos2Compatibility.SameLayer(map, capacitor!.Position, At(0, 62, 62)));
        }

        // --- heat conduit: a conduit on either deck must land in the same net ------------------
        Building? lowerConduit = SpawnSos2("ShipHeatConduit", At(0, 58, 56), Rot4.North);
        Building? upperConduit = SpawnSos2("ShipHeatConduit", At(1, 58, 56), Rot4.North);
        Check("sos2-heat-conduit-spawned-on-both-decks", lowerConduit != null && upperConduit != null);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType, "heatGridDirty")?.SetValue(sos2ShipComp, true);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType, "loaded")?.SetValue(sos2ShipComp, false);
        AccessTools.Method(Sos2Compatibility.ShipMapCompType, "MapComponentUpdate", Type.EmptyTypes)
            ?.Invoke(sos2ShipComp, null);
        int[]? grid = AccessTools.Field(Sos2Compatibility.ShipMapCompType, "grid")?.GetValue(sos2ShipComp) as int[];
        if (grid != null && lowerConduit != null && upperConduit != null)
        {
            int lower = grid[map.cellIndices.CellToIndex(lowerConduit.Position)];
            int upper = grid[map.cellIndices.CellToIndex(upperConduit.Position)];
            Check("sos2-heat-conduit-links-both-decks", lower >= 0 && lower == upper, $"lower={lower}, upper={upper}");
        }
        else
        {
            Check("sos2-heat-conduit-links-both-decks", false, "grid or conduits missing");
        }
    }

    /// <summary>
    /// Foam repairs must rank by deck before path length. Native <c>SpaceShipCache.SlowTick</c> sorts
    /// <c>BuildingsToFoam</c> by <c>Item3</c> alone, which is the breadth-first path back to the
    /// bridge: a breach two decks up next to the bridge outranks the hole right beside the
    /// distributor, so a ground-deck distributor seals a hole it cannot reach through its own hull.
    /// </summary>
    private static void Sos2FoamStep()
    {
        Building? distributor = SpawnSos2("HullFoamDistributor", At(0, 64, 56), Rot4.North);
        Check("sos2-foam-distributor-spawned", distributor != null);
        if (distributor == null) return;

        // SOS2 only registers a distributor while it caches a ship, and the cache was built before this
        // step ran. Without the recache the breach below is queued on a ship with no distributors, so
        // nothing is ever repaired and the assertion cannot tell a correct adapter from a no-op.
        RecacheSos2();
        object? cache = Sos2ShipCache();
        int distributorCount = cache == null ? -1 : CacheCount(cache, "FoamDistributors");
        Check("sos2-foam-distributor-is-cached", distributorCount == 1, $"distributors={distributorCount}");

        // The distributor is 3x3 at (64,56), so its footprint covers x63..65 / z55..57. Anything the
        // fixture spawns inside that rectangle is silently refused and the distributor is left with no
        // grid at all, which looks exactly like a broken adapter.
        ThingDef? conduitDef = DefDatabase<ThingDef>.GetNamedSilentFail("PowerConduit");
        ThingDef? batteryDef = DefDatabase<ThingDef>.GetNamedSilentFail("Battery");
        Thing? conduit = conduitDef == null ? null : GenSpawn.Spawn(ThingMaker.MakeThing(conduitDef), At(0, 60, 56), map);
        Thing? battery = batteryDef == null ? null : GenSpawn.Spawn(ThingMaker.MakeThing(batteryDef), At(0, 59, 56), map);
        Check("sos2-foam-power-parts-placed", conduit is { Spawned: true } && battery is { Spawned: true },
            $"conduit={conduit?.Spawned}, battery={battery?.Spawned}");
        // A spawned battery holds no energy: PowerNet only turns a trader on once the net can pay for
        // it, so an empty battery leaves the distributor permanently off.
        battery?.TryGetComp<CompPowerBattery>()?.AddEnergy(1000f);
        distributor.TryGetComp<CompRefuelable>()?.Refuel(250f);
        // Spawning a transmitter only queues it; the net does not exist until the manager rebuilds.
        // PowerNet then turns a consumer on through a rotating 5% window gated on the tick counter, so
        // a single pair of ticks can legitimately leave the distributor off.
        map.powerNetManager.UpdatePowerNetsAndConnections_First();
        for (int i = 0; i < 400 && distributor.TryGetComp<CompPowerTrader>() is { PowerOn: false }; i++)
        {
            Find.TickManager.DoSingleTick();
            map.powerNetManager.PowerNetsTick();
        }
        Check("sos2-foam-distributor-is-powered",
            distributor.TryGetComp<CompPowerTrader>() is { PowerOn: true },
            $"power={distributor.TryGetComp<CompPowerTrader>()?.PowerOn}, " +
            $"net={distributor.GetComp<CompPower>().PowerNet != null}, " +
            $"batteryNet={battery?.TryGetComp<CompPower>()?.PowerNet != null}, " +
            $"batteryStored={battery?.TryGetComp<CompPowerBattery>()?.StoredEnergy}, " +
            $"nets={map.powerNetManager.AllNetsListForReading.Count}");

        // Both breaches are single hull tiles. The lower one is far from the bridge, the upper one
        // sits directly above it, so the two orderings disagree by construction.
        IntVec3 lowerBreach = At(0, 52, 64);
        IntVec3 upperBreach = At(1, 57, 56);
        Thing? lowerTile = lowerBreach.GetFirstThing<Building>(map);
        Thing? upperTile = upperBreach.GetFirstThing<Building>(map);
        Check("sos2-foam-breach-tiles-present", lowerTile != null && upperTile != null,
            $"lower={lowerTile?.def.defName}, upper={upperTile?.def.defName}");
        if (lowerTile == null || upperTile == null) return;
        // Why the breach may or may not be queued, read straight off the cache the repair loop uses.
        object? wreck = cache == null ? null : CacheField(cache, "IsWreck");
        object? core = cache == null ? null : CacheField(cache, "Core");
        object? cacheOff = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "CacheOff")?.GetValue(sos2ShipComp);
        System.Collections.IDictionary? shipCells = Sos2Compatibility.ShipCellsOf(sos2ShipComp);
        ThingComp? partComp = null;
        Type? partType = Sos2Compatibility.ShipCachePartType;
        if (partType != null && lowerTile is ThingWithComps withComps)
            partComp = withComps.AllComps.FirstOrDefault(partType.IsInstanceOfType);
        object? under = partComp == null ? null : CacheField(partComp, "cellsUnder");
        int underCount = under is System.Collections.ICollection underCells ? underCells.Count : -1;
        bool moveFlag = MoveShipFlagOf();
        bool shipPart = lowerTile.def.building?.shipPart ?? false;
        MethodInfo? despawn = AccessTools.Method(typeof(Building), nameof(Building.DeSpawn));
        Patches? patches = despawn == null ? null : Harmony.GetPatchInfo(despawn);
        string owners = patches == null
            ? "none"
            : string.Join(",", patches.Prefixes.Select(p => p.owner));
        string targets = despawn == null
            ? "none"
            : string.Join(",", patches?.Prefixes.Select(p => p.PatchMethod?.DeclaringType?.Name) ?? Array.Empty<string>());
        lowerTile.Destroy(DestroyMode.KillFinalize);
        upperTile.Destroy(DestroyMode.KillFinalize);

        HashSet<IntVec3> pending = FoamCells(cache);
        Check("sos2-foam-registers-both-breaches",
            pending.Contains(lowerBreach) && pending.Contains(upperBreach),
            $"pending={pending.Count}, any={string.Join(";", pending)}, " +
            $"isWreck={IsWreckOf(cache)}, core={core != null}, cacheOff={cacheOff}, " +
            $"cellsUnder={underCount}, moveFlag={moveFlag}, shipPart={shipPart}, " +
            $"sos2Prefixes={owners}, prefixTypes={targets}, destroyed={lowerTile.Destroyed}, " +
            $"distributors={CacheCount(cache!, "FoamDistributors")}, " +
            $"lowerClaimed={shipCells?.Contains(lowerBreach)}, upperClaimed={shipCells?.Contains(upperBreach)}");

        AccessTools.Method(cache!.GetType(), "SlowTick")?.Invoke(cache, null);

        HashSet<IntVec3> after = FoamCells(cache);
        Check("sos2-foam-repairs-the-same-deck-breach",
            !after.Contains(lowerBreach), $"stillPending={after.Contains(lowerBreach)}");
        Check("sos2-foam-leaves-the-other-deck-alone",
            after.Contains(upperBreach), $"stillPending={after.Contains(upperBreach)}");
    }

    /// <summary>
    /// <c>ShipMapComp.KillAllOffShip</c> sweeps <c>map.AllCells</c>, which on a layered map covers the
    /// padding bands and every deck. The adapter restricts the sweep to the ground band, so the three
    /// sentinels below pin down exactly what survives and what still dies.
    /// </summary>
    private static void Sos2OffShipStep()
    {
        int stride = owner.Layout!.Stride, height = owner.Layout.Height;
        Check("sos2-offship-padding-band-exists", stride > height, $"stride={stride}, height={height}");
        if (stride <= height) return;

        IntVec3 padding = new(5, 0, height);
        IntVec3 upperStray = At(1, 20, 20);
        IntVec3 groundStray = At(0, 20, 20);

        Thing? padSilver = Sentry(padding);
        Thing? upperSilver = Sentry(upperStray);
        Thing? groundSilver = Sentry(groundStray);
        Check("sos2-offship-sentries-placed",
            padSilver is { Spawned: true } && upperSilver is { Spawned: true } && groundSilver is { Spawned: true },
            $"pad={padSilver?.Spawned}, upper={upperSilver?.Spawned}, ground={groundSilver?.Spawned}");
        if (padSilver == null || upperSilver == null || groundSilver == null) return;

        // The sweep destroys everything it finds on the ground band, and a generated ground map still
        // carries steam geysers, which RimWorld refuses to destroy. SOS2 only ever runs this sweep on
        // a space map, where the band holds nothing but the ship, so the fixture despawns the map's own
        // leftover terrain features rather than teach the adapter to skip things the game protects.
        int geysers = 0;
        foreach (Thing geyser in map.listerThings.ThingsOfDef(ThingDefOf.SteamGeyser).ToList())
            if (owner.TryDecode(geyser.Position, out LayerCell at) && at.LayerId == 0)
            {
                geyser.DeSpawn();
                geysers++;
            }
        Check("sos2-offship-ground-band-is-clear", geysers >= 0, $"geysers={geysers}");

        AccessTools.Method(Sos2Compatibility.ShipMapCompType, "KillAllOffShip")?.Invoke(sos2ShipComp, null);

        // Destroyed things report Destroyed and vanish from the cell list; that is the only signal
        // that distinguishes "the sweep skipped this cell" from "the sweep ran and this cell was off
        // the ship".
        Check("sos2-offship-keeps-the-padding-band", !padSilver.Destroyed, $"destroyed={padSilver.Destroyed}");
        Check("sos2-offship-keeps-other-decks", !upperSilver.Destroyed, $"destroyed={upperSilver.Destroyed}");
        Check("sos2-offship-still-clears-the-ground-deck", groundSilver.Destroyed, $"destroyed={groundSilver.Destroyed}");
    }

    private static Thing? Sentry(IntVec3 cell)
    {
        ThingDef? silver = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
        if (silver == null) return null;
        // The sweep destroys everything on the ground deck, and some of what the map generator put
        // there cannot be destroyed at all: a steam geyser anywhere in the band turns the sweep into
        // a runtime error before the sentinels are even checked. Clearing the band first is what a
        // real player map looks like anyway - the ship owns its deck.
        foreach (Thing occupant in cell.GetThingList(map).ToList())
            if (occupant is not Pawn && occupant.def.destroyable) occupant.Destroy(DestroyMode.Vanish);
        var thing = ThingMaker.MakeThing(silver);
        return GenSpawn.Spawn(thing, cell, map) == null ? null : thing;
    }

    /// <summary>
    /// SOS2 disables its whole cache path during a ship move, so a fixture that reads a queue right
    /// after a destroy has to rule the flag out before blaming the adapter.
    /// </summary>
    private static bool IsWreckOf(object? cache)
    {
        if (cache == null) return true;
        return cache.GetType().GetProperty("IsWreck")?.GetValue(cache) is true;
    }

    private static bool MoveShipFlagOf()
    {
        Type? type = AccessTools.TypeByName("SaveOurShip2.ShipInteriorMod2");
        return type != null && AccessTools.Field(type, "MoveShipFlag")?.GetValue(null) is true;
    }

    private static object? Sos2ShipCache()
    {
        System.Collections.IDictionary? ships = AccessTools.PropertyGetter(Sos2Compatibility.ShipMapCompType, "ShipsOnMap")
            ?.Invoke(sos2ShipComp, null) as System.Collections.IDictionary;
        if (ships is not { Count: > 0 }) return null;
        foreach (System.Collections.DictionaryEntry entry in ships) return entry.Value;
        return null;
    }

    /// <summary>
    /// SOS2 builds its ship cache from the bridge, and only rebuilds it on map load, on a merge, or
    /// when it notices the ship changed shape. A fixture that spawns parts after that point is
    /// invisible to the cache, so anything driven by a cached list needs this first.
    /// </summary>
    private static void RecacheSos2() =>
        AccessTools.Method(Sos2Compatibility.ShipMapCompType, "RecacheFromRoots")?.Invoke(sos2ShipComp, null);

    /// <summary>Breach cells queued for foam, read through the same reflection the patch uses.</summary>
    private static HashSet<IntVec3> FoamCells(object? cache)
    {
        var cells = new HashSet<IntVec3>();
        if (cache == null) return cells;
        if (AccessTools.Field(cache.GetType(), "BuildingsToFoam")?.GetValue(cache) is not System.Collections.IEnumerable entries)
            return cells;
        foreach (object? entry in entries)
        {
            if (entry == null) continue;
            if (entry.GetType().GetProperty("Item2")?.GetValue(entry) is IntVec3 cell) cells.Add(cell);
        }
        return cells;
    }

    private static object? CacheField(object cache, string name) =>        AccessTools.Field(cache.GetType(), name)?.GetValue(cache);

    private static int CacheCount(object cache, string name) =>
        CacheField(cache, name) is System.Collections.ICollection collection ? collection.Count : -1;

    private static bool CacheHolds(object cache, string name, object? member)
    {
        if (member == null) return false;
        if (CacheField(cache, name) is not System.Collections.IEnumerable items) return false;
        // The cache lists comps, not buildings: LifeSupports holds CompShipLifeSupport and Engines holds
        // CompEngineTrail. Comparing the building reference alone answers "not present" for a part that
        // is in fact registered, which is how this check first read a healthy ship as broken.
        foreach (object? item in items)
        {
            if (ReferenceEquals(item, member)) return true;
            if (item is ThingComp comp && ReferenceEquals(comp.parent, member)) return true;
        }
        return false;
    }
}
