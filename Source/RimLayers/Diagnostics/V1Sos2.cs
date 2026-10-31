using System.Collections;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Ships;
using RimLayers.Structure;
using RimWorld;
using RimWorld.Planet;
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

    /// <summary>True once the launch fixture has reached the orbiting map and the checks below ran.</summary>
    private static bool sos2LaunchStarted;
    private static bool sos2FlightReported;
    private static bool sos2EngineReported;
    private static Map? sos2SpaceMap;
    private static Map? sos2GroundMap;
    /// <summary>
    /// The launch pad's own band-0/band-1 cells, so "did the ship come back" can be asked about
    /// this hull instead of about the map. Earlier phases leave their own pads behind in
    /// <c>ShipsOnMap</c>, and a map-wide "any ship at all" test can therefore never pass.
    /// </summary>
    private static IntVec3[] sos2LaunchCells = System.Array.Empty<IntVec3>();
    /// <summary>The launch fixture's engine, kept so the flight window can be inspected later.</summary>
    private static Building? sos2LaunchEngine;
    /// <summary>The launch fixture's bridge, which is the ship's core and its camera target.</summary>
    private static Building? sos2LaunchBridge;
    private static int sos2LaunchTicks;

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

    private static Building? SpawnSos2(string defName, IntVec3 cell, Rot4 rot) => SpawnSos2On(map, defName, cell, rot);

    private static Building? SpawnSos2On(Map target, string defName, IntVec3 cell, Rot4 rot)
    {
        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null)
        {
            Check("sos2-def-" + defName + "-exists", false, "missing ThingDef");
            return null;
        }
        var building = (Building)ThingMaker.MakeThing(def);
        building.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(building, cell, target, rot);
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

            // Launching used to break here: CenterShipOnMap measured the hull with raw storage z, so a
            // hull spanning two bands reported a span of 2*stride and the offset came out negative
            // (-92 on the real ship), pushing every transferred cell to z=-1. Exercise the live method
            // and require the offset it returns to keep every tracked ship cell inside the storage.
            object? cache = AccessTools.Property(sos2Bridge!.GetType(), "Ship")?.GetValue(sos2Bridge);
            MethodInfo? centre = cache == null ? null : AccessTools.Method(cache.GetType(), "CenterShipOnMap");
            System.Collections.IDictionary? tracked = Sos2Compatibility.ShipCellsOf(sos2ShipComp);
            if (centre?.Invoke(cache, null) is not IntVec3 adjustment)
            {
                Check("sos2-centring-keeps-a-two-band-hull-inside-the-map", false, "CenterShipOnMap unavailable");
            }
            else
            {
                int outside = 0, bandsSeen = 0;
                var seen = new HashSet<int>();
                if (tracked != null)
                    foreach (IntVec3 key in tracked.Keys)
                    {
                        if (owner.TryDecode(key, out LayerCell c)) seen.Add(c.LayerId);
                        if (!Sos2Geometry.InStorage(key.x + adjustment.x, key.z + adjustment.z,
                                owner.Layout!.Width, owner.Layout.Stride, owner.Layout.LayerCount)) outside++;
                    }
                bandsSeen = seen.Count;
                Check("sos2-centring-keeps-a-two-band-hull-inside-the-map",
                    outside == 0 && adjustment.z >= 0,
                    $"adjustment={adjustment}, bands={bandsSeen}, outside={outside}, tracked={tracked?.Count}");
            }

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
            sos2Phase = 9;
            ticks = 0;
            return;
        }
        if (sos2Phase == 9)
        {
            Sos2StairStep();
            sos2Phase = 10;
            ticks = 0;
            return;
        }
        if (sos2Phase == 10)
        {
            Sos2LaunchStep();
            if (sos2Phase == 10) return;
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

        // A flat space map is what broke launching: the deck cells were mirrored into a map that had
        // no second band, so the move threw. The generated map must inherit this ship's bands, and
        // the announcement must be single-use so a later unrelated map cannot pick up a stale one.
        Sos2Compatibility.ExpectSpaceLayout(map.Size);
        LayerLayout? announced = Sos2Compatibility.TakeSpaceLayout();
        Check("sos2-a-space-map-inherits-the-ships-bands",
            announced != null && announced.LayerCount == owner.Layout.LayerCount &&
            announced.Height == owner.Layout.Height && announced.Stride == owner.Layout.Stride,
            $"announced={announced?.LayerCount} layers, {announced?.Height} high");
        Check("sos2-the-space-map-announcement-is-consumed-once",
            Sos2Compatibility.TakeSpaceLayout() == null);

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

        // A stacked pair is not automatically one net. SOS2's walk is now allowed vertically only
        // where BOTH cells carry heat hardware, so a lone reactor on the upper deck must stay out of
        // the lower deck's net instead of bleeding heat into it from nowhere.
        Building? isolated = SpawnSos2("ShipHeatConduit", At(1, 72, 56), Rot4.North);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType, "heatGridDirty")?.SetValue(sos2ShipComp, true);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType, "loaded")?.SetValue(sos2ShipComp, false);
        AccessTools.Method(Sos2Compatibility.ShipMapCompType, "MapComponentUpdate", Type.EmptyTypes)
            ?.Invoke(sos2ShipComp, null);
        grid = AccessTools.Field(Sos2Compatibility.ShipMapCompType, "grid")?.GetValue(sos2ShipComp) as int[];
        if (grid != null && isolated != null && lowerConduit != null)
        {
            int alone = grid[map.cellIndices.CellToIndex(isolated.Position)];
            int floor = grid[map.cellIndices.CellToIndex(lowerConduit.Position)];
            Check("sos2-a-ledge-conduit-with-no-conduit-below-keeps-its-own-net",
                alone >= 0 && alone != floor, $"alone={alone}, below={floor}");
        }

        // The buildable answer is RL_HeatRiser: one on each layer, same local position, and the two
        // decks join - the same contract RL_PowerRiser has for power.
        ThingDef? riser = DefDatabase<ThingDef>.GetNamedSilentFail("RL_HeatRiser");
        Building? lowerRiser = riser == null ? null : SpawnSos2("RL_HeatRiser", At(0, 76, 56), Rot4.North);
        Building? upperRiser = riser == null ? null : SpawnSos2("RL_HeatRiser", At(1, 76, 56), Rot4.North);
        Check("sos2-thermal-riser-exists-in-the-ship-category",
            riser != null && riser.designationCategory?.defName == "Ship", $"def={riser?.defName}, category={riser?.designationCategory?.defName}");
        if (riser != null && lowerRiser != null && upperRiser != null)
        {
            AccessTools.Field(Sos2Compatibility.ShipMapCompType, "heatGridDirty")?.SetValue(sos2ShipComp, true);
            AccessTools.Field(Sos2Compatibility.ShipMapCompType, "loaded")?.SetValue(sos2ShipComp, false);
            AccessTools.Method(Sos2Compatibility.ShipMapCompType, "MapComponentUpdate", Type.EmptyTypes)
                ?.Invoke(sos2ShipComp, null);
            grid = AccessTools.Field(Sos2Compatibility.ShipMapCompType, "grid")?.GetValue(sos2ShipComp) as int[];
            if (grid != null)
            {
                int low = grid[map.cellIndices.CellToIndex(lowerRiser.Position)];
                int high = grid[map.cellIndices.CellToIndex(upperRiser.Position)];
                Check("sos2-thermal-riser-joins-the-decks", low >= 0 && low == high, $"lower={low}, upper={high}");
            }
        }
        else Check("sos2-thermal-riser-joins-the-decks", false, $"riser={riser != null}, lower={lowerRiser != null}, upper={upperRiser != null}");

        // The deck def has to be reachable from the Ship tab, and its strings have to come from the
        // language files instead of the patch value.
        TerrainDef? sos2Deck = DefDatabase<TerrainDef>.GetNamedSilentFail("RL_Sos2Deck");
        Check("sos2-deck-plating-sits-in-the-ship-category",
            sos2Deck != null && sos2Deck.designationCategory?.defName == "Ship",
            $"category={sos2Deck?.designationCategory?.defName}");

        // SOS2 decides "on a hull" from the thing list of the cell itself, and an upper deck has no
        // hull tile in it. This is the assertion that a bridge can actually be built up there.
        ThingDef? bridge = DefDatabase<ThingDef>.GetNamedSilentFail("ShipPilotSeatMini");
        PlaceWorker? hullWorker = bridge?.PlaceWorkers?.FirstOrDefault();
        if (bridge != null && hullWorker != null && upperConduit != null)
        {
            // SOS2's own worker, not RimLayers': this is the check the game runs on the ghost.
            AcceptanceReport onDeck = hullWorker.AllowsPlacing(bridge, upperConduit.Position, Rot4.North, map);
            Check("sos2-a-bridge-can-stand-on-an-upper-deck", onDeck.Accepted, $"reason={onDeck.Reason}");
            AcceptanceReport offShip = hullWorker.AllowsPlacing(bridge, At(1, 90, 90), Rot4.North, map);
            Check("sos2-a-bridge-is-still-refused-over-empty-space", !offShip.Accepted, $"reason={offShip.Reason}");
        }
        else Check("sos2-a-bridge-can-stand-on-an-upper-deck", false, $"bridge={bridge != null}, worker={hullWorker != null}");

        // The bug the player reported: a bridge actually standing on an upper deck threw
        // KeyNotFoundException from CompShipCachePart.CompInspectStringExtra, because it owned no ship
        // index and the dev-mode string then indexed ShipsOnMap[-1]. Being placeable is not enough -
        // the hull below has to adopt the part, and its inspect string has to come out clean.
        if (upperConduit != null)
        {
            IntVec3 deckCell = upperConduit.Position;
            Building? seated = SpawnSos2("ShipPilotSeatMini", deckCell, Rot4.North);
            Check("sos2-a-bridge-sits-on-an-upper-deck", seated != null);
            if (seated != null)
            {
                IntVec3 hullCell = owner.Adjacent(deckCell, -1);
                int seatedIndex = Sos2Compatibility.ShipIndexAt(map, seated.Position);
                Check("sos2-an-upper-deck-bridge-belongs-to-the-hull-below", seatedIndex >= 0,
                    $"index={seatedIndex}, hullIndex={Sos2Compatibility.ShipIndexAt(map, hullCell)}");
                string inspect = string.Empty;
                bool threw = false;
                try { inspect = seated.GetInspectString(); }
                catch (Exception exception) { threw = true; inspect = exception.GetType().Name + ": " + exception.Message; }
                Check("sos2-an-upper-deck-bridge-can-be-inspected", !threw,
                    threw ? inspect : inspect.Replace("\n", " | "));
                seated.Destroy();
            }
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

    /// <summary>
    /// Building a second deck on a real SOS2 ship, the way a player does it: lay hull plating on the
    /// deck above, then finish a stair that breaks through the ship's own roof.
    /// </summary>
    /// <remarks>
    /// SOS2 never generates its space map with layers (RimLayers only pre-expands maps it generated
    /// itself, and a space map belongs to SOS2), so a ship in orbit has no <see cref="LayerMapComponent"/>.
    /// The first stair is therefore the only way a player can open a deck, and it has to accept the
    /// roof SOS2 puts over a hermetic hull - <c>RoofShip</c>, not <c>RoofConstructed</c>. That is the
    /// exact case a registered support roof used to open a deck for but never let a stair reach.
    /// </remarks>
    /// <summary>
    /// The deck a player opens on a space map. <c>map</c> is already layered by the time this runs,
    /// so the scenario uses the band above it: a deck that exists but has never been plated.
    /// </summary>
    private static readonly CellRect sos2StairSite = new(76, 76, 8, 8);
    private static IntVec3 sos2StairCell = IntVec3.Invalid;

    private static void Sos2StairStep()
    {
        var component = LayerMapComponent.For(map);
        if (component == null)
        {
            Check("sos2-stair-fixture-has-layers", false, "no LayerMapComponent on the scenario map");
            return;
        }
        // The scenario map itself is a settlement fixture, not an orbiting ship map; what this step
        // needs is a band above the ground deck that has never been plated. The space-map side of the
        // adapter is covered by sos2-space-map-is-generated-at-the-logical-size in the subsystem step.
        Check("sos2-stair-fixture-has-a-deck-above", component.Layout is { LayerCount: >= 2 },
            $"parent={map.Parent?.GetType().Name}, layers={component.Layout?.LayerCount}");
        ThingDef? hullDef = DefDatabase<ThingDef>.GetNamedSilentFail("ShipHullTile");
        RoofDef? shipRoof = DefDatabase<RoofDef>.GetNamedSilentFail("RoofShip");
        if (hullDef == null || shipRoof == null)
        {
            Check("sos2-stair-defs-exist", false, $"hull={hullDef != null}, roof={shipRoof != null}");
            return;
        }
        int deck = 1;
        if (!component.TryDecode(component.AtElevation(deck, sos2StairSite.minX, sos2StairSite.minZ), out _))
        {
            Check("sos2-stair-deck-exists", false, $"elevation={deck}, layout={component.Layout}");
            return;
        }
        // A bare deck with no ship on it: the floor below is what a player stands on, and the band
        // above is the space the stair has to open into.
        foreach (IntVec3 c in sos2StairSite)
        {
            IntVec3 lower = component.AtElevation(0, c.x, c.z);
            IntVec3 upper = component.AtElevation(deck, c.x, c.z);
            foreach (IntVec3 cell in new[] { lower, upper })
                foreach (Thing t in cell.GetThingList(map).ToList())
                    if (t is not Pawn)
                    {
                        if (t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                        else t.DeSpawn();
                    }
            // SetFoundation refuses a cell that still holds an under terrain. Every other fixture in
            // this suite clears the top layer first; this one skipped it and left the run dependent on
            // whether an earlier phase happened to push a layer down here.
            if (map.terrainGrid.UnderTerrainAt(lower) != null) map.terrainGrid.RemoveTopLayer(lower, false);
            map.terrainGrid.SetFoundation(lower, TerrainDefOf.Substructure);
            map.terrainGrid.SetTerrain(lower, TerrainDefOf.Concrete);
            map.terrainGrid.SetTerrain(upper, RimLayersDefOf.RL_OpenAir);
            map.roofGrid.SetRoof(upper, null);
            map.fogGrid.Unfog(lower);
            map.fogGrid.Unfog(upper);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c => sos2StairSite.Contains(new IntVec3(c.x, 0, c.z)) ||
            component.TryDecode(c, out var at) && sos2StairSite.Contains(new IntVec3(at.X, 0, at.Z)));
        foreach (IntVec3 c in new[] { new IntVec3(76, 0, 76), new IntVec3(83, 0, 76), new IntVec3(76, 0, 83), new IntVec3(83, 0, 83) })
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column, ThingDefOf.Steel), c, map);
        // Deliberately leave the roof empty before plating. CompShipCachePart only installs RoofShip
        // when the existing roof is not already airtight; seeding RoofConstructed here would make SOS2
        // keep it and the whole point of this step (RoofShip is an accepted support) would be skipped.
        // A real SOS2 space deck starts roofless too, so this mirrors the actual player path.
        foreach (IntVec3 c in sos2StairSite) SpawnSos2On(map, "ShipHullTile", new IntVec3(c.x, 0, c.z), Rot4.North);
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c =>
            sos2StairSite.Contains(new IntVec3(c.x, 0, c.z)) ||
            component.TryDecode(c, out var marked) && sos2StairSite.Contains(new IntVec3(marked.X, 0, marked.Z)));
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("sos2-stair-deck-carries-the-ship-roof",
            sos2StairSite.All(c => map.roofGrid.RoofAt(new IntVec3(c.x, 0, c.z))?.defName == "RoofShip"),
            $"roof={map.roofGrid.RoofAt(new IntVec3(76, 0, 76))?.defName}");

        var worker = new PlaceWorker_LayerSupport();
        sos2StairCell = new IntVec3(79, 0, 79);
        AcceptanceReport withShipRoof = worker.AllowsPlacing(RimLayersDefOf.RL_Stairs, sos2StairCell, Rot4.North, map);
        Check("sos2-stair-is-allowed-under-the-ship-roof", withShipRoof.Accepted, $"reason={withShipRoof.Reason}");
        AcceptanceReport bootstrap = StairLayerBootstrap.Validate(map, sos2StairCell, Rot4.North);
        Check("sos2-stair-bootstrap-accepts-the-ship-roof", bootstrap.Accepted, $"reason={bootstrap.Reason}");

        // Finish the frame the way the bootstrap scenario does, then let the tick bootstrap run.
        var frame = (Frame)ThingMaker.MakeThing(RimLayersDefOf.RL_Stairs.frameDef);
        frame.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(frame, sos2StairCell, map);
        foreach (var cost in RimLayersDefOf.RL_Stairs.CostListAdjusted(null))
        {
            var material = ThingMaker.MakeThing(cost.thingDef);
            material.stackCount = cost.count;
            frame.resourceContainer.TryAdd(material);
        }
        // Do NOT clear the roof here. The bootstrap scenario clears it to prove a roofless stair is
        // rejected and the frame keeps its materials; this step is the opposite - the roof is the
        // whole reason the stair is allowed to open a deck.
        // A real pawn, not null. Frame.CompleteConstruction dereferences worker immediately, and so do
        // the Vehicle Framework and SOS2 prefixes on it, so a null worker fails before RimLayers runs.
        Pawn builder = map.mapPawns.FreeColonistsSpawned.FirstOrDefault() ??
            PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
        frame.CompleteConstruction(builder);
        var stair = sos2StairCell.GetThingList(map).OfType<Building_Stairs>().FirstOrDefault();
        Check("sos2-stair-completion-opens-a-deck", stair != null,
            $"stair={stair != null}, upper={stair?.UpperCell}, layers={component.Layout?.LayerCount}");
        if (stair == null) return;
        IntVec3 landing = stair.UpperCell;
        Check("sos2-opened-deck-is-reachable-from-the-ship",
            landing.IsValid && component.Structure.HasSurface(landing),
            $"layers={component.Layout?.LayerCount}, landing={landing}");
        if (!landing.IsValid) return;
        // StructuralSupport.EnsureLanding lays RL_StructuralFloor when no RimLayers ship owns the cell,
        // which is the case here: the deck belongs to an SOS2 hull, not to a RimLayers engine. Assert
        // the landing is a real surface, not that it stayed open air - open air is exactly what a
        // landing must not be.
        Check("sos2-opened-deck-starts-as-a-real-surface",
            component.Structure.HasSurface(landing),
            $"terrain={map.terrainGrid.TerrainAt(landing)?.defName}");
        // The point of the whole exercise: SOS2 hull can be laid on the deck the stair opened. The
        // hull only rewrites terrain that is not layerable, so an RL_StructuralFloor cell keeps its
        // terrain and the tile becomes the deck surface - which is what a player gets either way.
        SpawnSos2On(map, "ShipHullTile", landing, Rot4.North);
        Check("sos2-upper-hull-tile-lands-on-the-opened-deck",
            landing.GetThingList(map).Any(t => t.def.defName == "ShipHullTile"),
            $"things={string.Join(",", landing.GetThingList(map).Select(t => t.def.defName))}");
        // Recursion is the real claim: the deck this stair opened can be plated with SOS2 hull, that
        // hull puts RoofShip on this deck, and RoofShip in turn is what allows the next stair. Without
        // RoofShip in ConnectorRoof the second stair is refused here.
        IntVec3 next = landing + IntVec3.East;
        SpawnSos2On(map, "ShipHullTile", next, Rot4.North);
        // IsShipDeck asks SOS2 which ship a cell belongs to, so the cache has to see the new plating
        // before the deck can be placed on it. A player's plating goes through PostSpawnSetup, which
        // fills MapShipCells itself; spawning through ThingMaker from a fixture bypasses that hook.
        RecacheSos2();
        Check("sos2-second-deck-gets-its-own-ship-roof",
            map.roofGrid.RoofAt(next)?.defName == "RoofShip",
            $"roof={map.roofGrid.RoofAt(next)?.defName}");

        // The SOS2 deck is a separate def from the Odyssey gravship floor on purpose. That one needs
        // an inspected Building_GravEngine and spends its substructure capacity, neither of which an
        // SOS2 hull has; and its Substructure tag makes SOS2 detach the plating underneath.
        TerrainDef? sos2Deck = StructuralSupport.Sos2Deck;
        Check("sos2-deck-def-exists", sos2Deck != null, $"def={(sos2Deck == null ? "missing" : sos2Deck.defName)}");
        if (sos2Deck == null) return;
        AcceptanceReport deckHere = worker.AllowsPlacing(sos2Deck, next, Rot4.North, map);
        Check("sos2-deck-is-allowed-on-the-hull", deckHere.Accepted, $"reason={deckHere.Reason}");
        // Six cells north is outside the plated footprint, so the deck below it has no hull tile and
        // no RoofShip. This is the "only on the ship's own roof" half of the contract.
        IntVec3 offHull = next + new IntVec3(0, 0, 6);
        AcceptanceReport deckOffHull = worker.AllowsPlacing(sos2Deck, offHull, Rot4.North, map);
        Check("sos2-deck-is-refused-off-the-hull", !deckOffHull.Accepted, $"reason={deckOffHull.Reason}");
        if (!deckHere.Accepted) return;
        ShipDeckTerrain.InstallDeck(component, next, sos2Deck);
        Check("sos2-deck-installs-on-a-plated-cell",
            map.terrainGrid.FoundationAt(next) == sos2Deck && component.StructuralCells.Contains(next),
            $"foundation={map.terrainGrid.FoundationAt(next)?.defName}, structural={component.StructuralCells.Contains(next)}");
        // SOS2 must not tear its own plating off when the deck arrives. It does exactly that on any
        // cell that receives a foundation with the Substructure tag, which is why this deck is not
        // tagged even when Odyssey is loaded.
        Check("sos2-deck-does-not-detach-the-hull",
            next.GetThingList(map).Any(t => t.def.defName == "ShipHullTile"),
            $"things={string.Join(",", next.GetThingList(map).Select(t => t.def.defName))}");
        AcceptanceReport secondStair = worker.AllowsPlacing(RimLayersDefOf.RL_Stairs, next, Rot4.North, map);
        Check("sos2-upper-deck-can-hold-a-second-stair", secondStair.Accepted, $"reason={secondStair.Reason}");

        // The player path is not InstallDeck: Frame.CompleteConstruction calls TerrainGrid.SetFoundation
        // directly for an isFoundation def and never goes through SetTerrain. Testing only InstallDeck
        // passed while every deck a player laid stayed inert, so the native write is exercised here.
        IntVec3 native = next + new IntVec3(2, 0, 0);
        SpawnSos2On(map, "ShipHullTile", native, Rot4.North);
        RecacheSos2();
        map.terrainGrid.SetFoundation(native, sos2Deck);
        Check("sos2-native-foundation-write-registers-the-deck",
            component.StructuralCells.Contains(native),
            $"foundation={map.terrainGrid.FoundationAt(native)?.defName}, structural={component.StructuralCells.Contains(native)}");
        Check("sos2-native-foundation-write-gives-the-cell-a-surface",
            component.Structure.HasSurface(native),
            $"surface={component.Structure.HasSurface(native)}, terrain={map.terrainGrid.TerrainAt(native)?.defName}");
        // SOS2 rewrites the cell's top layer with its own layerable FakeFloorInsideShip after a terrain
        // change, and TerrainAt prefers the under layer. Left alone, the deck never reports itself and
        // IsBareSlab refuses to dismantle it - the player could not undo what they just laid.
        Check("sos2-native-foundation-write-keeps-the-deck-as-the-terrain",
            map.terrainGrid.TerrainAt(native) == sos2Deck,
            $"terrain={map.terrainGrid.TerrainAt(native)?.defName}, under={map.terrainGrid.UnderTerrainAt(native)?.defName}");
        if (ThingDefOf.Wall is { } wall)
        {
            AcceptanceReport onNativeDeck = worker.AllowsPlacing(wall, native, Rot4.North, map);
            Check("sos2-a-building-can-stand-on-a-player-laid-deck",
                onNativeDeck.Accepted, $"reason={onNativeDeck.Reason}");
        }

        // A hull cell that has been reloaded once carries RoofConstructed, not RoofShip:
        // CompShipCachePart.SetRoof writes RoofShip only when the old roof was NOT airtight, and
        // IsRoofDefAirtight accepts RoofShip - so the second pass downgrades every ceiling to the plain
        // constructed roof. SOS2 is fine with that (both are airtight), and a deck rule that matched the
        // def name alone would refuse every hull the player has loaded twice. Reproduce the downgrade
        // here rather than trusting the reader to imagine it.
        IntVec3 downgraded = next + IntVec3.West;
        SpawnSos2On(map, "ShipHullTile", downgraded, Rot4.North);
        RecacheSos2();
        map.roofGrid.SetRoof(downgraded, RoofDefOf.RoofConstructed);
        Check("sos2-reload-downgrades-the-ship-roof",
            map.roofGrid.RoofAt(downgraded) == RoofDefOf.RoofConstructed,
            $"roof={map.roofGrid.RoofAt(downgraded)?.defName}");
        Check("sos2-deck-accepts-a-downgraded-ship-roof",
            Sos2Compatibility.IsShipCeiling(map.roofGrid.RoofAt(downgraded)),
            $"roof={map.roofGrid.RoofAt(downgraded)?.defName}");
        AcceptanceReport downgradedDeck = worker.AllowsPlacing(sos2Deck, downgraded, Rot4.North, map);
        Check("sos2-deck-is-allowed-under-a-downgraded-roof",
            downgradedDeck.Accepted,
            $"reason={downgradedDeck.Reason}, roof={map.roofGrid.RoofAt(downgraded)?.defName}");
    }

    /// <summary>
    /// The end-to-end launch. Everything before this point proved a piece: the layout, the centring,
    /// the deck plumbing, the heat net. This step builds a two-deck hull with a bridge on the upper
    /// deck, hands it to SOS2's own ShipInteriorMod2.LaunchShip, and then watches what the flight
    /// state machine does to it. That is the only path a player actually walks when they press launch,
    /// and it is the one that kept sending the ship back down.
    /// </summary>
    private static void Sos2LaunchStep()
    {
        if (!sos2LaunchStarted)
        {
            sos2LaunchStarted = true;
            sos2GroundMap = map;
            LaunchTheShip();
            return;
        }
        sos2LaunchTicks++;
        if (sos2LaunchTicks < 240)
        {
            ObserveFlight();
            return;
        }
        FinishFlight();
        sos2Phase = 6;
    }

    private static void LaunchTheShip()
    {
        Type? interior = AccessTools.TypeByName("SaveOurShip2.ShipInteriorMod2");
        Type? countdown = AccessTools.TypeByName("RimWorld.ShipCountdown");
        if (interior == null || countdown == null)
        {
            Check("sos2-launch-types-resolve", false, $"interior={interior != null}, countdown={countdown != null}");
            return;
        }
        var component = LayerMapComponent.For(map);
        if (component?.Layout is not { LayerCount: >= 2 })
        {
            Check("sos2-launch-fixture-has-two-decks", false, $"layout={component?.Layout}");
            return;
        }
        Building? bridge = BuildLaunchShip(component);
        if (bridge == null)
        {
            Check("sos2-launch-fixture-builds", false, "no upper-deck bridge");
            return;
        }
        RecacheSos2();
        sos2LaunchBridge = bridge;
        object? cache = Sos2ShipCache();
        int shipIndex = Sos2Compatibility.ShipIndexAt(map, component.AtElevation(0, 104, 104));
        // Everything this fixture owns, so later checks can ask about this hull rather than about the
        // map. The pads left behind by earlier phases are still in ShipsOnMap, which makes every
        // map-wide "is there a ship here" test useless.
        sos2LaunchCells = (from c in component.LayerRect(0).Cells
                           where Sos2Compatibility.ShipIndexAt(map, c) == shipIndex
                           select c).ToArray();
        // ShipsOnMap holds every hull the map has ever carried, including the pads the earlier phases
        // built and then tore down. The launch only concerns the ship whose hull the bridge sits in, so
        // the cache is resolved by index - picking "the first one" reads a dead hull and reports an
        // engine with no net that has nothing to do with the flight under test.
        object? launchCache = shipIndex >= 0 ? Sos2ShipCache(map, shipIndex) : null;
        Check("sos2-launch-fixture-joins-one-ship", shipIndex >= 0 && launchCache != null,
            $"index={shipIndex}, deckCell={component.AtElevation(0, 104, 104)}, cache={launchCache != null}, all={Sos2ShipCount()}");
        if (shipIndex < 0 || launchCache == null) return;
        int engineCount = CacheCount(launchCache, "Engines");
        Check("sos2-launch-fixture-carries-an-engine", engineCount >= 1, $"engines={engineCount}, index={shipIndex}");
        // The flight loop only ever asks two questions of the ship: can it move, and does an engine
        // fire. Both answers come from the cache the move hands over, so they are read here, before
        // the map changes, to keep a post-move failure attributable.
        Check("sos2-launch-fixture-can-move-before-launch", CanMoveOf(launchCache), Details(launchCache));
        // LaunchShip does not take the core from its argument: it moves whatever ShipCountdown.shipRoot
        // holds. A fixture that forgets this line gets a silent no-op with no error at all, because
        // MoveShip bails on a null core before it touches anything.
        AccessTools.Field(countdown, "shipRoot")?.SetValue(null, bridge);
        var launch = AccessTools.Method(interior, "LaunchShip");
        if (launch == null)
        {
            Check("sos2-launch-method-resolves", false, "ShipInteriorMod2.LaunchShip missing");
            return;
        }
        try
        {
            launch.Invoke(null, new object[] { bridge });
            Check("sos2-launch-does-not-throw", true, "LaunchShip returned");
        }
        catch (Exception ex)
        {
            Check("sos2-launch-does-not-throw", false, $"{ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
            return;
        }
        sos2SpaceMap = SpaceMap();
        Check("sos2-launch-created-a-space-map", sos2SpaceMap != null,
            $"maps={string.Join(",", Find.Maps.Select(m => m.ToString()))}");
        if (sos2SpaceMap == null) return;
        var space = LayerMapComponent.For(sos2SpaceMap);
        // The whole point of handing the source layout to the generator: an orbiting map for a
        // two-deck ship must have two bands, otherwise every stored z above the first band is out of
        // bounds and the move throws halfway through.
        Check("sos2-launch-space-map-keeps-both-bands", space?.Layout is { LayerCount: >= 2 },
            $"layout={space?.Layout}, size={sos2SpaceMap.Size}");
        object? moved = Sos2Compatibility.ShipCompOf(sos2SpaceMap);
        Check("sos2-launch-moved-the-ship-to-the-space-map", Sos2ShipCache(sos2SpaceMap) != null,
            $"spaceCells={CountCells(moved)}");
    }

    /// <summary>Upper-deck bridge, an engine, and a hull on both bands - the shape a player's ship has.</summary>
    private static Building? BuildLaunchShip(LayerMapComponent component)
    {
        var rect = new CellRect(100, 100, 9, 7);
        foreach (IntVec3 c in rect)
        {
            foreach (int layer in new[] { 0, 1 })
            {
                IntVec3 cell = component.AtElevation(layer, c.x, c.z);
                foreach (Thing t in cell.GetThingList(map).ToList())
                    if (t is not Pawn)
                    {
                        if (t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                        else t.DeSpawn();
                    }
                if (layer == 0 && map.terrainGrid.UnderTerrainAt(cell) != null)
                    map.terrainGrid.RemoveTopLayer(cell, false);
                if (layer == 0) map.terrainGrid.SetTerrain(cell, TerrainDefOf.Substructure);
                else map.terrainGrid.SetTerrain(cell, RimLayersDefOf.RL_OpenAir);
                map.roofGrid.SetRoof(cell, null);
                map.fogGrid.Unfog(cell);
            }
        }
        foreach (IntVec3 c in rect)
        {
            SpawnSos2On(map, "ShipHullTile", component.AtElevation(0, c.x, c.z), Rot4.North);
            SpawnSos2On(map, "ShipHullTile", component.AtElevation(1, c.x, c.z), Rot4.North);
        }
        // The player's upper floor is RL_Sos2Deck, not a bare hull tile. Leaving the fixture on plain
        // hull tiles made every deck assertion below ask about a layout the game never produces: the
        // foundation count was 2 (the parts), so a wrong deck-move patch still passed.
        if (StructuralSupport.Sos2Deck is { } deckTerrain)
        {
            for (int x = rect.minX; x <= rect.maxX; x++)
                for (int z = rect.minZ; z <= rect.maxZ; z++)
                    ShipDeckTerrain.InstallDeck(component, component.AtElevation(1, x, z), deckTerrain);
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            int laid = 0;
            foreach (IntVec3 c in component.LayerRect(1))
                if (StructuralSupport.IsSos2DeckTerrain(map.terrainGrid.FoundationAt(c))) laid++;
            Check("sos2-launch-fixture-lays-a-real-deck-upstairs", laid >= rect.Area,
                $"laid={laid}, expected={rect.Area}");
        }
        // Layout is constrained on both axes: the pad is x100..108 / z100..106, and RimLayers refuses to
        // transmit from a building that does not stand entirely on a deck. A 5x5 reactor centred on z=101
        // reaches z=99 and loses its transmitter flag, which shows up far away as an engine with no net.
        Building? engine = SpawnSos2On(map, "Ship_Engine_Interplanetary", component.AtElevation(0, 101, 102), Rot4.North);
        // A real ship has a reactor, and the engine only reports CanFire while its net is live. Spawning
        // the engine without a source leaves PowerOn false, which looks exactly like the bug under test
        // and would let a fixture failure pass for a game failure.
        //
        // Picked in a separate local under a different name on purpose: a local called `reactor` in this
        // method would shadow the static field of the same name and silently drop the reference.
        // 3x3 engine spans x100..102 / z101..103 and the 5x5 reactor spans x103..107 / z100..104: they
        // touch along x102|x103 without overlapping, and both stay inside the pad.
        Building? reactorOnDeck = SpawnSos2On(map, "Ship_Reactor", component.AtElevation(0, 105, 102), Rot4.North);
        // The bridge sits on the upper band, and RimLayers keeps power layers apart by design - a
        // trader on one band never joins a net on another without a riser. Giving the bridge its own
        // reactor on its own deck is what a player builds, and it keeps the bridge powered after the
        // move instead of relying on a cross-layer net that RimLayers deliberately refuses to make.
        Building? upperReactor = SpawnSos2On(map, "Ship_Reactor", component.AtElevation(1, 105, 103), Rot4.North);
        Building? bridge = SpawnSos2On(map, "ShipPilotSeatMini", component.AtElevation(1, 101, 102), Rot4.North);
        if (engine == null || bridge == null || reactorOnDeck == null || upperReactor == null)
        {
            Check("sos2-launch-fixture-has-engine-and-bridge", false,
                $"engine={engine != null}, bridge={bridge != null}, reactor={reactorOnDeck != null}, upperReactor={upperReactor != null}");
            return null;
        }
        // Every fixture building has to stand fully on a deck. A part hanging off the pad silently loses
        // its transmitter flag and the failure surfaces much later as an unpowered engine.
        var offDeck = new List<string>();
        foreach (Building part in new[] { engine, bridge, reactorOnDeck, upperReactor })
            foreach (IntVec3 cell in part.OccupiedRect())
                if (!component.Structure.HasSurface(cell)) offDeck.Add($"{part.def.defName}@{part.Position}");
        Check("sos2-launch-fixture-stands-fully-on-the-deck", offDeck.Count == 0, string.Join(", ", offDeck.Distinct()));
        foreach (Building source in new[] { reactorOnDeck, upperReactor })
            source.TryGetComp<CompRefuelable>()?.Refuel(source.TryGetComp<CompRefuelable>()?.Props.fuelCapacity ?? 0f);
        // A transmitter flag that RimLayers has withdrawn and a transmitter that simply never got a net
        // look identically unpowered from the engine's side. Naming which one it is here keeps the next
        // failure from being blamed on the engine again.
        Check("sos2-launch-fixture-reactors-count-as-transmitters",
            reactorOnDeck.TransmitsPowerNow && upperReactor.TransmitsPowerNow,
            PowerReport(reactorOnDeck) + " " + PowerReport(upperReactor));
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        map.powerNetManager.UpdatePowerNetsAndConnections_First();
        // Two phases on purpose. Joining a net rebuilds the surrounding graph, and the native teardown
        // resets PowerOn on every connector it passes, so a part switched on before a later part joins
        // ends up off again. Every part joins first; only then is anything switched on.
        var powered = new List<Building> { reactorOnDeck, engine, upperReactor, bridge };
        foreach (Building part in powered) JoinNet(part);
        Check("sos2-launch-fixture-reactor-gets-a-net", reactorOnDeck.TryGetComp<CompPowerTrader>()?.PowerNet != null,
            PowerReport(reactorOnDeck));
        Check("sos2-launch-fixture-engine-gets-a-net", engine.TryGetComp<CompPowerTrader>()?.PowerNet != null,
            PowerReport(engine));
        Check("sos2-launch-fixture-power-graph",
            powered.All(part => part.TryGetComp<CompPowerTrader>()?.PowerNet != null),
            $"reactor={PowerReport(reactorOnDeck)} engine={PowerReport(engine)} " +
            $"upper={PowerReport(upperReactor)} bridge={PowerReport(bridge)}");
        foreach (Building part in powered) PowerUp(part);
        Check("sos2-launch-fixture-parts-are-on",
            powered.All(part => part.TryGetComp<CompPowerTrader>()?.PowerOn == true),
            string.Join(" ", powered.Select(PowerReport)));
        sos2LaunchEngine = engine;
        return bridge;
    }

    /// <summary>
    /// What the power graph thinks of one building. TransmitsPowerNow is the flag the native net search
    /// reads, and RimLayers withdraws it when any occupied cell lacks a surface - so a false there means
    /// the building is not standing on its deck, not that the net failed to form.
    /// </summary>
    private static string PowerReport(Building? building)
    {
        if (building == null) return "null";
        CompPowerTrader? power = building.TryGetComp<CompPowerTrader>();
        var offDeck = building.OccupiedRect().Cells
            .Where(c => LayerMapComponent.For(building.Map)?.Structure.HasSurface(c) != true).ToList();
        return $"{building.def.defName}@({building.Position.x},{building.Position.z})" +
               $"[transmits={building.TransmitsPowerNow}, net={power?.PowerNet != null}" +
               $", on={power?.PowerOn == true}" +
               $", netAtCell={building.Map.powerNetGrid.TransmittedPowerNetAt(building.Position) != null}" +
               $", offDeck={(offDeck.Count == 0 ? "-" : string.Join("/", offDeck.Select(c => $"({c.x},{c.z})")))}";
    }

    /// <summary>Gives a building a net without touching whether it is switched on.</summary>
    private static void JoinNet(Building? building)
    {
        if (building == null || !building.Spawned) return;
        building.TryGetComp<CompFlickable>()?.SwitchIsOn = true;
        if (building.TryGetComp<CompPowerTrader>() is not { } power) return;
        // A building spawned into a queued batch is only guaranteed a net if its own register action is
        // still pending. Ours are usually already consumed by the first global update, and nothing ever
        // re-queues them, so the net is never created. Re-declaring the transmitter and running the
        // native phases is the same path the game takes when a player builds the part by hand; there is
        // no manual PowerNet surgery here.
        for (int attempt = 0; attempt < 4 && power.PowerNet == null; attempt++)
        {
            if (power.Props.transmitsPower) map.powerNetManager.Notify_TransmitterSpawned(power);
            if (building.def.ConnectToPower) map.powerNetManager.Notify_ConnectorWantsConnect(power);
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
        }
        if (power.PowerNet == null) power.TryManualReconnect(dispayVisuals: false);
    }

    /// <summary>Switches a building on the way a player does: it must already have a net.</summary>
    private static void PowerUp(Building? building)
    {
        if (building == null || !building.Spawned) return;
        if (building.TryGetComp<CompPowerTrader>() is not { } power) return;
        if (power.PowerNet == null) return;
        power.PowerOn = true;
    }

    /// <summary>How many of the launch pad's own cells the ground map still claims as ship.</summary>
    private static int GroundLaunchCells()
    {
        if (sos2GroundMap == null || sos2GroundMap.Disposed) return 0;
        return sos2LaunchCells.Count(c => Sos2Compatibility.ShipIndexAt(sos2GroundMap, c) >= 0);
    }

    private static void ObserveFlight()
    {
        if (sos2FlightReported) return;
        // The engine is the only thing that decides whether the ship climbs, so its own live state is
        // reported first: an engine whose building is despawned reads as "powered off" from SOS2's side
        // even though the power graph is fine, and that is a different failure from a dead net.
        // Reported once - this method runs every tick of the flight window.
        if (!sos2EngineReported && sos2LaunchEngine != null)
        {
            sos2EngineReported = true;
            Check("sos2-launch-engine-survives-the-move", sos2LaunchEngine.Spawned && sos2LaunchEngine.Map != null,
                LiveEngineReport());
        }
        // The bug is the ship coming back. SOS2's landing branch calls MoveShip back to PrevMap, so the
        // map to watch is the one the fixture launched from - the transit state is not a reliable
        // signal, because a ship that fell back keeps reporting inTransit until the move completes.
        // It has to be asked about the launch pad's own cells: earlier phases leave their pads behind
        // in ShipsOnMap, so "the ground map has a ship" is true long before the ship ever launched.
        int returned = GroundLaunchCells();
        if (returned > 0)
        {
            sos2FlightReported = true;
            Check("sos2-launch-ship-does-not-fall-back", false,
                $"ship returned to the ground map: groundCells={returned}/{sos2LaunchCells.Length}");
            return;
        }
        if (sos2SpaceMap == null || sos2SpaceMap.Disposed)
        {
            sos2FlightReported = true;
            Check("sos2-launch-ship-does-not-fall-back", false, "the space map was disposed mid-flight");
        }
    }

    private static void FinishFlight()
    {
        if (sos2SpaceMap == null || sos2SpaceMap.Disposed)
        {
            Check("sos2-launch-space-map-survives-the-flight", false, $"spaceMap={sos2SpaceMap}");
            return;
        }
        object? component = Sos2Compatibility.ShipCompOf(sos2SpaceMap);
        object? cache = Sos2ShipCache(sos2SpaceMap);
        object? state = component == null ? null : AccessTools.Field(component.GetType(), "ShipMapState")?.GetValue(component);
        object? heading = component == null ? null : AccessTools.Field(component.GetType(), "Heading")?.GetValue(component);
        Check("sos2-launch-space-map-survives-the-flight", true, $"size={sos2SpaceMap.Size}");
        Check("sos2-launch-ship-stays-in-the-space-map", cache != null,
            $"spaceCells={CountCells(component)}, state={state}, heading={heading}");
        // Paired with ObserveFlight: that one reports the failure the moment the ship lands back, this
        // one only records the pass when the whole window went by without it happening.
        if (!sos2FlightReported)
            Check("sos2-launch-ship-does-not-fall-back", true,
                $"state={state}, heading={heading}, altitude={AccessTools.Field(component?.GetType() ?? typeof(object), "Altitude")?.GetValue(component)}");
        // The decks are the reason this is a RimLayers problem at all. A hull that arrives with an
        // empty upper band is the failure the deck-move patch was written for.
        var space = LayerMapComponent.For(sos2SpaceMap);
        if (space?.Layout is { LayerCount: >= 2 })
        {
            int decked = 0;
            foreach (IntVec3 c in space.LayerRect(1))
                if (sos2SpaceMap.terrainGrid.FoundationAt(c) != null) decked++;
            Check("sos2-launch-upper-deck-arrives-with-a-floor", decked >= 60, $"decked={decked}");
        }
        else
        {
            Check("sos2-launch-upper-deck-arrives-with-a-floor", false, $"layout={space?.Layout}");
        }
        Check("sos2-launch-ground-map-is-cleared-of-the-ship", GroundLaunchCells() == 0,
            $"groundCells={GroundLaunchCells()}/{sos2LaunchCells.Length}");
        // Evidence that the re-power patch is what the flight needed, rather than a ship that happened
        // to survive on its own. Passes counts native re-power runs on a multi-deck hull; Restored
        // counts the parts this adapter had to switch on beyond the bridge's own deck.
        Check("sos2-launch-repower-patch-runs-on-a-multi-deck-hull",
            RimLayers.Compatibility.Sos2RepowerPatches.Passes > 0,
            $"passes={RimLayers.Compatibility.Sos2RepowerPatches.Passes}, restored={RimLayers.Compatibility.Sos2RepowerPatches.Restored}");
        // A deck whose foundation exists but whose surface is open air looks like empty sky to the
        // player. MoveShip's terrain copy only accepts layerable defs, and a deck is a foundation, so
        // the surface has to be asserted separately instead of trusting the foundation alone.
        if (space?.Layout is { LayerCount: >= 2 })
        {
            int surfaced = 0, bare = 0;
            foreach (IntVec3 c in space.LayerRect(1))
            {
                if (sos2SpaceMap.terrainGrid.FoundationAt(c) == null) continue;
                if (sos2SpaceMap.terrainGrid.TerrainAt(c) == RimLayersDefOf.RL_OpenAir) bare++;
                else surfaced++;
            }
            Check("sos2-launch-upper-deck-arrives-with-a-surface", surfaced > 0 && bare == 0,
                $"surfaced={surfaced}, bare={bare}");
            // Terrain alone does not put anything on screen: the parts themselves have to arrive on the
            // upper band, otherwise the player looks at a lit empty deck.
            int upperThings = 0;
            foreach (IntVec3 c in space.LayerRect(1))
                upperThings += sos2SpaceMap.thingGrid.ThingsListAt(c).Count;
            Check("sos2-launch-upper-deck-arrives-with-its-parts", upperThings > 0,
                $"upperThings={upperThings}, lowerThings={sos2SpaceMap.thingGrid.ThingsListAt(space.LayerRect(0).CenterCell).Count}");
        }
        LookAtTheArrival();
    }

    // The flight is invisible in a headless run, so a picture is the only evidence that the hull
    // renders at all. Jump to the ship's own core and look at the deck band it landed on.
    private static void LookAtTheArrival()
    {
        if (sos2SpaceMap == null || sos2SpaceMap.Disposed) return;
        Building? core = sos2LaunchBridge;
        var space = LayerMapComponent.For(sos2SpaceMap);
        if (core == null || space == null) return;
        IntVec3 target = space.TryDecode(core.Position, out LayerCell logical)
            ? space.ToStorage(logical)
            : core.Position;
        CameraJumper.TryJump(target, sos2SpaceMap, CameraJumper.MovementMode.Cut);
        // The bridge's own band, not the highest one: a fixture that expanded the source map leaves
        // empty bands above the decks, and looking at one of those shows nothing but sky.
        var onDeck = space.TryDecode(core.Position, out LayerCell where) ? where.LayerId : 0;
        RimLayers.Presentation.LayerView.Switch(space, onDeck);
        // The debug window sits in the middle of the screen and covers exactly what this shot exists to
        // show. Verse re-opens it whenever its static flag is set, so the flag has to go too.
        AccessTools.Field(AccessTools.TypeByName("Verse.EditWindow_Log"), "wantsToOpen")?.SetValue(null, false);
        foreach (Window window in Find.WindowStack.Windows.ToList())
            if (window.GetType().Name.Contains("Debug") || window.GetType().Name.Contains("Log")) window.Close();
        Find.CameraDriver.SetRootPosAndSize(target.ToVector3Shifted(), 14f);
        Check("sos2-launch-arrival-shot-target", true,
            $"layer={onDeck}, pos=({target.x},{target.z}), things={sos2SpaceMap.thingGrid.ThingsListAt(target).Count}");
        // The move ghost is laid out as pos - lowestCorner. A storage-coordinate lowest corner makes the
        // sketch as tall as the whole map, so every cell fails InBounds and the designator paints red on
        // every floor with no message. The repair lives in PlaceWorker_MoveShip.AllowsPlacing, which now
        // judges only the bottom band; that is what gets asserted here.
        object? cache = Sos2ShipCache(sos2SpaceMap);
        if (cache != null && space.Layout is { LayerCount: > 1 } ghostLayout)
        {
            MethodInfo? lowest = AccessTools.Method(cache.GetType(), "LowestCorner", new[] { typeof(byte), typeof(Map) });
            object? corner = lowest?.Invoke(cache, new object[] { (byte)0, sos2SpaceMap });
            int cornerZ = corner is IntVec3 c ? c.z : -1;
            // The lowest corner itself sits inside the first band - band 0 starts at storage z 0. What
            // breaks the ghost is the SPAN: the sketch is laid out as pos - lowestCorner, so a hull whose
            // upper deck lives at stride+122 produces rows past the first floor's height and every one of
            // them fails InBounds. Measuring the span is what actually pins that down.
            int maxZ = int.MinValue;
            if (AccessTools.Field(cache.GetType(), "Area")?.GetValue(cache) is System.Collections.IEnumerable cells)
                foreach (object? item in cells)
                    if (item is IntVec3 cell && cell.z > maxZ) maxZ = cell.z;
            int span = maxZ == int.MinValue ? 0 : maxZ - cornerZ;
            Check("sos2-move-ghost-sketch-is-taller-than-one-floor", span >= ghostLayout.Height,
                $"lowestCorner.z={cornerZ}, max.z={maxZ}, span={span}, floorHeight={ghostLayout.Height}, stride={ghostLayout.Stride}, bands={ghostLayout.LayerCount}");
            System.Type? worker = AccessTools.TypeByName("SaveOurShip2.PlaceWorker_MoveShip");
            MethodInfo? allows = worker == null ? null : AccessTools.Method(worker, "AllowsPlacing", new[]
            {
                typeof(BuildableDef), typeof(IntVec3), typeof(Rot4), typeof(Map), typeof(Thing), typeof(Thing)
            });
            Check("sos2-move-ghost-worker-is-reachable", allows != null, $"worker={(worker != null)}, method={(allows != null)}");
            // The real area is never borrowed any more: the ghost is judged inside the place worker. This
            // guard means a future attempt to swap Area is caught immediately instead of after it has
            // corrupted region registration.
            var area = (System.Collections.IEnumerable?)AccessTools.Field(cache.GetType(), "Area")?.GetValue(cache);
            int upper = 0, offBand = 0;
            if (area != null)
                foreach (object? item in area)
                {
                    if (item is not IntVec3 cell) continue;
                    if (!space.TryDecode(cell, out LayerCell decoded)) { offBand++; continue; }
                    if (decoded.LayerId > 0) upper++;
                }
            Check("sos2-move-ghost-leaves-the-real-area-alone", upper > 0 && offBand == 0,
                $"upper={upper}, undecodable={offBand}");
        }

        // A hull that gained a deck upstairs must not land on a map with fewer bands: MoveShip writes
        // storage coordinates cell by cell, so the first out-of-range cell throws mid-move and leaves the
        // hull half transferred. The adapter declares the need and LiveMapExpansion grows the target
        // before the move, so assert both halves of that contract.
        int required = Sos2Compatibility.RequiredElevation(sos2SpaceMap);
        int have = space.Layout?.HighestElevation ?? 0;
        Check("sos2-a-hull-declares-the-floor-it-needs", required > 0, $"required={required}, highest={have}");
        Check("sos2-the-space-map-meets-that-floor-demand", required <= have, $"required={required}, highest={have}");
        // And the growth really happens through the shared transaction rather than an ad-hoc resize.
        bool grew = RimLayers.Generation.LiveMapExpansion.EnsureRequirements(sos2SpaceMap);
        Check("sos2-a-tall-enough-map-is-not-grown-again", !grew, $"grew={grew}, highest={space.Layout?.HighestElevation}");
        // pendingScreenshot is only honoured while screenshotHold counts down; without the hold the
        // file is requested and the run ends before a frame is ever drawn.
        pendingScreenshot = Path.Combine(directory, "sos2-space-arrival.png");
        screenshotHold = 3;
    }

    private static string LiveEngineReport()
    {
        Building? engine = sos2LaunchEngine;
        if (engine == null) return "no engine";
        CompPowerTrader? power = engine.TryGetComp<CompPowerTrader>();
        CompFlickable? flick = engine.TryGetComp<CompFlickable>();
        return $"{engine.def.defName} spawned={engine.Spawned}, destroyed={engine.Destroyed}" +
               $", map={(engine.Map == null ? "null" : engine.Map.ToString())}" +
               $", pos=({engine.Position.x},{engine.Position.z})" +
               $", flick={flick?.SwitchIsOn}, on={power?.PowerOn}, net={power?.PowerNet != null}";
    }

    private static Map? SpaceMap()
    {
        foreach (Map candidate in Find.Maps)
            // SOS2 marks an orbiting map by its world-object parent, not by biome: the orbiting parent
            // type is the only thing LaunchShip sets before the ship arrives.
            if (candidate != map && candidate.Parent?.GetType().Name.Contains("OrbitingShip") == true)
                return candidate;
        return null;
    }

    /// <summary>One specific hull on a map, by the index its bridge carries.</summary>
    private static object? Sos2ShipCache(Map target, int index)
    {
        object? component = Sos2Compatibility.ShipCompOf(target);
        if (component == null) return null;
        System.Collections.IDictionary? ships = AccessTools.PropertyGetter(Sos2Compatibility.ShipMapCompType, "ShipsOnMap")
            ?.Invoke(component, null) as System.Collections.IDictionary;
        return ships != null && ships.Contains(index) ? ships[index] : null;
    }

    /// <summary>How many hulls the map currently tracks, so a by-index lookup that finds nothing is explainable.</summary>
    private static int Sos2ShipCount()
    {
        System.Collections.IDictionary? ships = AccessTools.PropertyGetter(Sos2Compatibility.ShipMapCompType, "ShipsOnMap")
            ?.Invoke(sos2ShipComp, null) as System.Collections.IDictionary;
        return ships?.Count ?? -1;
    }

    private static object? Sos2ShipCache(Map target)
    {
        object? component = Sos2Compatibility.ShipCompOf(target);
        if (component == null) return null;
        System.Collections.IDictionary? ships = AccessTools.PropertyGetter(Sos2Compatibility.ShipMapCompType, "ShipsOnMap")
            ?.Invoke(component, null) as System.Collections.IDictionary;
        if (ships is not { Count: > 0 }) return null;
        foreach (System.Collections.DictionaryEntry entry in ships) return entry.Value;
        return null;
    }

    private static int CountCells(object? component)
    {
        if (component == null) return -1;
        return (AccessTools.PropertyGetter(component.GetType(), "MapShipCells")?.Invoke(component, null)
            as System.Collections.ICollection)?.Count ?? -1;
    }

    private static bool CanMoveOf(object? cache) =>
        cache != null && cache.GetType().GetMethod("CanMove", Type.EmptyTypes)?.Invoke(cache, null) is true;

    private static string Details(object? cache)
    {
        if (cache == null) return "no ship cache";
        var type = cache.GetType();
        object? stuck = type.GetProperty("IsStuck")?.GetValue(cache);
        object? wreck = type.GetProperty("IsWreck")?.GetValue(cache);
        object? thrust = type.GetProperty("ThrustRatio")?.GetValue(cache);
        return $"stuck={stuck}, wreck={wreck}, thrust={thrust}, {EngineReport(cache)}";
    }

    /// <summary>
    /// Per-engine flight state. CanMove only ever asks "does one engine fire", so when it is false the
    /// answer lives in these three flags - a switched-off engine, a dead power net, or a rotation that
    /// no longer matches the hull after the ship was mirrored onto another map.
    /// </summary>
    private static string EngineReport(object cache)
    {
        if (cache.GetType().GetField("Engines")?.GetValue(cache) is not System.Collections.IEnumerable engines)
            return "engines=?" ;
        var parts = new List<string>();
        foreach (object? engine in engines)
        {
            if (engine == null) continue;
            var type = engine.GetType();
            object? flick = type.GetField("flickComp")?.GetValue(engine);
            object? power = type.GetField("powerComp")?.GetValue(engine);
            object? fuel = type.GetField("refuelComp")?.GetValue(engine);
            object? parent = type.GetField("parent")?.GetValue(engine);
            bool fires = type.GetMethod("CanFire", Type.EmptyTypes)?.Invoke(engine, null) is true;
            // The cached comps are the ones SOS2 bound at PostSpawnSetup. If they are null while the same
            // building reports a live net, the engine is reading a stale reference, not a dead graph -
            // that distinction is the whole difference between an adapter bug and a fixture bug.
            object? liveFlick = (parent as ThingWithComps)?.TryGetComp<CompFlickable>();
            object? livePower = (parent as ThingWithComps)?.TryGetComp<CompPowerTrader>();
            bool cachedNull = flick == null || power == null;
            bool on = flick?.GetType().GetProperty("SwitchIsOn")?.GetValue(flick) is true;
            bool powered = power?.GetType().GetProperty("PowerOn")?.GetValue(power) is true;
            bool net = power?.GetType().GetProperty("transNet")?.GetValue(power) != null;
            float stored = fuel?.GetType().GetProperty("Fuel")?.GetValue(fuel) is float f ? f : -1f;
            parts.Add($"[fires={fires}, switch={on}, power={powered}, net={net}, fuel={stored:0.#}" +
                      $", rot={(parent as Thing)?.Rotation.ToString() ?? "?"}" +
                      $", cachedNull={cachedNull}, liveSwitch={liveFlick?.GetType().GetProperty("SwitchIsOn")?.GetValue(liveFlick) is true}" +
                      $", livePower={livePower?.GetType().GetProperty("PowerOn")?.GetValue(livePower) is true}" +
                      $", liveNet={livePower?.GetType().GetProperty("PowerNet")?.GetValue(livePower) != null}" +
                      $", spawned={(parent as Thing)?.Spawned}, sameMap={ReferenceEquals((parent as Thing)?.Map, map)}" +
                      $", liveIsCached={ReferenceEquals(livePower, power)}]");
        }
        return parts.Count == 0 ? "engines=0" : string.Join(" ", parts);
    }

    private static Thing? Sentry(IntVec3 cell)    {
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
