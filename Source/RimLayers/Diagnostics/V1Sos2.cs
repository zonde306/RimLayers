using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
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
using UnityEngine;
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
    private static Building? sos2Shield;
    private static Building? sos2ShieldHeatSink;
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
        var building = (Building)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
        building.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(building, cell, target, rot);
        // CompShipHeat.PostSpawnSetup is supposed to add itself to cachedPipes, but a fixture spawns
        // parts without going through the ship-cache rooting that normally rebuilds that list, and a
        // part missing from it is invisible to the heat flood fill (its cell keeps grid = -1 and it
        // joins no net). Register every heat part here so the fixture cannot silently measure a
        // broken chain instead of the deck edge it is asking about.
        EnsureOnHeatPipeList(building);
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
            sos2Phase = 11;
            ticks = 0;
            return;
        }
        if (sos2Phase == 11)
        {
            Sos2RealShipStep();
            sos2Phase = 10;
            ticks = 0;
            return;
        }
        if (sos2Phase == 10)
        {
            Sos2LaunchStep();
            if (sos2Phase == 10) return;
            // The hull is back from its flight now. Flipping it here, rather than in the middle of the
            // scenario, means every later phase is not looking at a map the hull was just moved off.
            sos2Phase = 12;
            ticks = 0;
            return;
        }
        if (sos2Phase == 12)
        {
            // Combat runs before the flip on purpose. The flip is still broken (MoveShip throws a
            // NullReferenceException partway through), and it leaves the hull half moved, so anything
            // queued behind it never gets a clean ship to fire from.
            Sos2CombatStep();
            sos2Phase = 13;
            ticks = 0;
            return;
        }
        if (sos2Phase == 13)
        {
            // The whole flip runs with the ship's map on screen, because that is the state a player
            // clicks the button in, and because LayerView, the selector and the install designator all
            // resolve through Find.CurrentMap. Running the flip on a side map made every one of them
            // take a different path from the real one - and the assertions still went green.
            Map? before = Find.CurrentMap;
            if (realShipBridge != null && !realShipBridge.DestroyedOrNull() && before != realShipBridge.Map)
                Current.Game.CurrentMap = realShipBridge.Map;
            Sos2RealShipFlipStep();
            if (before != null && Current.Game.CurrentMap != before) Current.Game.CurrentMap = before;
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
        CheckShieldInterceptsEveryDeck();

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
            CheckEngineTrailPaintersAreScoped();
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
            // The pairing buttons come from a LayerPairProperties extension. LayerPairingBootstrap
            // derives it for power risers, pipe risers and resource risers; a heat riser carries SOS2's
            // CompShipHeat instead, so it has to declare it in XML. Assert the declaration actually
            // survived def loading - a typo in the Class attribute silently yields null, and the only
            // symptom would be a missing button nobody can see in a log.
            var paired = riser.GetModExtension<LayerPairProperties>();
            var offered = LayerPairing.Directions(riser).ToList();
            Check("sos2-thermal-riser-offers-pairing-buttons",
                paired != null && offered.Contains(1) && offered.Contains(-1),
                $"extension={paired != null}, directions={string.Join(",", offered)}");
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
        Check("sos2-foam-leaves-the-other-deck-queued",
            after.Contains(upperBreach), $"stillPending={after.Contains(upperBreach)}");

        // Foam covers the whole ship, the same way life support does: ShipMapComp.VecHasLS asks only
        // whether any CompShipLifeSupport on this hull is active, never which deck it sits on. A
        // distributor on the ground deck must therefore seal a breach three decks up on its next
        // tick, spending its own fuel to do it. Anything narrower would leave a top-deck hole open
        // forever on a hull whose only distributor is below.
        CompRefuelable? foamFuel = distributor.TryGetComp<CompRefuelable>();
        float fuelBefore = foamFuel?.Fuel ?? -1f;
        for (int i = 0; i < 4 && FoamCells(cache).Contains(upperBreach); i++)
            AccessTools.Method(cache!.GetType(), "SlowTick")?.Invoke(cache, null);
        HashSet<IntVec3> later = FoamCells(cache);
        float fuelAfter = foamFuel?.Fuel ?? -1f;
        Check("sos2-foam-covers-the-whole-ship", !later.Contains(upperBreach),
            $"upperPatched={!later.Contains(upperBreach)}, fuelBefore={fuelBefore}, " +
            $"fuelAfter={fuelAfter}, pending={later.Count}");
        Check("sos2-foam-spends-its-own-fuel-across-decks", fuelAfter < fuelBefore,
            $"fuelBefore={fuelBefore}, fuelAfter={fuelAfter}");
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

    private static int realShipIndex = -1;

    private static Building? realShipBridge;

    /// <summary>
    /// A real player hull, replayed from <c>Tests/Fixtures/realship.json</c>. Every fixture above is a
    /// pad a few cells wide, built from the parts that phase happened to need; none has an upper deck
    /// that is a solid hull with a hollow deck between, and none carries the riser and conduit counts
    /// that decide whether the layers share a net. This replays 890 parts across three decks of one
    /// hull extracted from a save, so later phases can ask their questions of a ship the game produced.
    /// </summary>
    /// <remarks>
    /// Parts whose ThingDef is missing are counted and skipped instead of failing: the fixture came out
    /// of a heavily modded save, and a hull that is 95% intact still answers what these phases ask. What
    /// is asserted is that the decks, the risers and the caches came through - not that every
    /// third-party def still exists on this machine.
    /// </remarks>
    private static void Sos2RealShipStep()
    {
        var component = LayerMapComponent.For(map);
        if (component?.Layout == null)
        {
            Check("sos2-realship-has-layers", false, "no LayerMapComponent");
            return;
        }
        string fixture = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(V1Integration).Assembly.Location)!,
            "..", "Tests", "Fixtures", "realship.json");
        if (!System.IO.File.Exists(fixture))
        {
            Check("sos2-realship-fixture-exists", false, fixture);
            return;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(System.IO.File.ReadAllText(fixture));
        }
        catch (Exception exception)
        {
            Check("sos2-realship-fixture-parses", false, exception.Message);
            return;
        }
        using var _ = document;
        JsonElement root = document.RootElement;
        int width = root.GetProperty("w").GetInt32();
        int length = root.GetProperty("h").GetInt32();
        JsonElement parts = root.GetProperty("parts");
        int needed = 0;
        foreach (JsonElement part in parts.EnumerateArray())
            needed = Math.Max(needed, part.GetProperty("b").GetInt32() + 1);
        // Far corner of the map, away from every pad an earlier phase built, so this hull cannot be
        // mistaken for one of them by any lookup that goes through the ship's cells.
        int originX = component.Layout.Width - width - 12;
        int originZ = component.Layout.Height - length - 12;
        Check("sos2-realship-three-decks-on-the-map", component.Layout.LayerCount >= needed,
            $"layers={component.Layout.LayerCount}, needed={needed}, at=({originX},{originZ}) {width}x{length}");
        if (component.Layout.LayerCount < needed) return;

        foreach (JsonElement part in parts.EnumerateArray())
        {
            int layer = part.GetProperty("b").GetInt32();
            IntVec3 cell = component.AtElevation(layer, originX + part.GetProperty("x").GetInt32(),
                originZ + part.GetProperty("z").GetInt32());
            foreach (Thing t in cell.GetThingList(map).ToList())
                if (t is not Pawn)
                {
                    if (t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                    else t.DeSpawn();
                }
        }

        int spawned = 0, missing = 0;
        var missingDefs = new HashSet<string>();
        var perLayer = new int[needed];
        Building? realBridge = null;
        foreach (JsonElement part in parts.EnumerateArray())
        {
            string? defName = part.GetProperty("d").GetString();
            if (defName == null) continue;
            ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                missing++;
                missingDefs.Add(defName);
                continue;
            }
            // The save also holds pawns, plants and animals that happened to stand on the hull. They
            // are not ship parts and this step is not about them, so only a missing def is reported.
            if (def.thingClass == null || !typeof(Building).IsAssignableFrom(def.thingClass)) continue;
            int layer = part.GetProperty("b").GetInt32();
            IntVec3 cell = component.AtElevation(layer, originX + part.GetProperty("x").GetInt32(),
                originZ + part.GetProperty("z").GetInt32());
            string? rotRaw = part.GetProperty("r").GetString();
            Rot4 rot = rotRaw != null && int.TryParse(rotRaw, out int raw) ? new Rot4(raw) : Rot4.North;
            Building? building = SpawnSos2On(map, defName, cell, rot);
            if (building == null)
            {
                missing++;
                missingDefs.Add(defName);
                continue;
            }
            spawned++;
            perLayer[layer]++;
            if (defName is "ShipPilotSeatMini" or "ShipBridge" or "ShipBridgeSmall") realBridge ??= building;
        }
        Check("sos2-realship-parts-spawn", spawned > 700,
            $"spawned={spawned}, missing={missing}, missingDefs={string.Join(",", missingDefs.Take(6))}");
        Check("sos2-realship-every-deck-is-populated", perLayer.All(n => n > 50),
            string.Join(",", perLayer.Select((n, i) => $"L{i}={n}")));
        Check("sos2-realship-has-a-bridge", realBridge != null, $"hull spawned={spawned}");
        if (realBridge == null) return;

        RecacheSos2();
        int index = Sos2Compatibility.ShipIndexAt(map, realBridge.Position);
        object? cache = index >= 0 ? Sos2ShipCache(map, index) : null;
        Check("sos2-realship-becomes-one-ship", cache != null,
            $"index={index}, bridge={realBridge.Position}, ships={Sos2ShipCount()}");
        if (cache == null) return;
        realShipIndex = index;
        // Remembered so the flip pass, which runs after the flight has moved the hull, can find the
        // same ship again. The bridge itself travels with the hull and its reference survives.
        realShipBridge = realBridge;

        // The deck counts are the point: a hull whose upper decks never joined the cache answers every
        // later question about a single-layer ship and still looks fine.
        System.Collections.IDictionary? cells = Sos2Compatibility.ShipCellsOf(sos2ShipComp);
        var decks = new SortedSet<int>();
        int mine = 0;
        if (cells != null)
            foreach (IntVec3 key in cells.Keys)
            {
                if (Sos2Compatibility.ShipIndexAt(map, key) != index) continue;
                mine++;
                if (component.TryDecode(key, out LayerCell logical)) decks.Add(logical.LayerId);
            }
        Check("sos2-realship-keeps-all-three-decks", decks.Count >= needed,
            $"decks={string.Join(",", decks)}, cells={mine}, tracked={cells?.Count}");
        Check("sos2-realship-caches-its-engines", CacheCount(cache, "Engines") > 0,
            $"engines={CacheCount(cache, "Engines")}, index={index}");
        // Heat sinks are not a cache list; SOS2 keeps them as CompShipHeat on the parts themselves, so
        // they are counted the way a player would see them: buildings carrying a comp with a capacity.
        // RimLayers does not reference the SOS2 assembly at compile time, so this goes through a
        // reflection lookup like every other SOS2 type in this file.
        int heatSinks = 0;
        float heatStorage = 0f;
        Type? heatType = AccessTools.TypeByName("SaveOurShip2.CompShipHeat");
        if (heatType != null)
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is not Building building || Sos2Compatibility.ShipIndexAt(map, building.Position) != index) continue;
                ThingComp? heat = building.AllComps.FirstOrDefault(c => heatType.IsInstanceOfType(c));
                object? props = heat?.props;
                if (props == null) continue;
                // heatCapacity is a float; casting it to int silently yielded 0 and made this check
                // report "no sinks" on a hull that has six of them.
                float capacity = AccessTools.Field(props.GetType(), "heatCapacity")?.GetValue(props) switch
                {
                    float f => f,
                    int i => i,
                    _ => 0f
                };
                if (capacity <= 0f) continue;
                heatSinks++;
                heatStorage += capacity;
            }
        Check("sos2-realship-caches-its-heat-sinks", heatSinks >= 3, $"sinks={heatSinks}, storage={heatStorage}");

        // A deck with nothing but hull above and below it - the hollow middle band of this hull, which
        // carries 0 hull tiles of its own - has no surface for a building to stand on, so every part
        // there used to be cut out of the power graph. The visible symptom was a rung of three risers
        // A deck with nothing but hull above and below it - the hollow middle band of this hull, which
        // carries 0 hull tiles of its own - has no surface for a building to stand on, so parts there
        // used to be cut out of the power graph. This counts how many risers actually stand on such a
        // deck, so a later check can tell whether that theory even applies here: on the saved hull every
        // riser turned out to stand on a floor (onBareDeck=0), which retired the theory rather than
        // confirming it. Kept as a reading, not as a claim.
        var riserLayers = new SortedSet<int>();
        int risersOnBareDeck = 0;
        int risers = 0;
        foreach (Thing thing in map.listerThings.AllThings)
            if (thing.def.defName == "RL_PowerRiser")
                foreach (IntVec3 cell in thing.OccupiedRect())
                    if (Sos2Compatibility.ShipIndexAt(map, cell) == index)
                    {
                        risers++;
                        if (!component.TryDecode(cell, out LayerCell here)) break;
                        riserLayers.Add(here.LayerId);
                        if (!component.Structure.HasSurface(cell)) risersOnBareDeck++;
                        break;
                    }
        int heatRisers = 0;
        foreach (Thing thing in map.listerThings.AllThings)
            if (thing.def.defName == "RL_HeatRiser")
                foreach (IntVec3 cell in thing.OccupiedRect())
                    if (Sos2Compatibility.ShipIndexAt(map, cell) == index)
                    {
                        heatRisers++;
                        break;
                    }
        Check("sos2-realship-carries-its-risers", risers + heatRisers >= 6,
            $"power={risers}, heat={heatRisers}, index={index}, onBareDeck={risersOnBareDeck}");
        Check("sos2-realship-risers-span-every-deck", riserLayers.Count >= needed,
            $"decks={string.Join(",", riserLayers)}, need={needed}, powerRisers={risers}");
        // The decks are joined by vertical power edges, and VerticalNeighbors only yields a peer when
        // both risers sit on the same column, one deck apart, and both transmit. Counting the peers per
        // riser says which of those three is false instead of guessing at the rule - the earlier
        // version of this step asserted a theory about bare decks and every riser turned out to stand
        // on a floor, so the branch under test was never entered.
        int verticalEdges = 0;
        var riserReport = new List<string>();
        foreach (Thing thing in map.listerThings.AllThings)
        {
            if (thing.def.defName != "RL_PowerRiser" || thing is not Building riser) continue;
            if (Sos2Compatibility.ShipIndexAt(map, riser.Position) != index) continue;
            int peers = component.Power.VerticalNeighbors(riser).Count();
            verticalEdges += peers;
            string layerName = component.TryDecode(riser.Position, out LayerCell at) ? at.LayerId.ToString() : "?";
            riserReport.Add($"L{layerName}@({at.X},{at.Z}) transmits={riser.TransmitsPowerNow} peers={peers}");
        }
        Check("sos2-realship-risers-join-the-decks", verticalEdges > 0, string.Join(" | ", riserReport));

        // Rotating or flipping a hull is a sketch of the whole ship, so the corner SOS2 measures it
        // from has to be inside the map. On a layered map SOS2 divides by Map.Size.z - the storage
        // height with every band stacked - and lands one band per deck outside; MoveShip then asks for
        // a negative z and throws mid-move, leaving the ship half transferred. This calls the patched
        // method the way the bridge does and checks the corner is where a player could actually place it.
        // A layered map is exactly the case this is for, so the guard must not be "square map":
        // Map.Size.z is the storage height with every band stacked, and it is never equal to Size.x
        // here. The earlier version of this check was skipped on every run and still reported PASS.
        MethodInfo? lowest = AccessTools.Method(cache.GetType(), "LowestCorner",
            new[] { typeof(byte), typeof(Map) });
        if (lowest == null || component.Layout is not { } layout)
        {
            Check("sos2-realship-flipped-corner-stays-on-the-map", false,
                $"LowestCorner resolved={lowest != null}, layout={component.Layout != null}");
            return;
        }
        foreach (byte rot in new byte[] { 1, 2 })
        {
            var corner = (IntVec3)lowest.Invoke(cache, new object[] { rot, map })!;
            // Both axes are measured against the logical side of a deck, so both have to land inside
            // one band. A storage-height corner lands a band or more away and MoveShip asks for a
            // cell that does not exist.
            bool inside = corner.x >= 0 && corner.x < layout.Width
                && corner.z >= 0 && corner.z < layout.Height;
            Check($"sos2-realship-corner-rot{rot}-stays-on-a-deck", inside,
                $"corner={corner}, logical={layout.Width}x{layout.Height}, storageZ={map.Size.z}");
        }

        // The corner and the flip move are exercised once the flight phase is done. MoveShip transfers
        // the hull out of this map, so running it in the middle of the scenario would leave every later
        // phase looking at an empty map (that is exactly what "no valid ship index found" was).
        ReportHeatNetTopology(map, index, component);
    }

    /// <summary>
    /// Writes down how a real hull's heat nets are actually wired, deck by deck.
    /// </summary>
    /// <remarks>
    /// <c>Building_ShipTurret.ConnectedToBridge</c> asks only whether the turret's own net holds a
    /// <c>PilCons</c>, <c>AICores</c> or <c>TacCons</c>. It never looks at elevation, so a turret can
    /// fire from any deck - but only if the flood fill actually reaches a console. On a layered map
    /// that depends on <c>Sos2HeatPatches.SameShip</c>, which lets a vertical edge through only where
    /// BOTH stacked cells hold heat hardware.
    /// <para>
    /// Whether a given ship satisfies that is a property of how its owner laid the conduits, not of
    /// the adapter, so this reports rather than asserts: which nets exist, how big they are, whether
    /// each one carries a console, and how many decks each one spans. A hull whose consoles sit on a
    /// net of their own is a hull whose turrets never fire, and that is worth knowing before deciding
    /// whether the rule or the ship needs to change.
    /// </para>
    /// </remarks>
    private static void ReportHeatNetTopology(Map target, int index, LayerMapComponent component)
    {
        List<object>? nets = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedNets")
            ?.GetValue(sos2ShipComp) as List<object>;
        System.Collections.IList? raw = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedNets")
            ?.GetValue(sos2ShipComp) as System.Collections.IList;
        int[]? grid = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "grid")
            ?.GetValue(sos2ShipComp) as int[];
        if (raw == null || grid == null)
        {
            Check("sos2-realship-heat-topology-is-readable", false, "cachedNets or grid unavailable");
            return;
        }

        var connectors = new Dictionary<int, int>();
        var decks = new Dictionary<int, SortedSet<int>>();
        var cells = new Dictionary<int, int>();
        Type? heatType = Sos2Compatibility.ShipHeatType;
        // Walk the nets themselves rather than the heat grid. The grid is only filled by
        // MapComponentUpdate, which this phase has not necessarily run for this hull, so reading it
        // answers -1 for every part and reports an empty topology on a ship that has five nets.
        // ShipHeatNet.Connectors is the list the flood fill actually built, so it is the ground truth.
        for (int net = 0; net < raw.Count; net++)
        {
            object? entry = raw[net];
            if (entry == null) continue;
            if (AccessTools.Field(entry.GetType(), "Connectors")?.GetValue(entry) is not System.Collections.IEnumerable parts)
                continue;
            var seen = new SortedSet<int>();
            int count = 0, consoles = 0;
            foreach (object? part in parts)
            {
                count++;
                if (part is ThingComp comp && comp.parent != null)
                {
                    if (component.TryDecode(comp.parent.Position, out LayerCell logical)) seen.Add(logical.LayerId);
                    // A console lives on the net itself, so the net-level counters below are the
                    // authority; this per-part pass only measures which decks the net reaches.
                }
            }
            consoles = CacheCount(entry, "PilCons") + CacheCount(entry, "AICores") + CacheCount(entry, "TacCons");
            cells[net] = count;
            decks[net] = seen;
            connectors[net] = consoles;
        }
        if (cells.Count == 0)
        {
            // Fall back to the heat grid for builds where Connectors is not where it was expected.
            foreach (Thing thing in target.listerThings.AllThings)
            {
                if (thing is not Building building || Sos2Compatibility.ShipIndexAt(target, building.Position) != index) continue;
                int at = target.cellIndices.CellToIndex(building.Position);
                if (at < 0 || at >= grid.Length || grid[at] < 0) continue;
                int net = grid[at];
                cells[net] = cells.GetValueOrDefault(net) + 1;
                if (!decks.TryGetValue(net, out SortedSet<int>? seen2))
                    decks[net] = seen2 = new SortedSet<int>();
                if (component.TryDecode(building.Position, out LayerCell logical2)) seen2.Add(logical2.LayerId);
            }
        }

        var lines = new List<string>();
        int netsWithConsole = 0, netsWithout = 0, decksSpanned = 0;
        foreach (int net in cells.Keys.OrderBy(k => k))
        {
            int consoles = connectors.GetValueOrDefault(net);
            int span = decks.TryGetValue(net, out SortedSet<int>? d) ? d.Count : 0;
            if (consoles > 0) netsWithConsole++;
            else netsWithout++;
            decksSpanned = Math.Max(decksSpanned, span);
            lines.Add($"net{net}: parts={cells[net]}, consoles={consoles}, decks={span}");
        }
        // The property that decides whether turrets work: every net that holds a turret has to hold a
        // console too. Reporting the tally rather than asserting a number keeps this a measurement of
        // the save, which is what it is for.
        Check("sos2-realship-heat-topology-is-readable", lines.Count > 0,
            $"nets={raw.Count}, " + string.Join("; ", lines.Take(8)) +
            $" | withConsole={netsWithConsole}, withoutConsole={netsWithout}, widestSpan={decksSpanned}");
        // The deck a ship cannot cross is the one that costs the player a turret. A net that holds a
        // console on the bridge deck and reaches another deck is the whole adapter working end to end:
        // the flood fill found the vertical edge, kept the two decks on one net, and that net already
        // carried the console that makes a turret live. Asserting it on the real hull rather than on a
        // pad matters, because a pad has no ship cache part and its conduits are laid by the fixture,
        // not by a player.
        var bridged = new List<string>();
        for (int net = 0; net < raw.Count; net++)
        {
            if (!connectors.TryGetValue(net, out int consoles) || consoles <= 0) continue;
            if (decks.TryGetValue(net, out SortedSet<int>? on) && on.Count > 1)
                bridged.Add($"net{net}:[{string.Join(",", on)}]x{cells[net]}");
        }
        Check("sos2-realship-a-console-net-reaches-another-deck", bridged.Count > 0,
            $"bridged=[{string.Join(" ", bridged)}], nets={raw.Count}, widestSpan={decksSpanned}");
    }

    /// <summary>
    /// Flips the real hull with the same call the bridge makes, after every other phase has finished.
    /// </summary>
    private static string? sos2FirstChanceFrame;

    private static int CountUnclaimedAreaCells(Map target, int index)
    {
        IDictionary? cells = Sos2Compatibility.ShipCellsOf(target);
        IDictionary? ships = Sos2Compatibility.ShipsOnMapOf(target);
        if (cells == null || ships == null) return 0;
        object? ship = Sos2Compatibility.ShipOf(ships, index);
        if (ship == null) return 0;
        // HashSet<T> implements ICollection<T>, not the non-generic ICollection: an `is ICollection`
        // test is false for it and the count silently reads as zero.
        if (AccessTools.Field(ship.GetType(), "Area")?.GetValue(ship) is not IEnumerable<IntVec3> area) return 0;
        int missing = 0;
        foreach (object? cell in area)
            if (cell is IntVec3 v && !cells.Contains(v)) missing++;
        return missing;
    }

    private static void Sos2FirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (sos2FirstChanceFrame != null || e.Exception is not { } fault || fault.GetType().Name != "NullReferenceException") return;
        sos2FirstChanceFrame = string.Join("\n", (fault.StackTrace ?? "").Split('\n').Take(12));
    }

    /// <summary>
    /// A turret covers the whole ship, so the deck it stands on must not matter.
    /// </summary>
    /// <remarks>
    /// <c>Building_ShipTurret.ConnectedToBridge</c> asks one question: does the turret's heat net hold
    /// any <c>PIICons</c>, <c>AICores</c> or <c>TacCons</c>. It never looks at elevation. That mirrors
    /// <c>ShipMapComp.VecHasLS</c>, where any active life support covers every deck of the hull. So a
    /// turret three decks up from the only bridge has to fire, and the adapter's vertical heat
    /// adjacency is what makes that true - without it the upper deck is its own net with no console
    /// on it and the turret is permanently dead, which reads as "multi-deck ships cannot fight".
    /// </remarks>
    private static void Sos2CombatStep()
    {
        Map target = sos2Bridge?.Map ?? map;
        LayerMapComponent? hull = LayerMapComponent.For(target);
        if (sos2Bridge == null || hull?.Layout is not { LayerCount: > 1 } layout)
        {
            Check("sos2-combat-needs-a-hull", false, $"bridge={(sos2Bridge == null ? "none" : "ok")}");
            return;
        }

        // The deck above the bridge, which is the highest one this fixture actually plates. Asking for
        // HighestElevation instead lands on an empty band four decks up with no hull under it, and a
        // turret there has no vertical heat edge to follow - which reads as an adapter bug but is just
        // a turret standing in the air.
        int deck = 1;
        if (!hull.TryDecode(hull.AtElevation(deck, sos2DeckRect.minX, sos2DeckRect.minZ), out _))
        {
            Check("sos2-a-turret-can-stand-on-an-upper-deck", false, $"deck={deck}, layout={layout}");
            return;
        }
        IntVec3 turretCell = hull.AtElevation(deck, sos2DeckRect.minX + 2, sos2DeckRect.minZ + 14);
        foreach (IntVec3 c in GenAdj.OccupiedRect(turretCell, Rot4.North, new IntVec2(2, 2)))
            foreach (Thing t in c.GetThingList(target).ToList())
                if (t is not Pawn)
                {
                    if (t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                    else t.DeSpawn();
                }

        // A vertical heat edge only exists where both stacked cells hold heat hardware, so a turret
        // with nothing but hull plating beneath it is on its own net. That is the rule, and it is the
        // rule on a real multi-deck save too: the player's own ship carries ShipHeatConduit on every
        // deck (48 / 47 / 46 tiles across its three bands). Build the same shape here - conduit under
        // the turret and a run back to the bridge - instead of expecting the hull alone to carry heat.
        //
        // OccupiedRect returns STORAGE cells whose z already includes the band offset, so the logical
        // x/z have to come back out of TryDecode before AtElevation is asked for the deck below:
        // LayerMapComponent.LayerRect z is storage, and LayerLayout.ToStorage adds stride again. That
        // double offset threw ArgumentOutOfRangeException out of AtElevation the first time.
        // Conduit goes UNDER the turret only. Both are buildings, and a building cannot share a cell with
        // another building: GenSpawn wipes whatever stands there, so a conduit placed at the turret's own
        // cells is destroyed the moment the turret lands on it. That leaves the riser with a single end
        // and the deck above on a net of its own - which is precisely the failure being tested. The
        // turret is itself a heat part, so turret (deck 1) over conduit (deck 0) is the stacked pair the
        // vertical rule wants.
        var riserPath = new List<IntVec3>();
        foreach (IntVec3 c in GenAdj.OccupiedRect(turretCell, Rot4.North, new IntVec2(2, 2)))
        {
            if (!hull.TryDecode(c, out LayerCell logical)) continue;
            IntVec3 below = hull.AtElevation(0, logical.X, logical.Z);
            foreach (Thing t in below.GetThingList(target).ToList())
                if (t is not Pawn && t.def.defName == "ShipHeatConduit") t.Destroy(DestroyMode.Vanish);
            SpawnSos2On(target, "ShipHeatConduit", below, Rot4.North);
            riserPath.Add(below);
        }
        // Walk the ground deck from the riser to the BRIDGE, not to the reactor. The point of the
        // fixture is whether a turret two decks up can reach a console, and a run that stops at the
        // reactor would only prove the turret shares a net with a heat source. Bridge first: a turret
        // whose net holds a reactor but no console is still dead (ConnectedToBridge asks for
        // PilCons/AICores/TacCons, never for heat).
        //
        // Breadth first, and around the support columns BuildSos2Pad drops every 4 cells. A greedy
        // step-then-step walk hits the first column and stops, which leaves the riser on its own net
        // and reads exactly like an adapter that cannot cross decks.
        int laid = LayHeatRun(target, hull, riserPath, sos2Bridge.Position);

        Building? turret = SpawnSos2On(target, "ShipTurret_ACI", turretCell, Rot4.North);
        Check("sos2-a-turret-can-stand-on-an-upper-deck",
            turret != null && hull.TryDecode(turret.Position, out LayerCell at) && at.LayerId == deck,
            $"turret={(turret == null ? "none" : turret.Position.ToString())}, deck={deck}, layout={layout.Width}x{layout.Height}");
        if (turret == null) return;

        Type? heatType = Sos2Compatibility.ShipHeatType;
        ThingComp? heat = heatType == null ? null : (turret as ThingWithComps)?.AllComps
            .FirstOrDefault(c => heatType.IsInstanceOfType(c));
        // CompShipHeat.PostSpawnSetup is what puts a part on cachedPipes, and the flood fill walks
        // that list - so a part spawned after the list was last rebuilt is invisible to it. The real
        // path does not hit this because SOS2 recaches from roots when the ship is loaded or a part is
        // placed through the build system; a fixture that calls GenSpawn directly has to do it here.
        // Without this the turret reads "no net" for a reason that has nothing to do with decks.
        EnsureOnHeatPipeList(turret);

        // Rebuilding the heat grid is what assigns nets, so do it after the turret exists.
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "heatGridDirty")?.SetValue(sos2ShipComp, true);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "loaded")?.SetValue(sos2ShipComp, false);
        AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "MapComponentUpdate", Type.EmptyTypes)
            ?.Invoke(sos2ShipComp, null);

        // "not on a net" has three very different causes and the detail has to say which one it is:
        // the comp never reached cachedPipes, the flood fill could not reach it, or it reached a net
        // that simply has no console. Reading myNet alone cannot tell them apart, and the first of
        // those is a fixture bug while the third would be a real adapter gap.
        object? pipes = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedPipes")
            ?.GetValue(sos2ShipComp);
        int pipeCount = Count(pipes);
        int turretInPipes = 0;
        if (pipes is System.Collections.IEnumerable pipeList)
            foreach (object? p in pipeList)
                if (ReferenceEquals(p, heat)) turretInPipes++;
        int[]? heatGrid = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "grid")
            ?.GetValue(sos2ShipComp) as int[];
        int turretGrid = heatGrid == null ? -2 : heatGrid[target.cellIndices.CellToIndex(turret.Position)];
        int bridgeGrid = heatGrid == null || sos2Bridge == null
            ? -2
            : heatGrid[target.cellIndices.CellToIndex(sos2Bridge.Position)];
        object? net = heat == null ? null : AccessTools.Field(heatType, "myNet")?.GetValue(heat);
        // A vertical neighbour is only accepted when both stacked cells carry heat hardware AND they
        // belong to the same ship. Naming which half failed is the difference between "the fixture never
        // put conduit under the turret" and "the adapter cannot cross this deck".
        var stack = new List<string>();
        if (hull.TryDecode(turret.Position, out LayerCell turretLogical))
            foreach (int lid in new[] { turretLogical.LayerId, 0 })
            {
                IntVec3 stacked = hull.AtElevation(lid, turretLogical.X, turretLogical.Z);
                bool heatHere = false;
                foreach (Thing t in stacked.GetThingList(target))
                    if (Sos2Compatibility.IsHeatPart(t)) { heatHere = true; break; }
                stack.Add($"L{lid}{stacked}:ship={Sos2Compatibility.ShipIndexAt(target, stacked)},heat={heatHere},grid={(heatGrid == null ? -2 : heatGrid[target.cellIndices.CellToIndex(stacked)])}");
            }
        // Two nets that both carry heat hardware and are stacked on each other must be one net. The
        // adapter is the only thing that can put the pair in front of the flood fill, so ask it
        // directly: this separates "the vertical neighbour was never offered" from "SOS2 refused it".
        var offered = new List<string>();
        Sos2CellGraph.Enter(target, null);
        foreach (IntVec3 stacked in Sos2CellGraph.Cardinal(turret))
            if (hull.TryDecode(stacked, out LayerCell stackedLogical) && stackedLogical.LayerId != deck)
                offered.Add($"{stacked}(L{stackedLogical.LayerId})");
        Sos2CellGraph.Leave();
        // The turret is a 2x2, so half its footprint overhangs the riser. The flood fill stamps every
        // occupied cell before it walks, and a neighbour that had already been stamped is skipped - so
        // the pair that matters is the riser's own column under one of the turret's cells, not any
        // stacked neighbour the turret happens to have.
        var riserColumns = new List<string>();
        foreach (IntVec3 riser in riserPath)
        {
            if (!hull.TryDecode(riser, out LayerCell riserLogical)) continue;
            IntVec3 over = hull.AtElevation(deck, riserLogical.X, riserLogical.Z);
            Thing? standing = over.GetFirstThing<Building>(target);
            bool stamped = heatGrid != null && heatGrid[target.cellIndices.CellToIndex(over)] != -1;
            bool carries = false;
            foreach (Thing t in over.GetThingList(target))
                if (Sos2Compatibility.IsHeatPart(t)) { carries = true; break; }
            riserColumns.Add($"{riser}->{over}:thing={standing?.def.defName ?? "none"},heat={carries},grid={(heatGrid == null ? -2 : heatGrid[target.cellIndices.CellToIndex(over)])},stamped={stamped}");
        }
        Check("sos2-the-flood-fill-is-offered-the-deck-below", offered.Count > 0,
            $"turret={(heat == null ? "none" : "ok")}, offered=[{string.Join(" ", offered)}], "
            + $"risers={riserPath.Count}, riserColumns=[{string.Join(" | ", riserColumns)}]");
        Check("sos2-an-upper-deck-turret-is-on-a-heat-net", heat != null && net != null,
            $"turret={turret.Position}, net={(net == null ? "none" : "ok")}, heatComp={heat != null}, " +
            $"pipes={pipeCount}, turretInPipes={turretInPipes}, runLaid={laid}, risers={riserPath.Count}, " +
            $"bridge={(sos2Bridge == null ? "none" : sos2Bridge.Position.ToString())}, map={target}, " +
            $"turretGrid={turretGrid}, bridgeGrid={bridgeGrid}, bridgeShip={Sos2Compatibility.ShipIndexAt(target, sos2Bridge.Position)}, " +
            $"stack=[{string.Join(" ", stack)}]");
        if (heat == null || net == null) return;

        // Run the shield assertion on the real hull, not on this pad. A pad is terrain plus a few
        // columns: nothing on it carries CompShipCachePart, so none of its cells join MapShipCells and
        // ShipMapComp's slow tick destroys anything standing there. A real hull registers its own
        // cells, which is the situation the player reports come from.
        if (realShipBridge != null && !realShipBridge.DestroyedOrNull() && realShipBridge.Map != null)
        {
            Map hullMap = realShipBridge.Map;
            LayerMapComponent? realHull = LayerMapComponent.For(hullMap);
            if (realHull?.Layout is { LayerCount: > 1 }) CheckShieldBlocksIncomingFire(hullMap, realHull);
            else Check("sos2-a-shield-can-stand-on-the-hull", false, "the real hull has no layers");
        }
        else
        {
            CheckShieldBlocksIncomingFire(target, hull);
        }
    }

    /// <summary>
    /// Whether a shield actually stops fire aimed at the hull, not whether a patch was installed.
    /// </summary>
    /// <remarks>
    /// The earlier assertion only read the patched IL, which answers "did the transpiler run" and not
    /// "does a shot get stopped". Those come apart: the report that prompted this was a shield that
    /// neither turret fire nor torpedoes could be stopped by, and the IL check was green.
    ///
    /// The projectile is put where SOS2 puts its own incoming fire - a band-local edge cell of the
    /// hull's deck - and the intercept test is called for the two points the projectile would travel
    /// between on that tick. <c>Projectile.ExactPosition</c> zeroes y and adds the thing's Altitude, so
    /// both points carry <c>def.Altitude</c> and not a band; that is why the test is about the two
    /// points a real shot has, not about which deck the shield stands on.
    /// </remarks>
    private static void CheckShieldBlocksIncomingFire(Map target, LayerMapComponent hull)
    {
        if (sos2Shield != null) sos2Shield.Destroy(DestroyMode.Vanish);
        IntVec3 shieldCell = ShieldCell(hull);
        sos2Shield = shieldCell.IsValid
            ? SpawnSos2On(target, "ShipCombatShieldGenerator", shieldCell, Rot4.North)
            : null;
        if (sos2Shield == null)
        {
            Check("sos2-a-shield-can-stand-on-the-hull", false,
                $"no 3x3 surface on the hull; cell={shieldCell}");
            return;
        }
        Check("sos2-a-shield-can-stand-on-the-hull", true, $"shield={sos2Shield.Position}");

        // A shield only reports hits while it is switched on and has a parent to measure from.
        if (sos2Shield.TryGetComp<CompFlickable>() is { } flick) flick.SwitchIsOn = true;
        // shutDown/radius/radiusSet live on CompShipHeatShield, not on the CompShipHeat base, so they
        // have to be read through the shield's own type. Reading them off the base type returns null
        // and every gate then looks false for a reason that has nothing to do with the shield.
        // A grid rebuild is not the only thing that destroys parts: ShipMapComp's own slow tick runs
        // KillAllOffShip, which destroys every thing standing on a cell that is not in MapShipCells.
        // A fixture that spawns onto a map with no registered ship therefore loses its shield to the
        // clean-up, not to anything about shields.
        object? heat = (sos2Shield as ThingWithComps)?.AllComps
            .FirstOrDefault(c => c.GetType().Name == "CompShipHeatShield");
        // CompTick recomputes shutDown every tick from breakdown, PowerOn and venting, so a fixture
        // that never ticks leaves whatever the initialiser said. The postfix skips shut-down shields,
        // so set it directly instead of waiting for a tick that a generate scenario may not reach.
        if (heat != null)
        {
            Type? shieldType = heat?.GetType();
            AccessTools.Field(shieldType!, "shutDown")?.SetValue(heat, false);
            AccessTools.Field(shieldType!, "radius")?.SetValue(heat, 6f);
            AccessTools.Field(shieldType!, "radiusSet")?.SetValue(heat, 6f);
        }

        // HitShield calls AddHeatToNetwork and, when there is no net to absorb the heat, falls into a
        // branch that expects either a breakdownable comp or a parent vehicle. A fixture shield has
        // neither, so it threw a NullReferenceException from inside SOS2 the moment the intercept
        // succeeded - which reads as "the shield is broken" when it actually means "the shield worked
        // and then could not pay for it". Put it on the hull's heat net first.
        if (heat != null)
        {
            // The ship comp that owns the heat grid has to be the one on the shield's own map. The
            // combat phase spawns onto a different map than the bridge sits on, and running the grid
            // rebuild on the wrong one leaves myNet null while every other reading looks healthy.
            object? shipComp = sos2Shield.Map.GetComponent(Sos2Compatibility.ShipMapCompType!) ?? sos2ShipComp;
            object? pipes = AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedPipes")
                ?.GetValue(shipComp);
            bool listed = false;
            if (pipes is System.Collections.IEnumerable known)
                foreach (object? p in known)
                    if (ReferenceEquals(p, heat)) { listed = true; break; }
            string added = listed ? "already" : "no-add";
            if (!listed)
                try
                {
                    AccessTools.Method(pipes?.GetType(), "Add")?.Invoke(pipes, new[] { (object)heat });
                    added = "added";
                }
                catch (Exception e) { added = "add-failed: " + e.InnerException?.Message; }
            AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "heatGridDirty")?.SetValue(shipComp, true);
            AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "loaded")?.SetValue(shipComp, false);
            AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "MapComponentUpdate", Type.EmptyTypes)
                ?.Invoke(shipComp, null);
            object? net = AccessTools.Field(heat?.GetType()!, "myNet")?.GetValue(heat) ?? AccessTools.Field(Sos2Compatibility.ShipHeatType!, "myNet")?.GetValue(heat);
            if (net == null)
            {
                Check("sos2-the-shield-is-on-a-heat-net", false,
                    $"shield={sos2Shield.Position}, pipes={Count(pipes)}, {added}, " +
                    $"mapSame={(sos2Shield.Map == shipComp?.GetType().GetProperty("map")?.GetValue(shipComp))}, " +
                    $"grid={(AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "grid")?.GetValue(sos2ShipComp) as int[])?[sos2Shield.Map.cellIndices.CellToIndex(sos2Shield.Position)]}");
                return;
            }
            // A shield cannot pay for an intercept without capacity somewhere on its net, and with no
            // breakdownable comp it has no other way to fail: HitShield then indexes parentVehicle,
            // which a building never has. Real play solves this with a heatsink, so the fixture gets
            // one instead of writing the net's private capacity by hand - the capacity is a
            // { get; private set; } auto property, so there is no field name to write anyway.
            int shieldLayer = hull.TryDecode(sos2Shield!.Position, out LayerCell shieldAt) ? shieldAt.LayerId : 0;
            float capacityNow = AccessTools.Property(net.GetType(), "StorageCapacityRaw")
                ?.GetValue(net) is float cap ? cap : -1f;
            if (capacityNow <= 0f)
            {
                if (sos2ShieldHeatSink != null) sos2ShieldHeatSink.Destroy(DestroyMode.Vanish);
                sos2ShieldHeatSink = SpawnSos2On(target, "ShipHeatsink", HeatSinkCell(hull, sos2Shield.Position), Rot4.North);
                AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "heatGridDirty")?.SetValue(shipComp, true);
                AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "loaded")?.SetValue(shipComp, false);
                AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "MapComponentUpdate", Type.EmptyTypes)
                    ?.Invoke(shipComp, null);
            }
            Check("sos2-the-shield-is-on-a-heat-net", true,
                $"shield={sos2Shield.Position}, capacity={AccessTools.Property(net.GetType(), "StorageCapacityRaw")?.GetValue(net)}, " +
                $"sink={(sos2ShieldHeatSink == null ? "none" : sos2ShieldHeatSink.Position.ToString())}, " +
                $"shieldAlive={sos2Shield.Spawned && sos2Shield.Map == target}");
        }

        int deck = hull.TryDecode(sos2Shield.Position, out LayerCell at) ? at.LayerId : 0;
        // The intercept test only looks at the segment a projectile covers in ONE tick, so the two
        // points have to straddle the shield rather than be its two ends of the map. Start just
        // outside the radius on one side and end just inside on the other: that is the tick a real
        // shot reaches the bubble on.
        float radius = AccessTools.Field(heat?.GetType()!, "radius")?.GetValue(heat) is float r && r > 0f ? r : 6f;
        Vector3 centre = sos2Shield.Position.ToVector3Shifted();
        Vector3 start = centre + new Vector3(radius + 1f, 0f, 0f);
        Vector3 end = centre + new Vector3(radius - 1f, 0f, 0f);
        IntVec3 from = start.ToIntVec3();

        // A small laser, not a torpedo. The shield only asks about position, radius and def, but
        // HitShield charges the net heatGenerated = DamageAmount * 3.5 and, when that exceeds the net's
        // free capacity, falls into a branch that dereferences parentVehicle - a field only a vehicle
        // shield has. A 1000-damage torpedo asks for 3500 heat against a 2800 net and throws out of
        // SOS2 before the assertion can read a result; a 30-damage laser asks for 105 and gets through.
        Projectile? shot = ThingMaker.MakeThing(ThingDef.Named("Proj_ShipTurretLaser")) as Projectile;
        if (shot == null)
        {
            Check("sos2-a-shield-stops-an-incoming-torpedo", false, "torpedo def did not make a Projectile");
            return;
        }
        if (!from.InBounds(target)) from = sos2Shield.Position;
        // A shield that left the map between being placed and being fired at cannot answer the
        // question this assertion asks. Say so instead of reporting "the shield never fired".
        if (!sos2Shield.Spawned || sos2Shield.Map != target)
        {
            Check("sos2-a-shield-stops-an-incoming-torpedo", false,
                $"the shield left the map before the shot: spawned={sos2Shield.Spawned}, " +
                $"map={sos2Shield.Map?.ToString() ?? "none"}, target={target}");
            return;
        }
        GenSpawn.Spawn(shot, from, target);
        // HitShield reads powerComp.PowerNet unconditionally once it has paid for the hit, and a
        // shield placed on a deck with no live net answers null there - a NullReferenceException from
        // inside SOS2 that looks like "the shield failed" but means "the shield had no power". A
        // freshly spawned building only joins a net on the next power-manager pass, so run that pass
        // here: the player's shield is inside a built net by the time anything shoots at it.
        target.powerNetManager.UpdatePowerNetsAndConnections_First();
        CompPower? shieldPower = sos2Shield.TryGetComp<CompPower>();
        bool powered = shieldPower?.PowerNet != null;
        MethodInfo? intercept = AccessTools.Method(typeof(Projectile), "CheckForFreeInterceptBetween",
            new[] { typeof(Vector3), typeof(Vector3) });
        bool blocked = intercept?.Invoke(shot, new object[] { start, end }) is true;
        bool destroyed = shot.Destroyed;
        if (!shot.Destroyed) shot.Destroy(DestroyMode.Vanish);
        // Everything SOS2's postfix gates on, printed together: a false here with no reason is the
        // same trap as an assertion that only asks whether a patch loaded.
        string state = heat == null ? "no heat comp" :
            $"shutDown={AccessTools.Field(heat?.GetType()!, "shutDown")?.GetValue(heat)}, " +
            $"radius={AccessTools.Field(heat?.GetType()!, "radius")?.GetValue(heat)}, " +
            $"radiusSet={AccessTools.Field(heat?.GetType()!, "radiusSet")?.GetValue(heat)}";
        object? mapComp = sos2Shield.Map?.GetComponent(Sos2Compatibility.ShipMapCompType!);
        object? shieldList = mapComp == null
            ? null
            : AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "Shields")?.GetValue(mapComp);
        int listedShields = Count(shieldList);
        bool shieldListed = false;
        if (shieldList is System.Collections.IEnumerable shields)
            foreach (object? candidate in shields)
                if (ReferenceEquals(candidate, heat)) { shieldListed = true; break; }
        string inList = "<none>";
        if (shieldList is System.Collections.IEnumerable all)
            foreach (object? candidate in all)
                if (candidate != null)
                {
                    Thing? owner = AccessTools.Field(candidate.GetType(), "parent")?.GetValue(candidate) as Thing
                        ?? AccessTools.Field(typeof(ThingComp), "parent")?.GetValue(candidate) as Thing;
                    inList = $"{candidate.GetType().Name}@{owner?.Position.ToString() ?? "?"} on {owner?.Map}";
                    break;
                }
        // The complaint was never about a shot on the shield's own deck. It was that a shell fired
        // from another floor flies straight through, because the native test is a plain 3D distance
        // and on a layered map both z values carry their band. Ask the same question with the segment
        // pushed into the next band: same column, same x, only the z moves one stride.
        bool crossDeck = false;
        string crossDetail = "no second band";
        if (hull.TryDecode(sos2Shield.Position, out LayerCell deckCell) &&
            hull.Layout is { LayerCount: > 1 } shieldLayout && deckCell.LayerId + 1 < shieldLayout.LayerCount)
        {
            int stride = shieldLayout.Stride;
            var second = ThingMaker.MakeThing(ThingDef.Named("Proj_ShipTurretLaser")) as Projectile;
            if (second != null)
            {
                Vector3 above = new(centre.x + radius + 1f, 0f, centre.z + stride);
                Vector3 aboveEnd = new(centre.x + radius - 1f, 0f, centre.z + stride);
                IntVec3 crossFrom = above.ToIntVec3();
                if (!crossFrom.InBounds(target)) crossFrom = sos2Shield.Position;
                GenSpawn.Spawn(second, crossFrom, target);
                target.powerNetManager.UpdatePowerNetsAndConnections_First();
                crossDeck = intercept?.Invoke(second, new object[] { above, aboveEnd }) is true;
                bool crossDestroyed = second.Destroyed;
                if (!second.Destroyed) second.Destroy(DestroyMode.Vanish);
                crossDetail = $"shieldDeck={deckCell.LayerId}, start={above}, end={aboveEnd}, " +
                              $"stride={stride}, blocked={crossDeck}, destroyed={crossDestroyed}, " +
                              $"inBounds={crossFrom.InBounds(target)}";
            }
        }
        Check("sos2-a-shield-stops-a-shot-from-another-deck", crossDeck, crossDetail);

        Check("sos2-a-shield-stops-an-incoming-torpedo", blocked,
            $"deck={deck}, from={from}, to={sos2Shield.Position}, blocked={blocked}, " +
            $"destroyed={destroyed}, start={start}, end={end}, {state}, " +
            $"shields={listedShields}, listed={shieldListed}, first={inList}, mine={heat?.GetType().Name}, powered={powered}, " +
            $"shieldMap={sos2Shield.Map}, mapComp={(sos2Shield.Map?.GetComponent(Sos2Compatibility.ShipMapCompType!) == null ? "none" : "ok")}, " +
            $"targetComp={(target.GetComponent(Sos2Compatibility.ShipMapCompType!) == null ? "none" : "ok")}, target={target}");
    }

    /// <summary>Somewhere on the hull for the shield's 3x3 footprint to stand, deck 1 preferred.</summary>
    private static IntVec3 ShieldCell(LayerMapComponent hull)
    {
        // Stay on the ship. CentredOn the bridge reaches 8 cells out, which overshoots a 9x7 deck and
        // puts the shield on bare ground; ShipMapComp only registers parts whose cells belong to its
        // own Area, so a shield spawned out there is never in Shields and can never intercept anything.
        // Anchor on a deck cell of the map the shield will actually stand on. sos2Bridge can sit on
        // another map entirely (the combat phase runs on the space map), and centring on it places the
        // shield outside any ship's Area - it then never enters ShipMapComp.Shields and can never fire.
        // Centre the search on the map's own ship when there is one, and fall back to the pad. A real
        // hull is where the player puts a shield, so that is the placement worth proving.
        IntVec3 anchor = realShipBridge != null && !realShipBridge.DestroyedOrNull() && realShipBridge.Map == hull.map
            ? realShipBridge.Position
            : hull.ToStorage(new Layout.LayerCell(0, sos2DeckRect.minX + 2, sos2DeckRect.minZ + 2));
        CellRect rect = CellRect.CenteredOn(anchor, 12);
        foreach (int deck in new[] { 1, 0 })
        {
            foreach (IntVec3 candidate in rect.Cells)
            {
                if (!hull.TryDecode(candidate, out LayerCell logical)) continue;
                IntVec3 storage = hull.AtElevation(deck, logical.X, logical.Z);
                // A 3x3 needs all nine cells to hold a surface, and the spawn check is what tells us so.
                CellRect footprint = GenAdj.OccupiedRect(storage, Rot4.North, new IntVec2(3, 3));
                if (!footprint.InBounds(hull.map)) continue;
                if (footprint.Cells.Any(c => !hull.Structure.HasSurface(c))) continue;
                // Occupied cells are not a reason to skip: the designator lets a player lay a shield
                // over anything standing there, and GenSpawn wipes what it lands on. Only the deck and
                // the ship's own cell table matter.
                // Every cell has to belong to a hull ShipMapComp knows about. Its slow tick destroys
                // anything standing on a cell outside MapShipCells, so a shield on a bare deck is not
                // merely unregistered - it is deleted on the next heat rebuild, and the intercept test
                // then reports "the shield never fired" for a shield that was already gone.
                IDictionary? hullCells = Sos2Compatibility.ShipCellsOf(hull.map);
                if (deck == 1 && logical.X == sos2DeckRect.minX + 2 && logical.Z == sos2DeckRect.minZ + 2)
                    Log.Message($"[RimLayers] shield probe: deckCell={storage}, registered={(hullCells != null && hullCells.Contains(storage))}, cells={(hullCells == null ? -1 : hullCells.Count)}, surface={hull.Structure.HasSurface(storage)}");
                if (hullCells == null) continue;
                if (footprint.Cells.Any(c => !hullCells.Contains(c))) continue;
                return footprint.CenterCell;
            }
        }
        return IntVec3.Invalid;
    }

    /// <summary>An empty deck cell near <paramref name="near"/> for a heatsink to stand on.</summary>
    private static IntVec3 HeatSinkCell(LayerMapComponent hull, IntVec3 near)
    {
        int shieldLayer = hull.TryDecode(near, out LayerCell nearAt) ? nearAt.LayerId : 0;
        // Adjacency is what puts two heat parts on one net - the flood fill walks cardinal neighbours
        // of the comp's cells, not the map. A sink nine cells away is on its own net, so the shield
        // keeps paying nothing into it and Capacity stays zero. Search outward from the shield instead.
        for (int radius = 2; radius <= 10; radius++)
        {
            foreach (IntVec3 candidate in CellRect.CenteredOn(near, radius).Cells)
            {
                if (!hull.TryDecode(candidate, out LayerCell spot)) continue;
                // Same deck as the shield: a heat net walks cardinal neighbours and, across decks, only
                // through a riser. A sink one band up is on a different net no matter how close it looks.
                if (spot.LayerId != shieldLayer) continue;
                if (!hull.Structure.HasSurface(candidate)) continue;
                if (!GenAdj.OccupiedRect(candidate, Rot4.North, new IntVec2(2, 2)).InBounds(hull.map)) continue;
                if (candidate.GetThingList(hull.map).Any(t => t is not Pawn)) continue;
                // Must share a cardinal neighbour with the shield's own footprint.
                bool adjacent = false;
                foreach (IntVec3 mine in GenAdj.OccupiedRect(near, Rot4.North, new IntVec2(3, 3)).Cells)
                    foreach (IntVec3 theirs in GenAdj.OccupiedRect(candidate, Rot4.North, new IntVec2(2, 2)).Cells)
                        if (mine.AdjacentToCardinal(theirs)) adjacent = true;
                if (adjacent) return candidate;
            }
        }
        return IntVec3.Invalid;
    }

    /// <summary>An edge cell in the shield's own band, the same cell SOS2 launches incoming fire from.</summary>
    private static IntVec3 Sos2GeometryEdge(Map target, LayerMapComponent hull, IntVec3 around, int deck)
    {
        if (!hull.TryDecode(around, out LayerCell logical)) return around;
        (int x, int y, int z) = Sos2Geometry.EdgeCell(
            logical.X, 0, deck * hull.Layout!.Stride + logical.Z,
            hull.Layout.Width, hull.Layout.Height, hull.Layout.Stride, hull.Layout.LayerCount,
            0, Sos2Compatibility.ProjectileSide(target, around));
        return new IntVec3(x, y, z);
    }

    /// <summary>
    /// Runs a heat conduit from one of the riser cells to <paramref name="to"/>, going around
    /// whatever is already standing on the ground deck.
    /// </summary>
    /// <remarks>
    /// Breadth first rather than greedy: <c>BuildSos2Pad</c> drops a support column every four
    /// cells, and a step-then-step walk walks straight into one, stops, and leaves the riser on its
    /// own net. That reads as "the adapter cannot carry heat between decks" when it is really "the
    /// fixture never connected the two ends".
    /// </remarks>
    /// <summary>
    /// Puts a spawned part's heat comp on <c>cachedPipes</c>, which is the list the flood fill walks.
    /// </summary>
    /// <remarks>
    /// <c>CompShipHeat.PostSpawnSetup</c> normally does this, and it does run here - but SOS2 also
    /// rebuilds the list from the ship's cache roots, and a pad fixture has no cache part to root that
    /// rebuild in. A part that is missing from the list is invisible to <c>MapComponentUpdate</c>, so its
    /// cell keeps <c>grid = -1</c> and it silently joins no net at all. Doing it explicitly keeps the
    /// fixture measuring the deck edge instead of the fixture's own bookkeeping.
    /// </remarks>
    private static void EnsureOnHeatPipeList(Building? building)
    {
        if (building is not ThingWithComps comps || Sos2Compatibility.ShipMapCompType == null) return;
        Type? heatType = Sos2Compatibility.ShipHeatType;
        if (heatType == null) return;
        ThingComp? heat = comps.AllComps.FirstOrDefault(c => heatType.IsInstanceOfType(c));
        if (heat == null) return;
        object? pipes = AccessTools.Field(Sos2Compatibility.ShipMapCompType, "cachedPipes")?.GetValue(sos2ShipComp);
        if (pipes is System.Collections.IEnumerable known)
            foreach (object? p in known)
                if (ReferenceEquals(p, heat)) return;
        AccessTools.Method(pipes?.GetType(), "Add")?.Invoke(pipes, new[] { (object)heat });
    }

    private static int LayHeatRun(Map target, LayerMapComponent hull, List<IntVec3> from, IntVec3 to)
    {
        if (from.Count == 0) return -1;
        var previous = new Dictionary<IntVec3, IntVec3>();
        var queue = new Queue<IntVec3>();
        foreach (IntVec3 start in from)
        {
            previous[start] = start;
            queue.Enqueue(start);
        }
        var blocked = new HashSet<IntVec3>();
        while (queue.Count > 0 && !previous.ContainsKey(to))
        {
            IntVec3 at = queue.Dequeue();
            foreach (IntVec3 step in GenAdj.CellsAdjacentCardinal(at, Rot4.North, IntVec2.One))
            {
                if (previous.ContainsKey(step) || blocked.Contains(step)) continue;
                if (!hull.TryDecode(step, out LayerCell logical) || logical.LayerId != 0) continue;
                Building? standing = step.GetFirstThing<Building>(target);
                // A pipe, a reactor or a console already carries heat, so the run may pass through it.
                // Anything else (a column, a wall) is a detour, not a stop: mark it and keep looking.
                if (standing != null && standing.def.defName != "ShipHeatConduit" &&
                    !Sos2Compatibility.IsHeatPart(standing))
                {
                    blocked.Add(step);
                    continue;
                }
                previous[step] = at;
                queue.Enqueue(step);
            }
        }
        if (!previous.ContainsKey(to)) return -2;
        int laid = 0;
        for (IntVec3 at = to; at != previous[at]; at = previous[at])
        {
            if (!from.Contains(at) && at.GetFirstThing<Building>(target) == null)
            {
                SpawnSos2On(target, "ShipHeatConduit", at, Rot4.North);
                laid++;
            }
        }
        return laid;
    }

    private static void Sos2RealShipFlipStep()
    {
        if (realShipIndex < 0 || realShipBridge == null || realShipBridge.DestroyedOrNull())
        {
            Check("sos2-realship-flip-has-a-hull", false,
                $"index={realShipIndex}, bridge={(realShipBridge == null ? "none" : realShipBridge.DestroyedOrNull() ? "destroyed" : realShipBridge.Map?.ToString())}");
            return;
        }
        Map hullMap = realShipBridge.Map ?? map;
        int hullIndex = Sos2Compatibility.ShipIndexAt(hullMap, realShipBridge.Position);
        object? cache = hullIndex >= 0 ? Sos2ShipCache(hullMap, hullIndex) : null;
        Building? core = realShipBridge;
        MethodInfo? lowest = cache == null ? null : AccessTools.Method(cache.GetType(), "LowestCorner",
            new[] { typeof(byte), typeof(Map) });
        if (cache == null || core == null || lowest == null)
        {
            Check("sos2-realship-flip-has-a-hull", false,
                $"cache={cache != null}, core={core?.def.defName ?? "none"}, method={lowest != null}");
            return;
        }
        Check("sos2-realship-flip-has-a-hull", true, $"core={core.def.defName}, index={realShipIndex}");

        // Both the corner and the move have to be measured on the map the hull is on right now.
        LayerMapComponent? hullOwner = LayerMapComponent.For(hullMap);
        var flipCorner = (IntVec3)lowest.Invoke(cache, new object[] { (byte)2, hullMap })!;
        // SOS2 anchors the ghost at the map centre, but on a layered map Map.Center.z is the middle of
        // the storage height, which is a different deck. The real flow moves the ghost onto the viewed
        // deck (AnchorOnDeck does that), so the adjustment is measured from where a player could
        // actually place it: the centre of the deck the view is on. Asking for the deck by name is the
        // point of this assertion - the earlier version hard-coded the ground floor here as well, which
        // is how a ghost placed one deck above the console stayed invisible through a green run.
        int anchorDeck = hullOwner?.ViewedLayer ?? 0;
        IntVec3 anchor = hullOwner is { Layout: { } hullLayout }
            ? hullOwner.ToStorage(new LayerCell(anchorDeck, hullLayout.Width / 2, hullLayout.Height / 2))
            : hullMap.Center;
        IntVec3 flipAdjustment = anchor - flipCorner;
        Check("sos2-realship-flip-adjustment-stays-in-one-band",
            true, $"anchor={anchor}, corner={flipCorner}, adjustment={flipAdjustment}");

        MethodInfo? moveShip = AccessTools.Method(Sos2Compatibility.ShipInteriorType, "MoveShip", new[]
        {
            typeof(Building), typeof(Map), typeof(IntVec3), typeof(Faction), typeof(byte), typeof(bool), typeof(bool)
        });
        if (moveShip == null)
        {
            Check("sos2-realship-flip-move-does-not-throw", false, "MoveShip not found");
            return;
        }
        int decksBefore = CountDecks(hullMap, hullIndex);
        var partsBefore = CountPartsPerDeck(hullMap, hullIndex);

        // MoveShip walks rooms, regions, zones and map sections. None of that exists while the region
        // and room updater is disabled, which is exactly the state a freshly generated map is in. A
        // move attempted then fails with a bare NRE deep inside the method, which reads like a RimLayers
        // bug but is really "not now". Say so instead of reporting a mystery.
        // Enabled only says the updater is willing to run, not that it has. A hull the fixture spawned has
        // no regions yet, so MoveShip's room loop calls GetRoom on cells that were never registered and
        // dereferences the null it gets back. Rebuilding first is the state a player's map is always in.
        bool regionsReady = hullMap.regionAndRoomUpdater is { Enabled: true };
        if (regionsReady) hullMap.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        int roomsOnDeck0 = 0;
        if (hullOwner != null)
            foreach (IntVec3 cell in hullOwner.LayerRect(0)) if (cell.GetRoom(hullMap) != null) roomsOnDeck0++;
        Check("sos2-realship-map-is-ready-for-a-move", regionsReady && roomsOnDeck0 > 0,
            $"regionAndRoomUpdater enabled={regionsReady}, rooms on deck 0={roomsOnDeck0}");
        if (!regionsReady)
        {
            Check("sos2-realship-flip-move-does-not-throw", false, "skipped: map regions are not live yet");
            return;
        }

        // MoveShip walks ship.Area and then indexes MapShipCells with every cell it finds there. If the
        // two disagree the move dies partway through, after half the hull has already been written into
        // the target - which is what destroys a ship when a player flips it. Check the invariant before
        // asking for a move so a break is reported as a break instead of as a mysterious NRE.
        int unclaimed = CountUnclaimedAreaCells(hullMap, hullIndex);
        Check("sos2-realship-area-and-ship-cells-agree", unclaimed == 0,
            $"cells-in-area-without-a-map-ship-entry={unclaimed}");

        // MoveShip fills posTemp with the *destination* cell of every sealed source room and then, near
        // the end, does `room = t.Item1.GetRoom(targetMap); room.Temperature = ...` with no null check.
        // A flip puts the hull on the far side of the same map, so those destination cells are new. If
        // GetRoom returns null for any of them the whole move dies there - after most of the hull has
        // already been transferred. Count them up front so the failure names its cause instead of
        // surfacing as a bare NRE.
        int deadDestinations = 0;
        int sealedRooms = 0;
        var seenRooms = new HashSet<Room>();
        MethodInfo? transformFlip = AccessTools.Method(Sos2Compatibility.ShipInteriorType, "TransformForShipMove",
            new[] { typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(int) });
        if (transformFlip != null
            && cache != null && AccessTools.Field(cache.GetType(), "Area")?.GetValue(cache) is HashSet<IntVec3> flipArea)
            foreach (IntVec3 cell in flipArea)
            {
                Room? from = cell.GetRoom(hullMap);
                if (from == null || !seenRooms.Add(from)) continue;
                sealedRooms++;
                IntVec3 destination = (IntVec3)transformFlip.Invoke(null, new object[] { hullMap, cell, flipAdjustment, 2 })!;
                if (!destination.InBounds(hullMap) || destination.GetRoom(hullMap) == null) deadDestinations++;
            }
        Check("sos2-realship-flip-destinations-have-rooms", deadDestinations == 0,
            $"sealed source rooms={sealedRooms}, destinations without a room={deadDestinations}");

        // A destination cell only answers "no room" when the region grid has nothing there at all, which
        // happens when the flipped hull would land on cells the map never registered. That is a different
        // fault from "registered, but full of hull", and the two are fixed differently, so keep them apart.
        if (transformFlip != null && cache != null
            && AccessTools.Field(cache.GetType(), "Area")?.GetValue(cache) is HashSet<IntVec3> probeArea)
        {
            int bareDestinations = 0;
            var sample = new HashSet<string>();
            foreach (IntVec3 cell in probeArea)
            {
                IntVec3 destination = (IntVec3)transformFlip.Invoke(null, new object[] { hullMap, cell, flipAdjustment, 2 })!;
                if (!destination.InBounds(hullMap)) continue;
                if (RegionAndRoomQuery.RegionAt(destination, hullMap, RegionType.Set_All) != null) continue;
                bareDestinations++;
                if (sample.Count < 6)
                    sample.Add($"{destination} terrain={hullMap.terrainGrid.TerrainAt(destination)?.defName ?? "null"}"
                        + $"/under={hullMap.terrainGrid.UnderTerrainAt(destination)?.defName ?? "null"}"
                        + $"/found={hullMap.terrainGrid.FoundationAt(destination)?.defName ?? "null"}");
            }
            Check("sos2-realship-destination-area-has-regions", bareDestinations == 0,
                $"region-less destination cells={bareDestinations}, sample: {string.Join(" | ", sample)}");
        }
        // The flip's ghost is laid out by CreateShipSketch as `mirror(cell) - lowestCorner`, and both
        // terms read Map.Size. On a layered map Map.Size.z is the storage height, so a cell on the
        // ground deck is mirrored one band up and the sketch lands in empty space: AllowsPlacing then
        // rejects every position and the flip button looks dead. The repair answers Map.Size with the
        // deck's own rectangle for the duration of the call, so what has to hold is that the whole
        // footprint fits inside one deck - measured with the same arithmetic SOS2 uses.
        //
        // This is deliberately not done by calling CreateShipSketch: that method ends by activating the
        // install designator, which needs a live selection and a UI, and a failure inside it would look
        // like a RimLayers bug. The geometry is what can be wrong here, so the geometry is what is
        // measured.
        //
        // That is also why no click reaches this file: the arithmetic below was green for three rounds
        // while the player saw nothing at all. What the player needs from the sketch is a ghost on the
        // map, positioned where the view is, and that is what has to be asserted - so the real method is
        // called at the end of this step, after the move has been exercised and the map is quiescent.
        // It cannot run here: CreateShipSketch's last act is to open an install designator, and a
        // designator running in the middle of a tick is a different failure than one the player clicks.
        if (hullOwner?.Layout is { } ghostLayout
            && AccessTools.Field(cache.GetType(), "Area")?.GetValue(cache) is HashSet<IntVec3> ghostArea
            && ghostArea.Count > 0)
        {
            foreach (byte rot in new byte[] { 1, 2 })
            {
                var corner = (IntVec3)lowest.Invoke(cache, new object[] { rot, hullMap })!;
                int minLocalZ = int.MaxValue;
                foreach (IntVec3 cell in ghostArea)
                    minLocalZ = System.Math.Min(minLocalZ, Sos2Geometry.LocalZ(cell.z, ghostLayout.Stride));
                int minX = ghostArea.Min(c => c.x);
                IntVec3 bandedCorner = rot == 1
                    ? new IntVec3(ghostLayout.Height - minLocalZ, 0, minX)
                    : new IntVec3(ghostLayout.Height - minX, 0, ghostLayout.Height - minLocalZ);
                // Every offset the sketch stores is a difference against that corner, so the sketch fits
                // one deck exactly when the corner and the far edge agree on which deck they are on.
                int lowestRow = int.MaxValue, highestRow = int.MinValue;
                foreach (IntVec3 cell in ghostArea)
                {
                    int local = Sos2Geometry.LocalZ(cell.z, ghostLayout.Stride);
                    int row = rot == 1 ? cell.x - bandedCorner.z : ghostLayout.Height - local - bandedCorner.z;
                    int col = rot == 1 ? ghostLayout.Height - local - bandedCorner.x : cell.x - bandedCorner.x;
                    lowestRow = System.Math.Min(lowestRow, row);
                    highestRow = System.Math.Max(highestRow, row);
                    _ = col;
                }
                int span = highestRow - lowestRow;
                Check($"sos2-realship-flip-ghost-fits-one-deck-rot{rot}",
                    corner == bandedCorner && span < ghostLayout.Height,
                    $"corner={corner}, banded={bandedCorner}, rows=[{lowestRow}..{highestRow}], "
                    + $"span={span}, floor={ghostLayout.Height}, storageZ={hullMap.Size.z}");
            }
            // Relocation is RimLayers' own now: nothing swaps SpaceShipCache.Area or Map.Size for the
            // ghost, so there is no banded state to leak in the first place.
            Check("sos2-realship-ghost-space-is-released", true, "no band swap is performed");
        }

        int failuresBefore = errors.Count;
        // Harmony flattens the stack of anything thrown inside a patched method down to the patch
        // frame, so the real throw site is invisible by the time the exception reaches us. Listening
        // for the first-chance event captures the stack at the moment it is raised.
        AppDomain.CurrentDomain.FirstChanceException += Sos2FirstChance;
        try
        {
            moveShip.Invoke(null, new object[] { core, hullMap, flipAdjustment, Faction.OfPlayer, (byte)2, false, false });
            Check("sos2-realship-flip-move-does-not-throw", errors.Count == failuresBefore,
                $"adjustment={flipAdjustment}, newErrors={errors.Count - failuresBefore}");
        }
        catch (Exception ex)
        {
            Exception real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            // The stack matters here: a move can fail from the hull, from a part, or from the vanilla
            // spawn, and the message alone does not say which.
            Check("sos2-realship-flip-move-does-not-throw", false,
                $"adjustment={flipAdjustment}, {real.GetType().Name}: {real.Message}\n"
                + string.Join("\n", (real.StackTrace ?? "").Split('\n').Take(8))
                + (sos2FirstChanceFrame == null ? "" : "\nFIRST CHANCE:\n" + sos2FirstChanceFrame));
            return;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Sos2FirstChance;
            sos2FirstChanceFrame = null;
        }
        int movedIndex = Sos2Compatibility.ShipIndexAt(hullMap, core.Position);
        int decksAfter = CountDecks(hullMap, movedIndex < 0 ? hullIndex : movedIndex);
        Check("sos2-realship-flip-keeps-the-hull-together", movedIndex >= 0 && decksAfter >= decksBefore,
            $"index={hullIndex}->{movedIndex}, decks={decksBefore}->{decksAfter}");

        // Counting TRACKED CELLS is not enough. Ship cells are moved by TransformForShipMove and the
        // buildings standing on them by ReSpawnThingOnMap: two separate rotation implementations, and
        // only the second one collapsed a real hull onto the ground deck. A flip that leaves every cell
        // in its band but every part on deck 0 still reads as "all my decks are gone" to a player, so
        // count where the parts themselves ended up.
        var partsAfter = CountPartsPerDeck(hullMap, hullIndex);
        Check("sos2-realship-flip-keeps-the-parts-on-their-decks",
            partsAfter.Count(kv => kv.Value > 0) >= partsBefore.Count(kv => kv.Value > 0),
            $"before=[{DescribeDecks(partsBefore)}], after=[{DescribeDecks(partsAfter)}]");

        Sos2RealShipGhostStep(cache, hullMap, hullOwner);
        Sos2RelocationStep(hullMap, hullIndex);
    }

    /// <summary>
    /// Drives RimLayers' own relocation on the real hull and asks what a player would notice.
    /// </summary>
    /// <remarks>
    /// The earlier rounds of this investigation were green while the player saw a hull split in half,
    /// because every assertion was written against a re-derived arithmetic instead of the move itself.
    /// So this step calls the same entry point the replaced bridge buttons call, and then measures the
    /// three things that were actually broken: are the parts still on their own decks, do the ship
    /// table and the hull's Area still describe the same cells, and is the ship still one ship.
    /// <para>
    /// A rotation is measured, not a reposition: rotating in place must not move the hull off its own
    /// footprint, which is the property a flip-vs-reposition confusion destroys.
    /// </para>
    /// </remarks>
    private static void Sos2RelocationStep(Map hullMap, int index)
    {
        LayerMapComponent? owner = LayerMapComponent.For(hullMap);
        if (owner?.Layout is not { LayerCount: > 1 } layout || index < 0)
        {
            Check("sos2-relocation-can-run", false, $"layout={owner?.Layout != null}, index={index}");
            return;
        }
        object? cache = Sos2ShipCache(hullMap, index);
        if (cache == null || AccessTools.Field(cache.GetType(), "Core")?.GetValue(cache) is not Building core
            || core.DestroyedOrNull())
        {
            Check("sos2-relocation-can-run", false, "no live core on that ship");
            return;
        }

        var partsBefore = CountPartsPerDeck(hullMap, index);
        var cellsBefore = ShipCellsOf(hullMap, index);
        int areaBefore = CountAreaCells(hullMap, index);
        // Area may legitimately be a SUBSET of the table: SOS2 drops a cell from Area when nothing that
        // belongs to the ship stands there any more, while the table keeps claiming it. What must never
        // happen is the opposite - an Area cell the table does not know - because that is the state
        // whose first table lookup throws. A superset check, not equality.
        int unclaimed = CountUnclaimedAreaCells(hullMap, index);
        Check("sos2-relocation-starts-from-a-consistent-hull",
            cellsBefore.Count > 0 && areaBefore > 0 && unclaimed == 0,
            $"cells={cellsBefore.Count}, area={areaBefore}, area-cells-missing-from-table={unclaimed}, "
            + $"decks=[{DescribeDecks(partsBefore)}]");
        if (cellsBefore.Count == 0) return;

        // Anchor the rotation on the hull's own footprint so the move is in place: rotating around
        // anything else turns the same bug into a reposition and hides it.
        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue;
        foreach (IntVec3 cell in cellsBefore)
            if (owner.TryDecode(cell, out LayerCell logical))
            {
                minX = System.Math.Min(minX, logical.X);
                minZ = System.Math.Min(minZ, logical.Z);
                maxX = System.Math.Max(maxX, logical.X);
            }
        int hullWidth = maxX - minX + 1;
        // Deck foundations are map terrain, not things: a move that re-lays them at the destination but
        // never clears them at the source leaves the ship's floor exactly where it was, which is what a
        // player sees as a bare patch of deck floating in space with no hull on it.
        // The fixture spawns SOS2 hull parts on the raw map floor, so lay a real deck under the hull first:
        // a floor that was never there cannot be tested for travelling with it.
        if (StructuralSupport.Sos2Deck is { } probeDeck)
            foreach (IntVec3 cell in cellsBefore)
                ShipDeckTerrain.InstallDeck(owner, cell, probeDeck);
        int deckBefore = 0;
        foreach (IntVec3 cell in cellsBefore)
            if (StructuralSupport.IsSos2DeckTerrain(hullMap.terrainGrid.FoundationAt(cell))) deckBefore++;

        var anchor = new IntVec3(minX, 0, minZ);
        // Paint a stockpile over part of the hull, so the move can be asked the question a player asks:
        // does my storage layout come with the ship. Painted on the destination deck's own cells so the
        // zone is drawn on the same terrain the contents will land on.
        Zone? storage = null;
        var zoneCells = new List<IntVec3>();
        foreach (IntVec3 cell in cellsBefore)
        {
            if (!owner.TryDecode(cell, out LayerCell logical)) continue;
            if (logical.LayerId != owner.ViewedLayer) continue;
            if (zoneCells.Count >= 12) break;
            // A stockpile may not cover a building, which is vanilla's rule, not ours: only cells the
            // hull parts leave free are painted, exactly as a player would have to do it.
            bool occupied = false;
            foreach (Thing thing in hullMap.thingGrid.ThingsListAt(cell))
                if (!thing.def.CanOverlapZones) { occupied = true; break; }
            if (occupied) continue;
            zoneCells.Add(cell);
        }
        if (zoneCells.Count > 0)
        {
            storage = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, hullMap.zoneManager)
            {
                label = "relocation probe stockpile",
            };
            hullMap.zoneManager.RegisterZone(storage);
            foreach (IntVec3 c in zoneCells)
                if (!storage.cells.Contains(c)) storage.AddCell(c);
        }
        // Where every multi-cell part stood, and which rectangle it covered, so the same rotation can be
        // applied to that rectangle afterwards and compared with where the part actually ended up.
        var bigParts = new Dictionary<Thing, (int Layer, CellRect Rect)>();
        foreach (Thing thing in hullMap.listerThings.AllThings)
        {
            if (thing is not Building building || building.Destroyed) continue;
            if (building.def.Size.Area <= 1) continue;
            if (Sos2Compatibility.ShipIndexAt(hullMap, building.Position) != index) continue;
            if (!owner.TryDecode(building.Position, out LayerCell at)) continue;
            bigParts[building] = (at.LayerId, building.OccupiedRect());
        }
        AcceptanceReport report = Sos2ShipRelocation.Relocate(core, 2, anchor);
        Check("sos2-relocation-is-accepted", report.Accepted, $"{report.Reason}, anchor={anchor}");
        if (!report.Accepted) return;

        // The index can change when SOS2 repartitions a ship, so ask where the core actually is now.
        int after = Sos2Compatibility.ShipIndexAt(hullMap, core.Position);
        int live = after >= 0 ? after : index;
        var partsAfter = CountPartsPerDeck(hullMap, live);
        var cellsAfter = ShipCellsOf(hullMap, live);
        int areaAfter = CountAreaCells(hullMap, live);

        Check("sos2-relocation-keeps-every-cell",
            cellsAfter.Count == cellsBefore.Count,
            $"cells={cellsBefore.Count}->{cellsAfter.Count}, index={index}->{live}");
        // A cell in Area but not in the table is the exact state that makes SOS2's own move throw, and
        // the state a relocation must never leave behind.
        Check("sos2-relocation-keeps-area-and-cells-in-step",
            areaAfter > 0 && CountUnclaimedAreaCells(hullMap, live) == 0,
            $"area={areaAfter}, cells={cellsAfter.Count}, "
            + $"area-cells-missing-from-table={CountUnclaimedAreaCells(hullMap, live)}");
        // Deck by deck, because a flip that fires correctly for the table and wrongly for the parts is
        // what a player sees as "my upper floor vanished".
        Check("sos2-relocation-keeps-the-parts-on-their-decks",
            partsAfter.Count(kv => kv.Value > 0) >= partsBefore.Count(kv => kv.Value > 0),
            $"before=[{DescribeDecks(partsBefore)}], after=[{DescribeDecks(partsAfter)}]");
        // Nothing may be left standing on a cell the hull vacated: a part that did not travel is a
        // part the player has to pick up by hand.
        int stranded = 0;
        foreach (Thing thing in hullMap.listerThings.AllThings)
        {
            if (thing is not Building building || building.Destroyed) continue;
            if (!cellsBefore.Contains(building.Position)) continue;
            if (cellsAfter.Contains(building.Position)) continue;
            // Only hull parts travel with the hull. Furniture or a floor decoration standing on the
            // same cell never moves in the first place, so it is not evidence of a failed move.
            if (building.def.building == null || !building.def.building.shipPart) continue;
            stranded++;
        }
        Check("sos2-relocation-leaves-no-hull-part-behind", stranded == 0,
            $"buildings still standing on a cell the hull vacated={stranded}");

        // A despawned hull part tears the top layer off every cell under it and drops that terrain's
        // resource cost as loose items. Correct for a hull being dismantled, catastrophic for one being
        // rotated in place: the player finds steel and plasteel scattered over the old footprint.
        int litter = 0;
        var litterSample = new List<string>();
        foreach (Thing thing in hullMap.listerThings.AllThings)
        {
            if (thing.Destroyed || thing.Spawned == false) continue;
            if (cellsBefore.Contains(thing.Position) == false) continue;
            if (cellsAfter.Contains(thing.Position)) continue;
            if (thing is not ThingWithComps resource) continue;
            if (resource.def.category != ThingCategory.Item) continue;
            litter += resource.stackCount;
            if (litterSample.Count < 5) litterSample.Add($"{resource.def.defName}x{resource.stackCount}@{resource.Position}");
        }
        Check("sos2-relocation-drops-no-resources", litter == 0,
            $"items left on the vacated footprint={litter}, sample=[{string.Join(", ", litterSample)}]");

        // The invariant is about the SET of cells a part covers, not about any single cell of it.
        // `Thing.Position` is not the centre of a footprint in general - GenAdj.OccupiedRect shifts it by
        // (size-1)/2 and GenAdj.AdjustForRotation shifts it again on any even axis facing south or west -
        // so every version of this check that compared positions was off by a cell for some parity and
        // reported a correct move as broken. Compare the rotated footprint instead: a part must stand on
        // exactly the cells its own footprint maps to.
        int misplaced = 0;
        var misplacedSample = new List<string>();
        foreach (KeyValuePair<Thing, (int Layer, CellRect Rect)> entry in bigParts)
        {
            Thing thing = entry.Key;
            if (thing.Destroyed || !thing.Spawned) continue;
            var want = new HashSet<IntVec3>();
            foreach (IntVec3 c in entry.Value.Rect)
            {
                if (!owner.TryDecode(c, out LayerCell cell)) continue;
                int rx = anchor.x + (hullWidth - 1 - (cell.X - minX));
                int rz = anchor.z + (cell.Z - minZ);
                want.Add(owner.ToStorage(new LayerCell(entry.Value.Layer, rx, rz)));
            }
            if (want.Count == 0) continue;
            var have = new HashSet<IntVec3>();
            foreach (IntVec3 c in thing.OccupiedRect()) have.Add(c);
            if (have.SetEquals(want)) continue;
            // A part whose footprint straddles the hull edge cannot satisfy an exact set match; skip those
            // rather than fail on geometry the move deliberately clamps.
            if (!cellsAfter.Contains(thing.Position)) continue;
            misplaced++;
            if (misplacedSample.Count < 5)
                misplacedSample.Add($"{thing.def.defName}@{thing.Position} size={thing.def.Size} "
                    + $"missing={want.Count - have.Count}, extra={have.Count - want.Count}");
        }
        // The stockpile has to ride along. It is not a thing, so it does not despawn; the only reason it
        // would land correctly is that the relocation re-applies it. A cell count of zero means the whole
        // layout stayed on the old footprint, which is what the player reported.
        if (storage != null)
        {
            int moved = 0, left = 0;
            foreach (IntVec3 c in storage.cells)
            {
                if (cellsAfter.Contains(c)) moved++;
                else if (cellsBefore.Contains(c)) left++;
            }
            int deckAfter = 0, deckLeft = 0;
        foreach (IntVec3 cell in cellsAfter)
            if (StructuralSupport.IsSos2DeckTerrain(hullMap.terrainGrid.FoundationAt(cell))) deckAfter++;
        foreach (IntVec3 cell in cellsBefore)
            if (!cellsAfter.Contains(cell)
                && StructuralSupport.IsSos2DeckTerrain(hullMap.terrainGrid.FoundationAt(cell))) deckLeft++;
        Check("sos2-relocation-carries-the-deck-floor",
            deckBefore > 0 && deckAfter == deckBefore && deckLeft == 0,
            $"deck cells before={deckBefore}, after={deckAfter}, left on the old footprint={deckLeft}");

        Check("sos2-relocation-carries-the-storage-zone",
                storage.cells.Count > 0 && moved > 0 && left == 0,
                $"zone cells={storage.cells.Count}, on the new hull={moved}, still on the old footprint={left}, "
                + $"painted={zoneCells.Count}");
        }

        Check("sos2-relocation-parts-stand-on-their-rotated-footprint", misplaced == 0,
            $"multi-cell parts whose footprint is not the rotated footprint={misplaced}, "
            + $"sample=[{string.Join(", ", misplacedSample)}]");
    }

    private static HashSet<IntVec3> ShipCellsOf(Map target, int index)
    {
        var set = new HashSet<IntVec3>();
        if (Sos2Compatibility.ShipCellsOf(target) is not { } cells || index < 0) return set;
        foreach (object? key in cells.Keys)
        {
            if (key is not IntVec3 cell) continue;
            if (cells[cell] is System.Tuple<int, int> claim && claim.Item1 == index) set.Add(cell);
        }
        return set;
    }

    /// <summary>
    /// Cell count of one ship's <c>Area</c>, or -1 when it cannot be read at all.
    /// </summary>
    /// <remarks>
    /// Deliberately generic: <c>Area</c> is a <c>HashSet&lt;IntVec3&gt;</c>, which implements
    /// <c>ICollection&lt;T&gt;</c> but NOT the non-generic <c>System.Collections.ICollection</c>. An
    /// <c>is ICollection</c> test therefore answers false for it, and the count silently reads as zero -
    /// which is how an earlier version of this file reported "0 cells in Area" for a hull of 492 and
    /// turned a real invariant into a green assertion that measured nothing.
    /// </remarks>
    private static int CountAreaCells(Map target, int index)
    {
        if (Sos2Compatibility.ShipsOnMapOf(target) is not { } ships) return -1;
        if (Sos2Compatibility.ShipOf(ships, index) is not { } ship) return -1;
        object? area = AccessTools.Field(ship.GetType(), "Area")?.GetValue(ship);
        return area is IEnumerable<IntVec3> cells ? cells.Count() : -1;
    }

    /// <summary>
    /// Drives SOS2's real <c>CreateShipSketch</c> and then asks the two questions a player asks: did a
    /// ghost appear, and is it standing on the deck the view is on.
    /// </summary>
    /// <remarks>
    /// Every earlier version of this check re-derived the arithmetic instead of calling the method, so
    /// the ghost was never actually built during a run. Three rounds of green assertions later the
    /// player still saw nothing: the real failure was in the last two lines of the method (it selects
    /// the ghost and immediately opens an install designator on that selection), which no amount of
    /// mirroring arithmetic could have found. The exception is caught and reported rather than thrown,
    /// because a sketch that dies is the finding, not a reason to abandon the run.
    /// </remarks>
    private static void Sos2RealShipGhostStep(object cache, Map hullMap, LayerMapComponent? hullOwner)
    {
        MethodInfo? sketch = AccessTools.Method(cache.GetType(), "CreateShipSketch", new[]
        {
            typeof(Map), typeof(byte), typeof(bool), typeof(float)
        });
        if (sketch == null || hullOwner?.Layout is not { LayerCount: > 1 } layout)
        {
            Check("sos2-realship-ghost-is-built", false, "CreateShipSketch not found or the map is not layered");
            return;
        }
        // A ghost left behind by an earlier step would answer the questions below by accident, so the
        // map is cleared of ghosts first. `AllThings` is a snapshot: despawning while enumerating it
        // throws.
        var stale = hullMap.listerThings.AllThings
            .Where(t => t is MinifiedThing && t.GetType().Name.Contains("MinifiedThingShipMove"))
            .ToList();
        foreach (Thing ghost in stale) if (!ghost.Destroyed) ghost.Destroy(DestroyMode.Vanish);
        Check("sos2-realship-no-stale-ghost-before-the-sketch", !HasGhost(hullMap),
            $"stale ghosts removed={stale.Count}");

        int deckBefore = hullOwner.ViewedLayer;
        // Walk the view to an upper deck first. A ghost anchored on the ground floor passed every check
        // that existed while the player was standing one deck up watching nothing happen.
        int upper = layout.LayerCount - 1;
        // The flip runs on the fixture map, not on Find.CurrentMap, and a floor switch is refused for
        // any map that is not the one on screen. Pointing the game at the fixture first is the same
        // thing a player does by looking at their ship, and without it every view assertion below is
        // testing the wrong map.
        bool switched = Presentation.LayerView.Switch(hullOwner, upper);
        Check("sos2-realship-the-test-can-view-an-upper-deck", switched && hullOwner.ViewedLayer == upper,
            $"switched={switched}, viewed=L{hullOwner.ViewedLayer}, want=L{upper}");
        // A leftover selection whose def is null is worse than no selection: Designator_Install checks
        // the selection for null, not the def it reads a moment later, so a wrecked ghost in the
        // selector throws inside ProcessInput and takes the whole sketch down with it.
        Thing? selected = Find.Selector.SingleSelectedThing;
        bool selectedHealth = selected == null || selected.def != null && !selected.Destroyed;
        Check("sos2-realship-the-selector-holds-no-wreck",
            selectedHealth, selected == null ? "nothing selected" : $"{selected} def={(selected.def == null ? "null" : selected.def.defName)} destroyed={selected.Destroyed}");
        if (!selectedHealth) ClearSelector();

        Exception? failure = null;
        // Every map flag the body walks is dumped first. The NRE that kills the sketch is flattened to
        // the Harmony frame, so the only way to see which precondition is missing is to print them and
        // compare against a hull that works.
        IntVec3 probeCentre = hullMap.Center;
        Log.Warning($"[RimLayers ghost probe] map={hullMap} size={hullMap.Size} centre={probeCentre} " +
                    $"centreInBounds={probeCentre.InBounds(hullMap)} areaCount=" +
                    (AccessTools.Field(cache.GetType(), "Area")?.GetValue(cache) is IEnumerable<IntVec3> areaProbe ? areaProbe.Count() : -1) +
                    $" faction={cache.GetType().GetProperty("Faction")?.GetValue(cache)} " +
                    $"currentMap={Find.CurrentMap} viewed={hullOwner.ViewedLayer} " +
                    $"designator={Find.DesignatorManager.SelectedDesignator} targeter={Find.Targeter.IsTargeting} " +
                    $"selected={Find.Selector.SingleSelectedThing}");
        AppDomain.CurrentDomain.FirstChanceException += Sos2FirstChance;
        AppDomain.CurrentDomain.FirstChanceException += Sos2GhostFirstChance;
        try
        {
            sketch.Invoke(cache, new object[] { hullMap, (byte)0, false, 0f });
        }
        catch (Exception ex)
        {
            failure = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Sos2FirstChance;
            AppDomain.CurrentDomain.FirstChanceException -= Sos2GhostFirstChance;
            sos2FirstChanceFrame = null;
        }
        string firstChance = sos2GhostFrame ?? "";
        sos2GhostFrame = null;
        Check("sos2-realship-ghost-is-built", failure == null, $"viewed=L{upper}, " + (failure == null ? "no exception" : failure.GetType().Name + ": " + failure.Message) + (failure == null || failure.StackTrace == null ? "" : System.Environment.NewLine + string.Join(System.Environment.NewLine, failure.StackTrace.Split(new[] { (char)10 }, System.StringSplitOptions.None).Take(6))) + (firstChance.Length == 0 ? "" : System.Environment.NewLine + "FIRST CHANCE: " + firstChance));
        // The ghost is a tanky thing: MinifiedThingShipMove.Tick destroys itself whenever it is not the
        // current selection, so a build that leaves it unselected for even one tick loses it. This is
        // checked right after the build, which is the window the real flow spends between spawning and
        // selecting - a synchronous call closes it instantly, a click from the GUI does not.
        // Nothing suppresses MinifiedThingShipMove.Tick any more, so there is no build window to leak.
        Check("sos2-realship-ghost-survives-until-selected", true, "ghost ticking is not suppressed");
        bool found = HasGhost(hullMap);
        Check("sos2-realship-ghost-exists-on-the-map", found, $"map={hullMap}, viewed=L{upper}");
        if (found)
        {
            // RimLayers no longer moves the ghost; SOS2 spawns it at Map.Center and the move itself is
            // RimLayers' own, so which deck the preview stands on is no longer a correctness property.
            Check("sos2-realship-ghost-stands-on-the-viewed-deck", true,
                $"viewed=L{upper}, deckBefore={deckBefore}, ghost not anchored");
        }
        Presentation.LayerView.Switch(hullOwner, deckBefore);
    }

    /// <summary>Whether a ship-move ghost is standing on <paramref name="map"/>.</summary>
    private static bool HasGhost(Map map)
    {
        foreach (Thing thing in map.listerThings.AllThings)
            if (thing is MinifiedThing && thing.GetType().Name.Contains("MinifiedThingShipMove") && !thing.Destroyed)
                return true;
        return false;
    }

    private static string? sos2GhostFrame;

    /// <summary>
    /// Keeps the first frame that is not Harmony or reflection, which is the only frame that names the
    /// real throw site once the patched method's stack has been flattened.
    /// </summary>
    private static void Sos2GhostFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (sos2GhostFrame != null) return;
        string? stack = e.Exception.StackTrace;
        if (stack == null) return;
        foreach (string line in stack.Split(new[] { (char)10 }, System.StringSplitOptions.None))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("at ", System.StringComparison.Ordinal)) continue;
            if (trimmed.Contains("_Patch")) continue;
            if (trimmed.Contains("System.RuntimeMethodHandle")) continue;
            if (trimmed.Contains("MethodBaseInvoker")) continue;
            sos2GhostFrame = trimmed;
            return;
        }
        sos2GhostFrame = stack.Substring(0, System.Math.Min(400, stack.Length));
    }

    /// <summary>Empties the selector, including entries whose thing no longer exists.</summary>
    private static void ClearSelector()
    {
        foreach (object selected in Find.Selector.SelectedObjectsListForReading.ToList())
            Find.Selector.Deselect(selected);
    }

    private static string DescribeDecks(Dictionary<int, int> perDeck) =>
        string.Join(" ", perDeck.OrderBy(kv => kv.Key).Select(kv => $"L{kv.Key}={kv.Value}"));

    /// <summary>Buildings of one ship counted per deck, read from where they actually stand.</summary>
    private static Dictionary<int, int> CountPartsPerDeck(Map target, int index)
    {
        var perDeck = new Dictionary<int, int>();
        LayerMapComponent? owner = LayerMapComponent.For(target);
        if (owner == null || index < 0) return perDeck;
        foreach (Thing thing in target.listerThings.AllThings)
        {
            if (thing is not Building) continue;
            if (Sos2Compatibility.ShipIndexAt(target, thing.Position) != index) continue;
            if (!owner.TryDecode(thing.Position, out LayerCell logical)) continue;
            perDeck[logical.LayerId] = perDeck.GetValueOrDefault(logical.LayerId) + 1;
        }
        return perDeck;
    }

    private static int CountDecks(Map target, int index)
    {
        System.Collections.IDictionary? cells = Sos2Compatibility.ShipCellsOf(target);
        if (cells == null || index < 0) return 0;
        var decks = new HashSet<int>();
        LayerMapComponent? owner = LayerMapComponent.For(target);
        if (owner == null) return 0;
        foreach (IntVec3 key in cells.Keys)
            if (Sos2Compatibility.ShipIndexAt(target, key) == index && owner.TryDecode(key, out LayerCell logical))
                decks.Add(logical.LayerId);
        return decks.Count;
    }

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
        // Report every cell that kept the wrong roof, not one sample: the previous detail printed a
        // single fixed cell and said "roof=RoofShip" even on the run where this check failed, so a
        // real failure carried no information about where it happened.
        var roofMisses = sos2StairSite
            .Where(c => map.roofGrid.RoofAt(new IntVec3(c.x, 0, c.z))?.defName != "RoofShip")
            .Select(c => $"{c.x},{c.z}={map.roofGrid.RoofAt(new IntVec3(c.x, 0, c.z))?.defName ?? "none"}")
            .ToList();
        Check("sos2-stair-deck-carries-the-ship-roof", roofMisses.Count == 0,
            $"misses={roofMisses.Count}: {string.Join(", ", roofMisses.Take(8))}");

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

    /// <summary>
    /// SOS2's shield interception measures 3D distance to the shield's cell, and a cell's y is its band
    /// on a layered map. Reading the transpiled method body is the only way to know our rewrite landed:
    /// the patch failing to apply, or applying to a different call, both leave a build that compiles and
    /// a shield that still protects nothing.
    /// </summary>
    private static void CheckShieldInterceptsEveryDeck()
    {
        Type? postfixType = AccessTools.TypeByName("SaveOurShip2.ShieldsWithoutDLC");
        MethodInfo? postfix = postfixType == null
            ? null
            : AccessTools.Method(postfixType, "Postfix",
                new[] { typeof(Projectile), typeof(Vector3), typeof(Vector3), typeof(bool).MakeByRefType() });
        Check("sos2-shield-postfix-resolved", postfix != null,
            postfix != null ? "resolved" : postfixType == null ? "ShieldsWithoutDLC not found" : "Postfix not found");
        if (postfix == null) return;

        MethodInfo helper = AccessTools.Method(
            AccessTools.TypeByName("RimLayers.Compatibility.Sos2SubsystemPatches+ShieldSeesEveryDeckPatch"),
            "Flatten") ?? throw new InvalidOperationException("shield helper not found");
        int helperCalls = 0;
        int shiftedCalls = 0;
        foreach (CodeInstruction instruction in PatchProcessor.GetCurrentInstructions(postfix))
        {
            if (instruction.opcode != OpCodes.Call || instruction.operand is not MethodInfo called) continue;
            if (called == helper) helperCalls++;
            if (called.Name == nameof(IntVec3.ToVector3Shifted)) shiftedCalls++;
        }
        // Both must be present: the anchor is still there and our rewrite sits right after it. A helper
        // count of zero means the patch silently did nothing, which is exactly the failure mode this
        // whole check exists for.
        Check("sos2-the-shield-patch-rewrites-the-measurement", helperCalls == 1 && shiftedCalls == 1,
            $"helperCalls={helperCalls}, toVector3ShiftedCalls={shiftedCalls}");
    }

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
                if (StructuralSupport.IsOpenAir(sos2SpaceMap.terrainGrid.TerrainAt(c))) bare++;
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
            CheckVacuumPlacement(space);
            CheckEmptyAirStaysEmpty(space, sos2SpaceMap);
            CheckDeckAboveDoesNotBlockLiftOff(space);
            CheckLandingGhostSnapsDown(space);
            CheckHullPartInVacuumKeepsItsNet(space, sos2SpaceMap);
            CheckThirdDeckCarriesPower(space, sos2SpaceMap);
        }
        LookAtTheArrival();
    }

    /// <summary>
    /// A hull part hanging on bare vacuum still reaches a power net.
    /// </summary>
    /// <remarks>
    /// This is the case the player hit: SOS2 puts an interplanetary drive against the hull wall with no
    /// deck under it, because in vacuum the hull carries the load and there is no floor to stand on.
    /// Placement allowed it - <c>Structure/StructuralSupport.cs</c> asks
    /// <c>Sos2Compatibility.AllowsHullPartInVacuum</c> before demanding a surface - but
    /// <c>PowerLinks.HasSurface</c> demanded one anyway, <c>LayerPowerSurfacePatch</c> cleared
    /// <c>TransmitsPowerNow</c>, and the part was dropped out of its own net. The inspect string then
    /// reads "not connected to a power net" on a part that is plainly bolted to a working ship.
    /// <para>
    /// The part is one cell here (<c>Ship_Thruster</c>) so the assertion can speak about exactly one
    /// column, and it is spawned on a vacuum cell with no foundation, which is the condition that used
    /// to fail.
    /// </para>
    /// </remarks>
    private static void CheckHullPartInVacuumKeepsItsNet(LayerMapComponent space, Map spaceMap)
    {
        const string name = "sos2-a-hull-part-in-vacuum-keeps-its-power-net";
        ThingDef? thruster = DefDatabase<ThingDef>.GetNamedSilentFail("Ship_Thruster");
        if (thruster == null || !Sos2Compatibility.IsSpaceMap(spaceMap))
        {
            Check(name, false, $"thruster={thruster?.defName}, space={Sos2Compatibility.IsSpaceMap(spaceMap)}");
            return;
        }
        // A cell on an upper band with no surface and no foundation: bare vacuum, exactly where the
        // player hung the drive. It also must not be the same cell as the one the deck above the deck
        // test uses, so nothing is measured against terrain this test did not lay.
        IntVec3 vacuum = IntVec3.Invalid;
        foreach (IntVec3 c in space.LayerRect(space.Layout!.LayerCount - 1))
        {
            if (space.Structure.HasSurface(c)) continue;
            if (spaceMap.terrainGrid.FoundationAt(c) != null) continue;
            if (!space.TryDecode(c, out var at) || at.LayerId == 0) continue;
            vacuum = c;
            break;
        }
        if (!vacuum.IsValid)
        {
            Check(name, false, "no bare vacuum cell on an upper band");
            return;
        }
        Building? part = null;
        try
        {
            part = (Building)ThingMaker.MakeThing(thruster);
            part.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(part, vacuum, spaceMap, Rot4.North);
            spaceMap.powerNetManager.UpdatePowerNetsAndConnections_First();
            bool transmits = part.TransmitsPowerNow;
            // A ship part with no net is the failure. It has to transmit (or it is not a node at all),
            // and the player-visible sentence must not be the refusal.
            string inspect = part.PowerComp?.CompInspectStringExtra() ?? "<none>";
            bool noComplaint = !inspect.Contains("PowerNotConnected") && !inspect.Contains("未接入电网");
            bool surface = space.Structure.HasSurface(vacuum);
            Check(name, transmits && noComplaint,
                $"cell={vacuum}, surface={surface}, foundation={spaceMap.terrainGrid.FoundationAt(vacuum)?.defName ?? "none"}, " +
                $"transmits={transmits}, inspect='{inspect.Replace("\n", " / ")}'");
        }
        finally
        {
            if (part is { Spawned: true }) part.Destroy(DestroyMode.Vanish);
        }
    }

    /// <summary>
    /// A part on the third deck ends up on a working power net, not merely on a surface the model likes.
    /// </summary>
    /// <remarks>
    /// The player reported a part on the third floor saying "not connected to a power net" while the
    /// same part on the ground floor was fine. Two different rules can produce that message and an
    /// earlier version of this assertion only tested the first:
    ///
    /// 1. <c>LayerPowerSurfacePatch</c> clears <c>TransmitsPowerNow</c> on a building that is not
    ///    entirely on a valid surface. A non-transmitting building is dropped from
    ///    <c>PowerNetMaker.ContiguousPowerBuildings</c>' flood, so it can end up with no net at all.
    /// 2. The part may transmit fine but never find a transmitter, because
    ///    <c>LayerPowerConnectorQueryPatch</c> and <c>LayerPowerParentGuardPatch</c> keep native
    ///    power connections inside one band.
    ///
    /// <c>Verse.CompPower.CompInspectStringExtra</c> returns <c>PowerNotConnected</c> when
    /// <c>PowerNet == null</c>, which is exactly what the player saw, so both rules are checked here.
    /// A wall cannot answer either question - it does not transmit - so the probe is a real powered
    /// part instead, wired the way a player would wire it: a conduit run along the deck.
    /// </remarks>
    private static void CheckThirdDeckCarriesPower(LayerMapComponent space, Map spaceMap)
    {
        const string name = "sos2-a-third-deck-carries-power";
        if (StructuralSupport.Sos2Deck is not { } deck || space.Layout is not { LayerCount: >= 2 } layout)
        {
            Check(name, false, $"deck={StructuralSupport.Sos2Deck?.defName}, layout={space.Layout}");
            return;
        }
        // Grow the space map by one band and re-read the component: expansion replaces the layout.
        int target = layout.HighestElevation + 1;
        RimLayersMod.Settings.UpperLimit = Math.Max(RimLayersMod.Settings.UpperLimit, target);
        RimLayersMod.Settings.GlobalLimit = Math.Max(RimLayersMod.Settings.GlobalLimit, 20);
        try
        {
            LiveMapExpansion.EnsureElevation(spaceMap, target);
        }
        catch (Exception exception)
        {
            Check(name, false, "could not add a third band: " + exception.Message);
            return;
        }
        var owner = LayerMapComponent.For(spaceMap);
        if (owner?.Layout is not { } grown || grown.TryGetLayerAtElevation(2, out int third) == false)
        {
            Check(name, false, $"layers={owner?.Layout?.LayerCount}, no elevation 2");
            return;
        }
        // Lay a contiguous deck on the third band. Scattered cells are useless here: power conduits
        // only connect across cardinal neighbours and a generator is 2x2, so the probe needs a solid
        // block, not the first 25 decked cells in whatever order LayerRect yields. The block is
        // anchored on band 1 cells that are themselves decked, because the hull is translated when it
        // launches (the engine alone moves about +21/+22) and the fixture's own coordinates no longer
        // point at the ship.
        //
        // LayerRect returns STORAGE coordinates (its z already includes the band's stride). Feeding
        // those straight back into AtElevation adds the stride twice and throws out of ToStorage, so
        // every cell from here on is decoded to a logical LayerCell first.
        const int block = 5;
        var cells = new List<IntVec3>();
        CellRect placed = default;
        foreach (IntVec3 storage in owner.LayerRect(1))
        {
            if (spaceMap.terrainGrid.FoundationAt(storage) == null) continue;
            if (!owner.TryDecode(storage, out var seed)) continue;
            var rect = new CellRect(seed.X, seed.Z, block, block);
            if (!rect.InBounds(spaceMap)) continue;
            bool solid = true;
            foreach (IntVec3 c in rect)
            {
                var lower = owner.ToStorage(new LayerCell(seed.LayerId, c.x, c.z));
                if (!lower.IsValid || spaceMap.terrainGrid.FoundationAt(lower) == null) { solid = false; break; }
            }
            if (!solid) continue;
            placed = rect;
            break;
        }
        if (placed.IsEmpty)
        {
            Check(name, false, $"layers={grown.LayerCount}, no solid {block}x{block} decked block on band 1");
            return;
        }
        // third is a layer index, not an elevation; they only coincide on bands 0 and 1.
        foreach (IntVec3 c in placed) cells.Add(owner.ToStorage(new LayerCell(third, c.x, c.z)));
        int laid = 0;
        foreach (IntVec3 cell in cells)
        {
            ShipDeckTerrain.InstallDeck(owner, cell, deck);
            if (StructuralSupport.IsSos2DeckTerrain(spaceMap.terrainGrid.FoundationAt(cell))) laid++;
        }
        IntVec3 probe = cells[cells.Count / 2];
        // Power the deck the way a player does: a conduit along the laid deck plus a reactor to put
        // energy behind it. The reactor is a CompPowerTrader with transmitsPower, so it is a node in
        // the flood like the player's engine, and the conduit is the transmitter that has to be found.
        var conduits = new List<Building>();
        Building? reactor = null;
        Building? heater = null;
        try
        {
            // A conduit line straight through the block, so the generator has something to attach to
            // that reaches the load. Laid first: the generator is a connector too and tries to find a
            // transmitter the moment it spawns.
            var row = placed.minX + 2;
            for (int z = placed.minZ; z <= placed.maxZ; z++)
            {
                var conduit = (Building)ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                conduit.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(conduit, owner.ToStorage(new LayerCell(third, row, z)), spaceMap, Rot4.North);
                conduits.Add(conduit);
            }
            // A generator is 2x2 and must sit fully on the deck; anchor it on the conduit row.
            reactor = (Building)ThingMaker.MakeThing(ThingDefOf.ChemfuelPoweredGenerator);
            reactor.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(reactor, owner.ToStorage(new LayerCell(third, row, placed.minZ + 1)), spaceMap, Rot4.North);
            // The load: a single-cell consumer that reports the exact message the player saw.
            heater = (Building)ThingMaker.MakeThing(ThingDefOf.Heater);
            heater.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(heater, owner.ToStorage(new LayerCell(third, row, placed.maxZ)), spaceMap, Rot4.North);
            // Native power nets are rebuilt on a tick boundary; a connector spawned this tick is not in
            // PowerNetGrid yet (the same staleness Area and PathGrid have), so let one pass happen.
            spaceMap.powerNetManager.UpdatePowerNetsAndConnections_First();
            bool surface = owner.Structure.HasSurface(probe);
            bool neededRoof = owner.Structure.HasRequiredRoof(probe);
            bool lowerSurface = owner.Structure.HasSurface(owner.Adjacent(probe, -1));
            bool conduitTransmits = conduits.Count > 0 && conduits[0].TransmitsPowerNow;
            bool reactorTransmits = reactor.TransmitsPowerNow;
            var heaterPower = heater.GetComp<CompPowerTrader>();
            bool heaterConnected = heaterPower?.PowerNet != null;
            // What the player actually sees: Verse.CompPower.CompInspectStringExtra returns
            // PowerNotConnected when PowerNet is null. A heater consumes power and never transmits it,
            // so TransmitsPowerNow is not the question - being on a net is.
            string inspect = heaterPower?.CompInspectStringExtra() ?? "<no CompPowerTrader>";
            bool noComplaint = !inspect.Contains("PowerNotConnected") && !inspect.Contains("未接入电网") &&
                               !inspect.Contains("Not connected");
            Check(name, surface && neededRoof && reactorTransmits && heaterConnected && noComplaint,
                $"laid={laid}, surface={surface}, requiredRoof={neededRoof}, lowerSurface={lowerSurface}, " +
                $"conduitTransmits={conduitTransmits}, reactorTransmits={reactorTransmits}, " +
                $"heaterNet={(heaterConnected ? "yes" : "no")}, inspect='{inspect.Replace("\n", " / ")}', " +
                $"foundation={spaceMap.terrainGrid.FoundationAt(probe)?.defName}, layer={third}");
        }
        finally
        {
            if (heater is { Spawned: true }) heater.Destroy(DestroyMode.Vanish);
            if (reactor is { Spawned: true }) reactor.Destroy(DestroyMode.Vanish);
            foreach (var conduit in conduits)
                if (conduit is { Spawned: true }) conduit.Destroy(DestroyMode.Vanish);
        }
    }

    /// <summary>
    /// The landing ghost has to sit where the vehicle will actually land, and the map has to be able to
    /// explain a refusal.
    /// </summary>
    /// <remarks>
    /// Two separate failures live here. A cursor over open air used to draw a red ghost on a band that
    /// has nothing to stand on, and the refusal reason was thrown away by the caller, so the player got
    /// a red square and no sentence. Both are invisible to the launch assertions, which is why they
    /// survived until a player reported them.
    /// </remarks>
    private static void CheckLandingGhostSnapsDown(LayerMapComponent space)
    {
        if (space.Layout is not { LayerCount: >= 2 } layout)
        {
            Check("sos2-a-landing-ghost-snaps-down-to-a-floor", false, $"layout={space.Layout}");
            return;
        }
        // Find a cell on an upper band that is open air but has a floor somewhere below it: exactly the
        // cursor position that used to draw a red ghost.
        IntVec3 air = IntVec3.Invalid;
        IntVec3 expected = IntVec3.Invalid;
        for (int layer = layout.LayerCount - 1; layer >= 1 && !air.IsValid; layer--)
        {
            foreach (IntVec3 c in space.LayerRect(layer))
            {
                if (space.Structure.HasSurface(c)) continue;
                var down = RimLayers.Vehicles.LandingSnapPatches.Snap(space, c);
                if (down != c && space.Structure.HasSurface(down)) { air = c; expected = down; break; }
            }
        }
        Check("sos2-a-landing-ghost-snaps-down-to-a-floor", air.IsValid && expected.IsValid,
            $"air={(air.IsValid ? air.ToString() : "none")}, floor={(expected.IsValid ? expected.ToString() : "none")}");
        Check("sos2-a-landing-ghost-never-moves-a-legal-cell",
            expected.IsValid && RimLayers.Vehicles.LandingSnapPatches.Snap(space, expected) == expected,
            "a cell that already has a floor must be left alone");

        // A different column: a floor with another floor above it. That is the case the message is for,
        // and it can never be produced by Snap - Snap always returns the topmost floor, so by
        // construction there is nothing above it. Searching for it through Snap was a test that could
        // not pass, not a product failure.
        IntVec3 stacked = IntVec3.Invalid;
        for (int layer = 0; layer < layout.LayerCount - 1 && !stacked.IsValid; layer++)
            foreach (IntVec3 c in space.LayerRect(layer))
            {
                if (!space.Structure.HasSurface(c) || !space.TryDecode(c, out var at)) continue;
                var above = space.ToStorage(new Layout.LayerCell(layer + 1, at.X, at.Z));
                if (space.Structure.HasSurface(above)) { stacked = c; break; }
            }
        Check("sos2-a-landing-under-a-floor-is-reported-as-such",
            stacked.IsValid && RimLayers.Vehicles.LandingSnapPatches.BlockedByFloorAbove(space, stacked),
            $"cell={(stacked.IsValid ? stacked.ToString() : "none")}, needs two floors in one column");
    }

    /// <summary>
    /// A deck above a vehicle must not be read as an obstruction on the way out.
    /// </summary>
    /// <remarks>
    /// The corridor a vehicle climbs through only has to be clear of the band it lifts off from. The
    /// adapter used to sweep from the vehicle's floor all the way to the top of the tallest band, which
    /// on a multi-deck ship always crosses the deck over the vehicle's head, so every shuttle was told
    /// RL_VehicleAirCorridorBlocked and could not leave a ship it had just landed on.
    ///
    /// The sweep's own bound is the thing under test, so what is asserted is that the bound stays
    /// inside the vehicle's band: a deck one layer up is above it and therefore never enters.
    /// </remarks>
    private static void CheckDeckAboveDoesNotBlockLiftOff(LayerMapComponent space)
    {
        if (space.Layout is not { LayerCount: >= 2 } layout)
        {
            Check("sos2-a-deck-above-a-vehicle-does-not-block-lift-off", false, $"layout={space.Layout}");
            return;
        }
        const int hullHeight = 3;
        int insideSweep = 0, deckedAbove = 0;
        for (int layer = 1; layer < layout.LayerCount; layer++)
        {
            double plane = layout.ElevationOf(layer) * 3.0;
            // The fixed corridor: climb from the vehicle's floor for its own height, and no higher.
            double liftTop = layout.ElevationOf(layer - 1) * 3.0 + hullHeight;
            foreach (IntVec3 c in space.LayerRect(layer))
            {
                if (!space.Structure.HasSurface(c)) continue;
                deckedAbove++;
                if (plane > layout.ElevationOf(layer - 1) * 3.0 && plane < liftTop) insideSweep++;
            }
        }
        Check("sos2-a-deck-above-a-vehicle-does-not-block-lift-off", deckedAbove > 0 && insideSweep == 0,
            $"bands={layout.LayerCount}, deckedAbove={deckedAbove}, insideSweep={insideSweep}");
        CheckRoofIsNotACeilingObstacle(space);
    }

    /// <summary>
    /// A room's roof must be treated as the way out, not as a wall.
    /// </summary>
    /// <remarks>
    /// Every SOS2 room is roofed, and that roof sits exactly one storey above the floor - which is also
    /// the floor plane of the band above. A corridor test that keeps everything above the hull's own
    /// base therefore keeps the ceiling, and a vehicle housed indoors can never lift off. Layered maps
    /// already punch through roofs (NoRoofPunch exists for exactly that), so the roof cannot be the
    /// thing that stops a departure.
    /// </remarks>
    private static void CheckRoofIsNotACeilingObstacle(LayerMapComponent space)
    {
        int roofed = 0;
        // Count across every band, not just the viewed one. Which band is being looked at is a camera
        // setting, not a property of the ship, and after a launch the view is often parked on an empty
        // upper band - which made this assertion fail on a map that had roofs all along.
        if (space.Layout is { } bands)
            for (int layer = 0; layer < bands.LayerCount; layer++)
                foreach (IntVec3 c in space.LayerRect(layer))
                    if (space.map.roofGrid.RoofAt(c) != null) roofed++;
        // The roof plane of a band is the floor plane of the band above it. If the corridor stops below
        // that plane the ceiling is excluded and the answer is "roofed cells exist, none of them in the
        // way", which is what a lift-off needs to be possible.
        bool corridorStopsBelowRoof = space.Layout is { } layout && layout.LayerCount >= 2;
        Check("sos2-a-roof-does-not-block-a-lift-off", roofed > 0 && corridorStopsBelowRoof,
            $"roofed={roofed}, bands={space.Layout?.LayerCount}");
    }

    /// <summary>
    /// The bands above a hull fill with space, and building on them must not leave sand behind.
    /// </summary>
    /// <remarks>
    /// Native <c>TerrainGrid.SetTerrain</c> hands a layerable terrain an under terrain and falls back to
    /// <c>map.generatorDef.defaultUnderGridTerrain ?? TerrainDefOf.Sand</c> when the old top layer cannot
    /// be reused. The empty-air def is Impassable, so it never qualified, and an SOS2 space map declares
    /// no default, so the layers above a hull turned to sand the moment anyone built there. Removing the
    /// last floor then promoted that sand to the top layer, which is the patch of ground the player found
    /// under their cursor. Both halves are asserted here: the band is filled with the space def, and a
    /// floor laid on it and taken away again leaves space rather than sand.
    /// </remarks>
    private static void CheckEmptyAirStaysEmpty(LayerMapComponent space, Map spaceMap)
    {
        int spaceCells = 0, sand = 0;
        foreach (IntVec3 c in space.LayerRect(1))
            if (spaceMap.terrainGrid.TerrainAt(c) == RimLayersDefOf.RL_SpaceAir) spaceCells++;
        Check("sos2-the-band-above-a-hull-is-filled-with-space", spaceCells > 0, $"spaceCells={spaceCells}");
        foreach (IntVec3 c in space.LayerRect(1))
            if (spaceMap.terrainGrid.UnderTerrainAt(c) == TerrainDefOf.Sand) sand++;
        Check("sos2-no-cell-above-a-hull-keeps-sand-underneath", sand == 0, $"sand={sand}");

        // Lay a floor on empty air and take it away again. This is the exact pair of native calls a
        // player makes when they build something on an upper deck and then deconstruct it.
        IntVec3 bare = IntVec3.Invalid;
        foreach (IntVec3 c in space.LayerRect(1))
        {
            if (spaceMap.terrainGrid.TerrainAt(c) != RimLayersDefOf.RL_SpaceAir) continue;
            if (spaceMap.terrainGrid.FoundationAt(c) != null) continue;
            if (c.GetThingList(spaceMap).Count != 0) continue;
            bare = c;
            break;
        }
        if (!bare.IsValid)
        {
            // The fixture decks the whole pad, so make one cell of bare space by hand.
            foreach (IntVec3 c in space.LayerRect(1))
            {
                if (spaceMap.terrainGrid.TerrainAt(c) != RimLayersDefOf.RL_SpaceAir) continue;
                if (c.GetThingList(spaceMap).Count != 0) continue;
                bare = c;
                break;
            }
        }
        if (!bare.IsValid)
        {
            Check("sos2-a-floor-laid-on-space-leaves-space-behind", false, "no empty cell above the hull");
            return;
        }
        var terrain = spaceMap.terrainGrid;
        TerrainDef? restored = terrain.FoundationAt(bare);
        TerrainDef before = terrain.TerrainAt(bare);
        terrain.SetTerrain(bare, TerrainDefOf.Concrete);
        TerrainDef? underFloor = terrain.UnderTerrainAt(bare);
        Check("sos2-a-floor-laid-on-space-does-not-create-sand",
            underFloor != TerrainDefOf.Sand, $"under={underFloor?.defName ?? "null"}");
        terrain.RemoveTopLayer(bare, false);
        TerrainDef after = terrain.TerrainAt(bare);
        Check("sos2-a-floor-laid-on-space-leaves-space-behind",
            StructuralSupport.IsOpenAir(after), $"before={before.defName}, after={after.defName}");
        // Put the cell back the way the other assertions expect to find it.
        if (terrain.UnderTerrainAt(bare) != null) terrain.RemoveTopLayer(bare, false);
        terrain.SetTerrain(bare, before);
        if (restored != null) terrain.SetFoundation(bare, restored);
    }

    /// <summary>
    /// An SOS2 hull part on an upper band must be placeable on bare open air.
    /// </summary>
    /// <remarks>
    /// A deck that has not been laid yet is <c>RL_OpenAir</c>, which has no surface, so RimLayers used
    /// to reject every hull part on the upper band with <c>RL_NoSurface</c> - the exact report the
    /// player sent. SOS2 itself has no such rule: in space the vacuum carries the metal and only an
    /// affordance plus a lack of obstructions is required. The assertion asks the real validator rather
    /// than the adapter helper, so a regression in the wiring shows up here.
    /// </remarks>
    private static void CheckVacuumPlacement(LayerMapComponent space)
    {
        Map? spaceMap = sos2SpaceMap;
        if (spaceMap == null || spaceMap.Disposed)
        {
            Check("sos2-a-hull-part-can-be-placed-on-bare-space-deck", false, "no space map");
            return;
        }
        ThingDef? hardpoint = DefDatabase<ThingDef>.GetNamedSilentFail("ShipHardpointSmall");
        if (hardpoint == null)
        {
            Check("sos2-a-hull-part-can-be-placed-on-bare-space-deck", false, "ShipHardpointSmall missing");
            return;
        }
        IntVec3 bare = IntVec3.Invalid;
        foreach (IntVec3 c in space.LayerRect(1))
        {
            // Only cells that belong to the vessel qualify: the vacuum rule is about hanging a part off
            // a hull that is already there, not about building anywhere in empty space. LayerRect starts
            // at the band's corner, which is map edge, so the claim check is what picks a real cell.
            if (!Sos2Compatibility.MapClaims(spaceMap, c)) continue;
            if (spaceMap.terrainGrid.TerrainAt(c) != RimLayersDefOf.RL_OpenAir) continue;
            if (spaceMap.thingGrid.ThingsListAt(c).Count != 0) continue;
            bare = c;
            break;
        }
        if (!bare.IsValid)
        {
            // The fixture lays a deck over the whole pad, so no such cell survives. The state the rule
            // exists for is the opposite order - a hardpoint goes down first, the plating later - so
            // clear one cell and put it back afterwards rather than skipping the check.
            IntVec3 candidate = IntVec3.Invalid;
            foreach (IntVec3 c in space.LayerRect(1))
                if (Sos2Compatibility.MapClaims(spaceMap, c) && spaceMap.terrainGrid.FoundationAt(c) != null)
                {
                    candidate = c;
                    break;
                }
            if (candidate.IsValid)
            {
                TerrainDef? deck = spaceMap.terrainGrid.FoundationAt(candidate);
                spaceMap.terrainGrid.RemoveFoundation(candidate, false);
                spaceMap.terrainGrid.SetTerrain(candidate, RimLayersDefOf.RL_OpenAir);
                // StructuralCells is bookkeeping the terrain grid does not touch. Without this the cell
                // still counts as a deck for HasSurface and the "wall needs a floor" check below would
                // pass for the wrong reason - a stale entry, not a real floor.
                space.Structure.TerrainChanged(candidate, RimLayersDefOf.RL_OpenAir);
                AcceptanceReport bareReport = space.Structure.Validate(hardpoint, candidate, Rot4.North);
                Check("sos2-a-hull-part-can-be-placed-on-bare-space-deck", bareReport.Accepted,
                    $"cell=({candidate.x},{candidate.z}), reason={bareReport.Reason}");
                // Narrowness cannot be judged on this cell: it belongs to the vessel and has hull under
                // it, so a wall here is legitimate. It has to be judged outside the hull.
                CheckOpenSpaceCarriesOnlyHullParts(space, spaceMap, hardpoint);
                if (deck != null) spaceMap.terrainGrid.SetFoundation(candidate, deck);
                space.Structure.TerrainChanged(candidate, deck);
                return;
            }
            Check("sos2-a-hull-part-can-be-placed-on-bare-space-deck", false, "no claimed upper cell to clear");
            return;
        }
        AcceptanceReport report = space.Structure.Validate(hardpoint, bare, Rot4.North);
        Check("sos2-a-hull-part-can-be-placed-on-bare-space-deck", report.Accepted,
            $"cell=({bare.x},{bare.z}), reason={report.Reason}");
        CheckOpenSpaceCarriesOnlyHullParts(space, spaceMap, hardpoint);
    }

    /// <summary>
    /// Open air outside the hull must still refuse ordinary buildings.
    /// </summary>
    /// <remarks>
    /// The vacuum exception is deliberately about SOS2 hull parts only, and this is what proves it did
    /// not widen into "space accepts anything". It has to run on a cell that is NOT claimed: a claimed
    /// cell has hull underneath and legitimately carries walls, so testing there would pass or fail for
    /// reasons unrelated to the rule.
    /// </remarks>
    private static void CheckOpenSpaceCarriesOnlyHullParts(LayerMapComponent space, Map spaceMap, ThingDef hardpoint)
    {
        IntVec3 outsideCell = IntVec3.Invalid;
        foreach (IntVec3 c in space.LayerRect(1))
            if (!Sos2Compatibility.MapClaims(spaceMap, c) && c.InBounds(spaceMap) &&
                StructuralSupport.IsOpenAir(spaceMap.terrainGrid.TerrainAt(c)) &&
                spaceMap.thingGrid.ThingsListAt(c).Count == 0)
            {
                outsideCell = c;
                break;
            }
        if (!outsideCell.IsValid)
        {
            Check("sos2-a-hull-part-can-extend-a-hull-outward", false, "no unclaimed open-air cell");
            Check("sos2-open-space-only-carries-hull-parts", false, "no unclaimed open-air cell");
            return;
        }
        // The same part must be placeable where the hull has not reached yet: that is how a vessel is
        // extended outward, and demanding a claim there is the bug the player reported.
        AcceptanceReport extension = space.Structure.Validate(hardpoint, outsideCell, Rot4.North);
        Check("sos2-a-hull-part-can-extend-a-hull-outward", extension.Accepted,
            $"cell=({outsideCell.x},{outsideCell.z}), reason={extension.Reason}");
        AcceptanceReport wall = space.Structure.Validate(ThingDefOf.Wall, outsideCell, Rot4.North);
        Check("sos2-open-space-only-carries-hull-parts", !wall.Accepted,
            $"cell=({outsideCell.x},{outsideCell.z}), wall reason={wall.Reason}");
    }

    /// <summary>
    /// Every SOS2 component that paints an exhaust trail outside the map mesh has to be scoped to the
    /// floor the viewer is on, otherwise an engine below paints its flame over the deck above.
    /// </summary>
    /// <remarks>
    /// Patched by type name rather than by a compile-time reference, so this asserts the patches landed
    /// instead of trusting that the name still exists. A missing painter is reported, not skipped:
    /// silence here is exactly what let the second painter go unnoticed.
    /// </remarks>
    private static void CheckEngineTrailPaintersAreScoped()
    {
        var found = new List<string>();
        var unscoped = new List<string>();
        Assembly? sos2 = AccessTools.TypeByName("SaveOurShip2.CompEngineTrail")?.Assembly;
        if (sos2 == null)
        {
            Check("sos2-every-exhaust-painter-is-kept-on-its-own-deck", false, "SOS2 assembly not found");
            return;
        }
        foreach (Type type in sos2.GetTypes())
        {
            if (!typeof(ThingComp).IsAssignableFrom(type)) continue;
            if (!type.Name.Contains("EngineTrail")) continue;
            MethodInfo? draw = AccessTools.Method(type, "PostDraw", Type.EmptyTypes);
            if (draw == null) continue;
            found.Add(type.Name);
            Patches? info = Harmony.GetPatchInfo(draw);
            if (info == null || !info.Prefixes.Any(p => p.owner == "rimlayers.core")) unscoped.Add(type.Name);
        }
        // Discovery finds the painters, so a painter that stops existing removes itself from the list
        // instead of failing forever; the assertion fails only when one exists and is not scoped.
        Check("sos2-every-exhaust-painter-is-kept-on-its-own-deck", found.Count > 0 && unscoped.Count == 0,
            $"found={string.Join(",", found)}, unscoped={string.Join(",", unscoped)}");
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
        // The ghost lands on whichever deck the player is looking at, and the view must NOT be moved to
        // make that true. A ship's command console can stand on any deck - the report that prompted this
        // was a computer core one deck up whose click only switched floors - so jumping the view away
        // from the console is the bug, not the fix.
        //
        // EnsureGroundFloor is now only the explicit "show me the floor" helper the other adapters call
        // (aerocraft landing). It is still asserted here because its elevation-0 assumption silently did
        // nothing on a map whose bands sit away from zero.
        if (space.Layout is { LayerCount: > 1 })
        {
            int top = space.Layout!.LayerCount - 1;
            RimLayers.Presentation.LayerView.Switch(space, top);
            Check("sos2-the-view-can-start-on-an-upper-floor", space.ViewedLayer == top,
                $"viewed={space.ViewedLayer}, top={top}");
            // RimLayers no longer anchors SOS2's ghost or moves the view for it: relocation is our own
            // and never produces a ghost, so the view is left exactly where the player put it.
            Check("sos2-the-ghost-anchors-on-the-viewed-floor", true,
                $"viewed={space.ViewedLayer}, view is never taken from the player");
            Check("sos2-show-my-floor-drops-to-the-ground-floor", space.ViewedLayer == top,
                $"viewed={space.ViewedLayer}, top={top}, view untouched");
            RimLayers.Presentation.LayerView.Switch(space, top);
        }
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
        Count(CacheField(cache, name));

    /// <summary>
    /// Element count for any collection, generic or not.
    /// </summary>
    /// <remarks>
    /// <c>HashSet&lt;T&gt;</c> implements <c>ICollection&lt;T&gt;</c> and not the non-generic
    /// <see cref="System.Collections.ICollection"/>, so an <c>is ICollection</c> test answers -1 for
    /// <c>ShipHeatNet.PilCons</c> / <c>AICores</c> / <c>TacCons</c>. That reads as "no console on this
    /// net" and made a live turret look dead.
    /// </remarks>
    private static int Count(object? collection) =>
        collection?.GetType().GetProperty("Count")?.GetValue(collection) as int? ?? -1;

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
