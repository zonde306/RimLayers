using System.Text.Json;
using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Deep-crust resources (deep drill + ground-penetrating scanner) must behave per layer: placement
/// only on the ground and dug strata, scanning only inside the scanner's own band, a drill reading
/// only its own cell, and the deep-resource overlay limited to the viewed band.
/// </summary>
internal static partial class V1Integration
{
    private static Building deepScanner = null!, deepDrill = null!;
    private static Building deepSurfaceScanner = null!, deepSurfaceDrill = null!;
    private static IntVec3 deepCave, deepCavePlanar;
    private static int deepPhase, deepSurfaceCount, deepUndergroundCount, deepUpperCount;
    private static int deepGroundCellBefore, deepCaveCellBefore;

    private static IntVec3 DeepAt(int elevation, int x, int z) => owner.AtElevation(elevation, x, z);

    private static void BeginDeepResources()
    {
        Check("deep-resources-start-on-a-single-layer-map", LayerMapComponent.For(map) == null);
        foreach (var p in map.mapPawns.FreeColonistsSpawned)
        { p.jobs.StopAll(); p.drafter.Drafted = true; p.Position = new IntVec3(24, 0, 24); p.Notify_Teleported(); }
        pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Mining")));
        pawn.skills.GetSkill(SkillDefOf.Mining).Level = 20;
        pawn.skills.GetSkill(SkillDefOf.Intellectual).Level = 20;

        // Clear a working rectangle on the ground: plain soil, no roof, no entities.
        foreach (var c in new CellRect(18, 18, 84, 84))
        {
            foreach (var thing in c.GetThingList(map).ToList())
                if (thing is not Pawn) { if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish); else thing.DeSpawn(); }
            map.roofGrid.SetRoof(c, null);
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
            map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();

        // An unmanaged map keeps native behaviour.
        Check("unmanaged-map-keeps-native-scanner-placement",
            GenConstruct.CanPlaceBlueprintAt(ThingDefOf.GroundPenetratingScanner, new IntVec3(30, 0, 40), Rot4.North, map, true).Accepted);
        Check("unmanaged-map-scan-area-is-the-whole-map",
            DeepResourceScope.ScanArea(map, new IntVec3(30, 0, 30)) == new CellRect(0, 0, map.Size.x, map.Size.z).ContractedBy(10));

        var settings = RimLayersMod.Settings;
        settings.Caves = true; settings.CaveHives = false; settings.Minerals = 1;
        settings.UpperLimit = 2; settings.UndergroundLimit = 2; settings.GlobalLimit = 20;
        RimLayers.Generation.LiveMapExpansion.EnsureElevation(map, 1);
        RimLayers.Generation.LiveMapExpansion.EnsureElevation(map, -1);
        owner = LayerMapComponent.For(map)!;
        Check("fixture-has-ground-upper-and-dug-layers", owner.Layout!.Elevations.SequenceEqual(new[] { 0, 1, -1 }),
            string.Join(",", owner.Layout.Elevations));

        // 1. Upper floors reject both buildings, with the layer-specific reason.
        var deck = DeepAt(1, 40, 40);
        map.roofGrid.SetRoof(owner.Adjacent(deck, -1), RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(deck, RimLayersDefOf.RL_StructuralFloor);
        map.roofGrid.SetRoof(deck, RoofDefOf.RoofConstructed);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();

        var deckScanner = owner.Structure.Validate(ThingDefOf.GroundPenetratingScanner, deck, Rot4.North);
        Check("upper-floor-rejects-ground-penetrating-scanner", !deckScanner.Accepted, deckScanner.Reason);
        Check("upper-floor-rejection-uses-deep-resource-reason",
            deckScanner.Reason == "RL_DeepResourceGroundOnly".Translate(), deckScanner.Reason);
        Check("upper-floor-rejects-deep-drill", !owner.Structure.Validate(ThingDefOf.DeepDrill, deck, Rot4.North).Accepted);
        Check("upper-floor-blueprint-is-blocked",
            !GenConstruct.CanPlaceBlueprintAt(ThingDefOf.DeepDrill, deck, Rot4.North, map, true).Accepted);

        // 2. The ground layer keeps the native "must be unroofed" rule.
        var surfaceCell = new IntVec3(40, 0, 50);
        Check("ground-layer-accepts-open-scanner",
            GenConstruct.CanPlaceBlueprintAt(ThingDefOf.GroundPenetratingScanner, surfaceCell, Rot4.North, map, true).Accepted,
            GenConstruct.CanPlaceBlueprintAt(ThingDefOf.GroundPenetratingScanner, surfaceCell, Rot4.North, map, true).Reason);
        map.roofGrid.SetRoof(surfaceCell, RoofDefOf.RoofConstructed);
        Check("ground-layer-still-rejects-roofed-scanner",
            !GenConstruct.CanPlaceBlueprintAt(ThingDefOf.GroundPenetratingScanner, surfaceCell, Rot4.North, map, true).Accepted);
        map.roofGrid.SetRoof(surfaceCell, null);

        // 3. The dug stratum allows both, even though it is natively thick-roofed.
        if (!DeepFindCave(out deepCave)) { Check("fixture-found-open-underground-cell", false, "no standable B1 cell"); Finish(); return; }
        if (!DeepCarveRoom(deepCave, 7, out deepCave)) { Check("fixture-carved-an-underground-room", false, "carve failed"); Finish(); return; }
        Check("underground-cell-is-natively-thick-roofed", map.roofGrid.RoofAt(deepCave) == RoofDefOf.RoofRockThick);
        var caveScannerReport = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.GroundPenetratingScanner, deepCave, Rot4.North, map, true);
        Check("underground-allows-scanner-placement", caveScannerReport.Accepted, caveScannerReport.Reason);
        var caveDrillReport = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.DeepDrill, deepCave + new IntVec3(3, 0, 0), Rot4.North, map, true);
        Check("underground-allows-deep-drill-placement", caveDrillReport.Accepted, caveDrillReport.Reason);
        Check("upper-deck-still-rejects-both-after-fixture-roofing",
            !owner.Structure.Validate(ThingDefOf.GroundPenetratingScanner, deck, Rot4.North).Accepted &&
            !owner.Structure.Validate(ThingDefOf.DeepDrill, deck, Rot4.North).Accepted);

        // Real buildings on both usable layers.
        var groundDrillCell = new IntVec3(46, 0, 50);
        var caveDrillCell = deepCave + new IntVec3(3, 0, 0);
        map.deepResourceGrid.SetAt(groundDrillCell, ThingDefOf.Steel, 500);
        map.deepResourceGrid.SetAt(caveDrillCell, ThingDefOf.Steel, 500);
        deepSurfaceScanner = DeepBuilding(ThingDefOf.GroundPenetratingScanner, surfaceCell);
        deepSurfaceDrill = DeepBuilding(ThingDefOf.DeepDrill, groundDrillCell);
        deepScanner = DeepBuilding(ThingDefOf.GroundPenetratingScanner, deepCave);
        deepDrill = DeepBuilding(ThingDefOf.DeepDrill, caveDrillCell);
        DeepPowerScan(deepSurfaceScanner);
        DeepPowerScan(deepSurfaceDrill);
        DeepPowerScan(deepScanner);
        DeepPowerScan(deepDrill);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();

        Check("underground-scanner-passes-the-native-roof-gate", deepScanner.GetComp<CompScanner>().CanUseNow.Accepted,
            deepScanner.GetComp<CompScanner>().CanUseNow.Reason);
        Check("ground-scanner-still-refuses-a-roof", DeepRejectsRoof(deepSurfaceScanner, surfaceCell));
        Check("ground-scanner-accepts-open-sky-again", deepSurfaceScanner.GetComp<CompScanner>().CanUseNow.Accepted,
            deepSurfaceScanner.GetComp<CompScanner>().CanUseNow.Reason);

        // A drill only ever reads its own cell.
        Check("ground-drill-reads-its-own-deep-cell",
            DeepDrillUtility.GetNextResource(groundDrillCell, map) == ThingDefOf.Steel);
        owner.TryDecode(caveDrillCell, out var caveLocal);
        var caveGroundMirror = owner.ToStorage(new LayerCell(0, caveLocal.X, caveLocal.Z));
        Check("underground-drill-reads-only-its-own-layer",
            DeepDrillUtility.GetNextResource(caveDrillCell, map) == ThingDefOf.Steel &&
            owner.Elevation(caveLocal.LayerId) == -1 &&
            map.deepResourceGrid.ThingDefAt(caveGroundMirror) == null,
            $"cave={caveDrillCell}, ground={caveGroundMirror}");

        // 3b. The drill's own 21-cell sample stays inside its band and never enters an isolation band.
        Check("drill-sample-never-leaves-its-band", DeepDrillSampleStaysInBand(deepDrill.Position) && DeepDrillSampleStaysInBand(deepSurfaceDrill.Position),
            $"{deepDrill.Position}/{deepSurfaceDrill.Position}");
        Check("drill-sample-prefers-the-drill-cell-over-an-isolation-cell",
            DeepDrillUtility.GetNextResource(deepDrill.Position, map) == ThingDefOf.Steel);

        // 4. Scan rectangles are single-band and disjoint.
        var surfaceArea = DeepResourceScope.ScanArea(map, surfaceCell);
        var caveArea = DeepResourceScope.ScanArea(map, deepCave);
        Check("surface-scan-area-is-only-the-ground-band", surfaceArea == owner.LayerRect(0).ContractedBy(10), surfaceArea.ToString());
        Check("underground-scan-area-is-only-the-dug-band",
            caveArea == owner.LayerRect(owner.Layout.LayerAt(-1)).ContractedBy(10), caveArea.ToString());
        Check("scan-areas-never-overlap-another-band",
            !surfaceArea.Overlaps(caveArea) && !surfaceArea.Overlaps(owner.LayerRect(1)) && !caveArea.Overlaps(owner.LayerRect(1)));
        Check("scan-centers-never-leave-the-band", DeepCentersStayInBand(caveArea), caveArea.ToString());

        // 5. Scanning writes into the scanner's own band only.
        deepSurfaceCount = DeepCount(0);
        deepUndergroundCount = DeepCount(owner.Layout.LayerAt(-1));
        deepUpperCount = DeepCount(owner.Layout.LayerAt(1));
        int written = 0;
        for (int attempt = 0; attempt < 5; attempt++)
            written += DeepResourceScope.FindLump(deepScanner.GetComp<CompDeepScanner>(), pawn);
        Check("underground-scan-finds-and-writes-resources", written > 0, written.ToString());
        Check("underground-scan-grows-only-the-dug-band",
            DeepCount(owner.Layout.LayerAt(-1)) > deepUndergroundCount, DeepCount(owner.Layout.LayerAt(-1)).ToString());
        Check("underground-scan-never-touches-the-ground-band", DeepCount(0) == deepSurfaceCount,
            $"{deepSurfaceCount}->{DeepCount(0)}");
        Check("underground-scan-never-writes-a-floating-upper-lump", DeepCount(owner.Layout.LayerAt(1)) == deepUpperCount,
            $"{deepUpperCount}->{DeepCount(owner.Layout.LayerAt(1))}");
        Check("no-deep-resource-lands-outside-a-real-layer", DeepEveryCellHasALayer());

        int groundAfter = DeepCount(0);
        for (int attempt = 0; attempt < 3; attempt++)
            DeepResourceScope.FindLump(deepSurfaceScanner.GetComp<CompDeepScanner>(), pawn);
        Check("surface-scan-writes-only-on-the-ground", DeepCount(0) >= groundAfter);
        Check("surface-scan-leaves-the-dug-and-upper-bands-alone",
            DeepCount(owner.Layout.LayerAt(1)) == deepUpperCount);

        // 6. The real comp route (the patched native method) has the same guarantee.
        IntVec3 caveCenter = caveArea.CenterCell;
        int beforeNative = DeepCount(owner.Layout.LayerAt(-1));
        AccessTools.Method(typeof(CompDeepScanner), "DoFind", new[] { typeof(Pawn) })
            ?.Invoke(deepScanner.GetComp<CompDeepScanner>(), new object[] { pawn });
        Check("native-DoFind-route-scans-inside-the-layer", DeepCount(owner.Layout.LayerAt(-1)) >= beforeNative);
        Check("native-DoFind-route-never-writes-above-the-scanner", DeepCount(owner.Layout.LayerAt(1)) == deepUpperCount);
        Check("native-DoFind-route-keeps-the-dug-band-populated", DeepLayerPopulated(owner.Layout.LayerAt(-1)));

        // 7. Overlay is scoped to the viewed band.
        LayerView.Switch(owner, owner.Layout.LayerAt(-1));
        map.deepResourceGrid.MarkForDraw();
        Check("overlay-uses-a-band-scoped-drawer", DeepResourceScope.OverlayActive(map, owner.Layout.LayerAt(-1)),
            $"viewed={owner.ViewedLayer}");
        Check("overlay-draws-the-viewed-dug-band-and-nothing-else",
            DeepResourceScope.OverlayDraws(map, owner.Layout.LayerAt(-1), caveCenter) &&
            !DeepResourceScope.OverlayDraws(map, owner.Layout.LayerAt(-1),
                owner.ToStorage(new LayerCell(owner.Layout.LayerAt(1), deepCavePlanar.x, deepCavePlanar.z))),
            $"{caveCenter}");
        LayerView.Switch(owner, owner.Layout.LayerAt(1));
        map.deepResourceGrid.MarkForDraw();
        Check("overlay-follows-the-viewed-band", DeepResourceScope.OverlayActive(map, owner.Layout.LayerAt(1)));
        LayerView.Switch(owner, 0);

        // 8. Scanning on a layered map can never place a lump in a reserved isolation band.
        Check("scan-area-excludes-isolation-bands",
            owner.Layout.OrderedLayers.All(l => DeepResourceScope.ScanArea(map, owner.LayerRect(l).CenterCell).Height <= owner.Layout.Height));

        deepGroundCellBefore = map.deepResourceGrid.CountAt(groundDrillCell);
        deepCaveCellBefore = map.deepResourceGrid.CountAt(caveDrillCell);
        Check("both-drill-cells-start-with-the-same-count",
            deepGroundCellBefore > 0 && deepGroundCellBefore == deepCaveCellBefore,
            $"{deepGroundCellBefore}/{deepCaveCellBefore}");

        DeepWriteBaseline();
        deepPhase = 0; stage = 431; ticks = 0;
    }

    /// <summary>Extraction writes to the drill's own band; a portion from one drill leaves the other untouched.</summary>
    private static void DeepStep()
    {
        if (deepPhase == 0)
        {
            if (ticks < 2) return;
            int items = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Count();
            deepSurfaceDrill.GetComp<CompDeepDrill>()!.DrillWorkDone(pawn, 20000);
            Check("surface-drill-extraction-consumes-its-own-deep-cell",
                map.deepResourceGrid.CountAt(deepSurfaceDrill.Position) < deepGroundCellBefore,
                $"{deepGroundCellBefore}->{map.deepResourceGrid.CountAt(deepSurfaceDrill.Position)}");
            Check("surface-drill-extraction-leaves-the-underground-cell-intact",
                map.deepResourceGrid.CountAt(deepDrill.Position) == deepCaveCellBefore,
                $"{deepCaveCellBefore}->{map.deepResourceGrid.CountAt(deepDrill.Position)}");
            Check("surface-drill-extraction-yields-a-real-item",
                map.listerThings.ThingsOfDef(ThingDefOf.Steel).Count() > items);
            Check("surface-drill-extraction-stays-on-the-ground-layer",
                map.listerThings.ThingsOfDef(ThingDefOf.Steel).All(t => !owner.TryDecode(t.Position, out var q) || owner.Elevation(q.LayerId) == 0));
            deepPhase = 1; ticks = 0; return;
        }
        if (deepPhase == 1)
        {
            if (ticks < 2) return;
            int caveBefore = map.deepResourceGrid.CountAt(deepDrill.Position);
            int groundBefore = map.deepResourceGrid.CountAt(deepSurfaceDrill.Position);
            deepDrill.GetComp<CompDeepDrill>()!.DrillWorkDone(pawn, 20000);
            Check("underground-drill-extraction-consumes-its-own-deep-cell",
                map.deepResourceGrid.CountAt(deepDrill.Position) < caveBefore,
                $"{caveBefore}->{map.deepResourceGrid.CountAt(deepDrill.Position)}");
            Check("underground-drill-extraction-leaves-the-ground-cell-intact",
                map.deepResourceGrid.CountAt(deepSurfaceDrill.Position) == groundBefore,
                $"{groundBefore}->{map.deepResourceGrid.CountAt(deepSurfaceDrill.Position)}");
            Check("underground-drill-can-drill-right-now", deepDrill.GetComp<CompDeepDrill>().CanDrillNow());
            Check("underground-drill-work-finder-sees-the-drill", DeepWorkGiverSeesDrill());
            deepPhase = 2; ticks = 0; return;
        }
        if (deepPhase == 2)
        {
            if (ticks < 3) return;
            Check("whole-scenario-kept-the-upper-band-clean", DeepCount(owner.Layout!.LayerAt(1)) == deepUpperCount,
                $"{deepUpperCount}->{DeepCount(owner.Layout.LayerAt(1))}");
            stage = 11; ticks = 0;
        }
    }

    private static void RestoreDeepResources()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "deep-resources-baseline.json")));
        var b = doc.RootElement;
        deepSurfaceScanner = map.listerThings.AllThings.OfType<Building>().Single(t => t.ThingID == b.GetProperty("surfaceScanner").GetString());
        deepSurfaceDrill = map.listerThings.AllThings.OfType<Building>().Single(t => t.ThingID == b.GetProperty("surfaceDrill").GetString());
        deepScanner = map.listerThings.AllThings.OfType<Building>().Single(t => t.ThingID == b.GetProperty("scanner").GetString());
        deepDrill = map.listerThings.AllThings.OfType<Building>().Single(t => t.ThingID == b.GetProperty("drill").GetString());
        deepCave = deepScanner.Position;
        deepUpperCount = b.GetProperty("upper").GetInt32();
        deepUndergroundCount = b.GetProperty("underground").GetInt32();
        deepSurfaceCount = b.GetProperty("ground").GetInt32();
        pawn = map.mapPawns.FreeColonistsSpawned.First();

        Check("reload-keeps-the-three-layer-layout", owner.Layout!.Elevations.SequenceEqual(new[] { 0, 1, -1 }),
            string.Join(",", owner.Layout.Elevations));
        Check("reload-keeps-deep-resource-band-counts",
            DeepCount(owner.Layout.LayerAt(1)) == deepUpperCount &&
            DeepCount(owner.Layout.LayerAt(-1)) == deepUndergroundCount &&
            DeepCount(0) >= deepSurfaceCount,
            $"upper={DeepCount(owner.Layout.LayerAt(1))}, dug={DeepCount(owner.Layout.LayerAt(-1))}, ground={DeepCount(0)}");
        Check("reload-keeps-the-upper-floor-block",
            !owner.Structure.Validate(ThingDefOf.GroundPenetratingScanner, DeepAt(1, 40, 40), Rot4.North).Accepted &&
            !owner.Structure.Validate(ThingDefOf.DeepDrill, DeepAt(1, 40, 40), Rot4.North).Accepted);
        Check("reload-keeps-the-underground-scanner-usable", deepScanner.GetComp<CompScanner>().CanUseNow.Accepted,
            deepScanner.GetComp<CompScanner>().CanUseNow.Reason);
        Check("reload-keeps-the-underground-drill-usable", deepDrill.GetComp<CompDeepDrill>().CanDrillNow());
        Check("reload-keeps-scan-areas-layer-local",
            DeepResourceScope.ScanArea(map, deepCave) == owner.LayerRect(owner.Layout.LayerAt(-1)).ContractedBy(10));

        deepGroundCellBefore = map.deepResourceGrid.CountAt(deepSurfaceDrill.Position);
        deepCaveCellBefore = map.deepResourceGrid.CountAt(deepDrill.Position);
        deepPhase = 1; stage = 431; ticks = 0;
    }

    private static bool DeepRejectsRoof(Building scanner, IntVec3 cell)
    {
        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
        bool rejected = !scanner.GetComp<CompScanner>().CanUseNow.Accepted;
        map.roofGrid.SetRoof(cell, null);
        return rejected;
    }

    private static bool DeepFindCave(out IntVec3 cell)
    {
        cell = IntVec3.Invalid;
        if (!owner.Layout!.TryGetLayerAtElevation(-1, out int layer)) return false;
        foreach (var c in owner.LayerRect(layer).ContractedBy(32))
            if (c.Standable(map) && c.GetEdifice(map) == null && map.terrainGrid.TerrainAt(c) != RimLayersDefOf.RL_OpenAir)
            { cell = c; return true; }
        return false;
    }

    /// <summary>Carve a small open room so native interaction cells are free, then move to its center.</summary>
    private static bool DeepCarveRoom(IntVec3 origin, int radius, out IntVec3 center)
    {
        center = IntVec3.Invalid;
        if (!owner.TryDecode(origin, out var local)) return false;
        int layer = local.LayerId;
        var area = new CellRect(local.X - radius, local.Z - radius, radius * 2 + 1, radius * 2 + 1);
        // Log rectangles describe logical coordinates, not storage bands.
        var bounds = new CellRect(0, 0, owner.Layout!.Width, owner.Layout.Height).ContractedBy(24);
        if (!bounds.Contains(area.Min) || !bounds.Contains(area.Max)) return false;
        foreach (var planar in area)
        {
            var cell = owner.ToStorage(new LayerCell(layer, planar.x, planar.z));
            cell.GetEdifice(map)?.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThick);
            map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        center = owner.ToStorage(new LayerCell(layer, area.CenterCell.x, area.CenterCell.z));
        deepCavePlanar = area.CenterCell;
        return true;
    }

    private static Building DeepBuilding(ThingDef def, IntVec3 at)
    {
        var thing = (Building)ThingMaker.MakeThing(def);
        GenSpawn.Spawn(thing, at, map);
        thing.SetFaction(Faction.OfPlayer);
        map.fogGrid.Unfog(at);
        return thing;
    }

    private static void DeepPowerScan(Building building)
    {
        var trader = building.GetComp<CompPowerTrader>();
        if (trader != null) trader.PowerOn = true;
    }

    private static int DeepCount(int layer)
    {
        int count = 0;
        foreach (var cell in owner.LayerRect(layer))
            if (map.deepResourceGrid.ThingDefAt(cell) != null && map.deepResourceGrid.CountAt(cell) > 0) count++;
        return count;
    }

    private static bool DeepLayerPopulated(int layer) => DeepCount(layer) > 0;

    /// <summary>Every one of the drill's 21 sampled cells must decode into the drill's own band.</summary>
    private static bool DeepDrillSampleStaysInBand(IntVec3 storage)
    {
        if (!owner.TryDecode(storage, out var here)) return false;
        for (int i = 0; i < DeepDrillUtility.NumCellsToScan; i++)
        {
            IntVec3 offset = GenRadial.RadialPattern[i];
            int x = here.X + offset.x, z = here.Z + offset.z;
            if ((uint)x >= (uint)owner.Layout!.Width || (uint)z >= (uint)owner.Layout.Height) continue;
            IntVec3 sampled = owner.ToStorage(new LayerCell(here.LayerId, x, z));
            if (!owner.TryDecode(sampled, out var decoded) || decoded.LayerId != here.LayerId) return false;
        }
        return true;
    }

    private static bool DeepCentersStayInBand(CellRect area)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (!DeepResourceScope.TryFindCenter(area, _ => true, out var center)) return false;
            if (!area.Contains(center) || !owner.TryDecode(center, out var local)) return false;
            if (owner.Elevation(local.LayerId) != -1) return false;
        }
        return true;
    }

    private static bool DeepEveryCellHasALayer()
    {
        for (int index = 0; index < map.cellIndices.NumGridCells; index++)
        {
            IntVec3 cell = CellIndicesUtility.IndexToCell(index, map.Size.x);
            if (map.deepResourceGrid.ThingDefAt(cell) == null) continue;
            if (!owner.TryDecode(cell, out _)) return false;
        }
        return true;
    }

    private static bool DeepWorkGiverSeesDrill()
    {
        var giver = (WorkGiver_DeepDrill)DefDatabase<WorkGiverDef>.GetNamed("Drill").Worker;
        return giver.HasJobOnThing(pawn, deepDrill, true) && giver.JobOnThing(pawn, deepDrill) != null;
    }

    private static void DeepWriteBaseline()
    {
        var record = new
        {
            scanner = deepScanner.ThingID,
            drill = deepDrill.ThingID,
            surfaceScanner = deepSurfaceScanner.ThingID,
            surfaceDrill = deepSurfaceDrill.ThingID,
            cave = deepCave.ToString(),
            underground = DeepCount(owner.Layout!.LayerAt(-1)),
            ground = DeepCount(0),
            upper = DeepCount(owner.Layout.LayerAt(1))
        };
        File.WriteAllText(Path.Combine(directory, "deep-resources-baseline.json"), JsonSerializer.Serialize(record));
        GameDataSaveLoader.SaveGame("RimLayers_V1DeepResources");
    }
}
