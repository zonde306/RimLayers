using System.Text.Json;
using RimLayers.Areas;
using RimLayers.Core;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

/// <summary>
/// Native area and zone integration on a layered map.
///
/// Every vanilla <c>Area</c> is one flat <c>BoolGrid</c> sized to the whole STORAGE map, and every
/// area/zone designator only tests <c>InBounds</c>. On a managed map that means a paint stroke can
/// land on another layer or in the stride isolation band, "Invert" flips the entire flat grid, and
/// the automatic home-area/roof setters grow fixed rectangles in storage space. This scenario drives
/// the real designators, the real areas and the real setters and asserts the layer gate holds.
/// </summary>
internal static partial class V1Integration
{
    // A three-tile-wide structural strip near the top of layer 1's band. It is the only floor up
    // there, so a vanilla 4-tile home bubble around a building inside it MUST spill onto open air.
    private static readonly CellRect areaStrip = new(70, 113, 3, 7);
    private static readonly CellRect areaRoom = new(52, 52, 5, 5);
    private static readonly IntVec3 areaPathTarget = new(62, 0, 62);
    private static Thing areaAnchor = null!;
    private static Zone_Stockpile areaZone = null!;
    private static Area_Allowed areaAllowed = null!;
    private static int areaStripSurfaces, areaHomeCells, areaInvertSurfaces;
    private static int areaHomeBuildExpected, areaHomeZoneExpected;
    private static int areaZoneId = -1, areaInvertedId = -1, areaAllowedId = -1;

    private static string AreaOffLayer => "RL_AreaOffLayer".Translate().ToString();
    private static string AreaNoSurface => "RL_NoSurface".Translate().ToString();

    private static int AreaSurfaces(CellRect rect)
    {
        int count = 0;
        foreach (var cell in rect) if (owner.Structure.HasSurface(At(1, cell.x, cell.z))) count++;
        return count;
    }

    /// <summary>
    /// Counts real floors in one layer's STORAGE band. <see cref="LayerMapComponent.LayerRect"/> is
    /// already offset into storage space, so the cells must not be passed through <c>At</c>, which
    /// would add the stride a second time and throw on the top band.
    /// </summary>
    private static int AreaSurfacesOfLayer(int layer)
    {
        int count = 0;
        foreach (var cell in owner.LayerRect(layer)) if (owner.Structure.HasSurface(cell)) count++;
        return count;
    }

    /// <summary>Counts true cells that are not a real floor of the expected layer.</summary>
    private static int AreaOffLayerCells(Area area, int layer)
    {
        int wrong = 0;
        foreach (var cell in area.ActiveCells)
            if (!owner.TryDecode(cell, out var logical) || logical.LayerId != layer || !owner.Structure.HasSurface(cell))
                wrong++;
        return wrong;
    }

    /// <summary>
    /// Counts true cells that decode to this layer but are not a real floor, e.g. open air. Vanilla
    /// "Invert" flips the whole flat grid, so any such cell turning true is the exact regression.
    /// </summary>
    private static int AreaNoSurfaceOn(Area area, int layer)
    {
        int wrong = 0;
        foreach (var cell in area.ActiveCells)
            if (owner.TryDecode(cell, out var logical) && logical.LayerId == layer && !owner.Structure.HasSurface(cell))
                wrong++;
        return wrong;
    }

    /// <summary>
    /// Cells vanilla's fixed rectangle would seed for this anchor, restricted to real floor on the
    /// anchor's own layer. Computed from first principles rather than from the strip rectangle, so
    /// the assertion does not silently depend on the fixture being the only floor up there.
    /// </summary>
    private static int ExpectedSpread(Thing anchor, int layer)
    {
        int anchorLayer = AreaLayerGate.LayerOf(owner, anchor.Position, -1);
        var rect = new CellRect(anchor.Position.x - anchor.RotatedSize.x / 2 - 4,
            anchor.Position.z - anchor.RotatedSize.z / 2 - 4,
            anchor.RotatedSize.x + 8, anchor.RotatedSize.z + 8);
        rect.ClipInsideMap(map);
        int count = 0;
        foreach (var cell in rect)
            if (owner.TryDecode(cell, out var logical) && logical.LayerId == anchorLayer &&
                owner.Structure.HasSurface(cell)) count++;
        return count;
    }

    private static void BuildAreaStrip()
    {
        foreach (var cell in areaStrip)
        {
            map.roofGrid.SetRoof(At(0, cell.x, cell.z), RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1, cell.x, cell.z), RimLayersDefOf.RL_StructuralFloor);
            map.fogGrid.Unfog(At(0, cell.x, cell.z));
            map.fogGrid.Unfog(At(1, cell.x, cell.z));
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        areaStripSurfaces = AreaSurfaces(areaStrip);
    }

    private static void BeginAreas()
    {
        BuildAreaStrip();
        areaStripSurfaces = AreaSurfaces(areaStrip);
        Check("areas-fixture-has-a-real-floor-strip-on-upper-layer", areaStripSurfaces == areaStrip.Area,
            $"strip={areaStrip.Area}, surfaces={areaStripSurfaces}");

        // Harmony binds patch arguments by name, and a mismatch throws at install time: the class is
        // skipped, the mod logs it once, and every gate it carried is silently gone. Nothing else in
        // the codebase would notice, so the whole suite fails loudly on any skipped patch class.
        Check("every-harmony-patch-class-installed", RimLayersMod.FailedPatchClasses.Count == 0,
            $"installed={RimLayersMod.PatchClassCount}, skipped={string.Join(", ", RimLayersMod.FailedPatchClasses)}");

        // ------------------------------------------------------------------
        // 1. Paint designators: only the viewed layer, and only real floor.
        // ------------------------------------------------------------------
        var homeExpand = new Designator_AreaHomeExpand();
        var zoneAdd = new Designator_ZoneAddStockpile_Resources();
        var roofAdd = new Designator_AreaBuildRoof();
        // The upper probe must be REAL floor: Designator_ZoneAddStockpile additionally rejects
        // impassable terrain, and only the strip (already unfogged by BuildAreaStrip) is floor on
        // layer 1. Open air is the negative probe and must be visible for vanilla to get that far.
        IntVec3 ground = At(0, 54, 54), upper = At(1, areaStrip.CenterCell.x, areaStrip.CenterCell.z);
        IntVec3 openAir = At(1, 80, 80);
        map.fogGrid.Unfog(openAir);
        IntVec3 band = new(54, 0, owner.Layout!.Height);

        LayerView.Switch(owner, 0);
        var report = homeExpand.CanDesignateCell(upper);
        Check("home-paint-rejects-other-layer-under-ground-view", !report.Accepted && report.Reason == AreaOffLayer, report.Reason);
        report = homeExpand.CanDesignateCell(band);
        Check("home-paint-rejects-stride-isolation-band", !report.Accepted && report.Reason == AreaOffLayer, report.Reason);
        Check("home-paint-accepts-viewed-ground-cell", homeExpand.CanDesignateCell(ground).Accepted);

        LayerView.Switch(owner, 1);
        report = homeExpand.CanDesignateCell(upper);
        Check("home-paint-accepts-viewed-upper-floor", report.Accepted, report.Reason);
        report = homeExpand.CanDesignateCell(openAir);
        Check("home-paint-rejects-open-air-on-viewed-layer", !report.Accepted && report.Reason == AreaNoSurface, report.Reason);
        report = homeExpand.CanDesignateCell(ground);
        Check("home-paint-rejects-ground-from-upper-view", !report.Accepted && report.Reason == AreaOffLayer, report.Reason);

        report = zoneAdd.CanDesignateCell(ground);
        Check("zone-paint-rejects-ground-from-upper-view", !report.Accepted && report.Reason == AreaOffLayer, report.Reason);
        report = zoneAdd.CanDesignateCell(upper);
        Check("zone-paint-accepts-viewed-upper-floor", report.Accepted, report.Reason);
        report = zoneAdd.CanDesignateCell(openAir);
        Check("zone-paint-rejects-open-air-on-viewed-layer", !report.Accepted && report.Reason == AreaNoSurface, report.Reason);

        report = roofAdd.CanDesignateCell(ground);
        Check("roof-paint-rejects-ground-from-upper-view", !report.Accepted && report.Reason == AreaOffLayer, report.Reason);
        report = roofAdd.CanDesignateCell(upper);
        Check("roof-paint-accepts-viewed-upper-floor", report.Accepted, report.Reason);
        report = roofAdd.CanDesignateCell(openAir);
        Check("roof-paint-rejects-open-air-on-viewed-layer", !report.Accepted && report.Reason == AreaNoSurface, report.Reason);

        // ------------------------------------------------------------------
        // 2. Invert: only the viewed layer's floor cells participate.
        // ------------------------------------------------------------------
        map.areaManager.TryMakeNewAllowed(out areaAllowed);
        areaAllowed.SetLabel("Areas invert");
        // The seed cell has to be a real upper floor, otherwise it is not in the "live" set the gate
        // iterates and a vanilla-style full-grid flip is indistinguishable from a correct flip.
        IntVec3 invertCell = At(1, areaStrip.minX, areaStrip.minZ);
        IntVec3 invertGround = At(0, 52, 52);
        areaAllowed[invertCell] = true;        // cleared by the invert
        areaAllowed[invertGround] = true;      // other layer: must survive the upper-layer invert
        areaInvertedId = areaAllowed.ID;
        areaInvertSurfaces = AreaSurfacesOfLayer(1);
        LayerView.Switch(owner, 1);
        areaAllowed.Invert();
        // Every layer-1 floor flips except the seeded one, plus the untouched layer-0 cell:
        // (surfaces - 1) + 1 == surfaces.
        Check("invert-rewrites-only-viewed-layer-floors",
            areaAllowed.TrueCount == areaInvertSurfaces && !areaAllowed[invertCell] &&
            AreaNoSurfaceOn(areaAllowed, 1) == 0,
            $"true={areaAllowed.TrueCount}, surfaces={areaInvertSurfaces}, open-air={AreaNoSurfaceOn(areaAllowed, 1)}, seeded={areaAllowed[invertCell]}");
        Check("invert-leaves-other-layers-and-band-untouched",
            areaAllowed[invertGround] && !areaAllowed[At(2, 58, 58)] && !areaAllowed[band]);

        // ------------------------------------------------------------------
        // 3. Automatic home area: stays on the anchor's layer and never leaves the floor.
        // ------------------------------------------------------------------
        areaAnchor = ThingMaker.MakeThing(ThingDefOf.Stool, ThingDefOf.WoodLog);
        GenSpawn.Spawn(areaAnchor, At(1, 71, 115), map);
        areaAnchor.SetFaction(Faction.OfPlayer);
        map.areaManager.Home.Clear();
        Find.PlaySettings.autoHomeArea = true;
        AutoHomeAreaMaker.MarkHomeAroundThing(areaAnchor);
        areaHomeCells = map.areaManager.Home.TrueCount;
        areaHomeBuildExpected = ExpectedSpread(areaAnchor, 1);
        Check("auto-home-around-building-keeps-real-upper-floor", areaHomeCells == areaHomeBuildExpected,
            $"home={areaHomeCells}, expected={areaHomeBuildExpected}");
        Check("auto-home-around-building-never-seeds-open-air-or-band",
            AreaOffLayerCells(map.areaManager.Home, 1) == 0,
            $"true={map.areaManager.Home.TrueCount}");

        Find.PlaySettings.autoHomeArea = false;
        map.areaManager.Home.Clear();
        AutoHomeAreaMaker.MarkHomeAroundThing(areaAnchor);
        Check("auto-home-honours-native-player-toggle", map.areaManager.Home.TrueCount == 0,
            $"true={map.areaManager.Home.TrueCount}");
        Find.PlaySettings.autoHomeArea = true;

        // ------------------------------------------------------------------
        // 4. Automatic home area around a new zone cell: same rule.
        // ------------------------------------------------------------------
        map.areaManager.Home.Clear();
        areaZone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager) { label = "Areas zone" };
        map.zoneManager.RegisterZone(areaZone);
        areaZone.AddCell(At(1, 71, 115));
        areaZoneId = areaZone.ID;
        areaHomeZoneExpected = ExpectedSpread(areaAnchor, 1);
        Check("auto-home-around-zone-cell-keeps-real-upper-floor",
            map.areaManager.Home.TrueCount == areaHomeZoneExpected,
            $"home={map.areaManager.Home.TrueCount}, expected={areaHomeZoneExpected}");
        Check("auto-home-around-zone-cell-never-seeds-open-air-or-band",
            AreaOffLayerCells(map.areaManager.Home, 1) == 0,
            $"true={map.areaManager.Home.TrueCount}");

        // ZoneDelete is the one native CanDesignateCell whose parameter is named `sq`, so its patch
        // binds only if the postfix uses that name; a mismatch throws and Harmony skips the whole
        // class, which looks exactly like "the gate works" until someone deletes across layers.
        var zoneDelete = new Designator_ZoneDelete();
        var zoneCell = areaZone.Cells[0];
        LayerView.Switch(owner, 0);
        report = zoneDelete.CanDesignateCell(zoneCell);
        Check("zone-delete-rejects-cross-layer-cell-under-ground-view",
            !report.Accepted && report.Reason == AreaOffLayer, report.Reason);
        Check("zone-delete-patch-class-is-installed-not-silently-skipped",
            !RimLayersMod.FailedPatchClasses.Contains(nameof(ZoneDeleteGate)),
            string.Join(", ", RimLayersMod.FailedPatchClasses));
        LayerView.Switch(owner, 1);
        report = zoneDelete.CanDesignateCell(zoneCell);
        Check("zone-delete-accepts-the-same-cell-from-its-own-layer", report.Accepted, report.Reason);

        // ------------------------------------------------------------------
        // 5. Automatic roof setter: it may add, but never off the room's floor.
        // ------------------------------------------------------------------
        map.areaManager.BuildRoof.Clear();
        foreach (var cell in areaRoom.EdgeCells)
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), At(2, cell.x, cell.z), map)
                .SetFaction(Faction.OfPlayer);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var room = At(2, 54, 54).GetRoom(map);
        map.autoBuildRoofAreaSetter.TryGenerateAreaFor(room);
        map.autoBuildRoofAreaSetter.ResolveQueuedGenerateRoofs();
        Check("auto-roof-runs-on-a-real-upper-room-and-keeps-its-cells",
            map.areaManager.BuildRoof.TrueCount > 0 &&
            map.areaManager.BuildRoof.ActiveCells.All(c => owner.TryDecode(c, out var l) && l.LayerId == 2 && owner.Structure.HasSurface(c)),
            $"true={map.areaManager.BuildRoof.TrueCount}");

        // ------------------------------------------------------------------
        // 6. Allowed area and layer crossings.
        //
        // Vanilla is NOT purely a cost here, and the distinction matters for a layered map:
        //   * PathGridJob.CostForCell adds Cost_OutsideAllowedArea (600) to out-of-area cells, so a
        //     route may pass through them; but
        //   * GenPath.ShouldNotEnterCell / IntVec3.IsForbidden(p) call InAllowedArea, so an
        //     out-of-area DESTINATION is refused outright.
        // A layer crossing is one flat step the native grid never prices, so the router must apply
        // the same rule at the landing: in-area destinations are allowed (and priced), out-of-area
        // ones are not reachable.
        // ------------------------------------------------------------------
        pawn.drafter.Drafted = false;
        pawn.jobs.StopAll();
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
        var upperTarget = At(2, areaPathTarget.x, areaPathTarget.z);
        int freeCost;
        using (var free = map.pathFinder.FindPathNow(pawn.Position, upperTarget, pawn))
        {
            Check("unrestricted-pawn-reaches-upper-floor-target", free.Found,
                free.Found ? free.TotalCost.ToString() : "not found");
            freeCost = (int)free.TotalCost;
        }

        // (a) The destination itself is inside the area: the crossing must stay usable, and the
        //     landing penalty must show up in the total cost rather than blocking the route.
        map.areaManager.TryMakeNewAllowed(out var wide);
        areaAllowedId = wide.ID;
        wide[pawn.Position] = true;
        wide[upperTarget] = true;
        // Include every landing the chosen route can touch so the only remaining difference from
        // the unrestricted query is the per-cell outside-area cost.
        foreach (var edge in owner.Connectors.MovementEdges(TraverseParms.For(pawn)))
            wide[edge.To] = true;
        Find.TickManager.DoSingleTick();
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = wide;
        int allowedCost = -1;
        using (var allowedPath = map.pathFinder.FindPathNow(pawn.Position, upperTarget, pawn))
        {
            if (allowedPath.Found) allowedCost = (int)allowedPath.TotalCost;
            Check("allowed-area-keeps-a-cross-layer-route-to-an-in-area-destination", allowedPath.Found,
                $"found={allowedPath.Found}, cost={allowedCost}, free={freeCost}");
        }
        // Vanilla prices EVERY visited cell (PathGridJob.IndexCost), so the in-area route is more
        // expensive than the unrestricted one by however many out-of-area cells it steps on. The
        // assertion that matters is that a crossing still exists and is priced, never that the
        // totals match; the extra must be a whole multiple of the native per-cell penalty.
        int extraCost = allowedCost - freeCost;
        Check("in-area-crossing-is-priced-in-whole-native-per-cell-penalties",
            extraCost > 0 && extraCost % PathGridJob.Cost_OutsideAllowedArea == 0,
            $"free={freeCost}, allowed={allowedCost}, extra={extraCost}, " +
            $"cells={extraCost / PathGridJob.Cost_OutsideAllowedArea}");

        // (b) The destination is outside the area: vanilla refuses it, so the cross-layer route
        //     must refuse it too instead of teleporting the pawn out of its area.
        map.areaManager.TryMakeNewAllowed(out var narrow);
        narrow[pawn.Position] = true;
        Find.TickManager.DoSingleTick();
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = narrow;
        using (var blocked = map.pathFinder.FindPathNow(pawn.Position, upperTarget, pawn))
            Check("out-of-area-cross-layer-destination-is-refused-like-vanilla",
                !blocked.Found && !pawn.CanReach(upperTarget, PathEndMode.OnCell, Danger.Deadly),
                $"found={blocked.Found}, canReach={pawn.CanReach(upperTarget, PathEndMode.OnCell, Danger.Deadly)}, " +
                $"targetInArea={narrow[upperTarget]}, targetForbidden={upperTarget.IsForbidden(pawn)}");
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;

        File.WriteAllText(Path.Combine(directory, "areas-baseline.json"), JsonSerializer.Serialize(new
        {
            pawn = pawn.ThingID, zone = areaZoneId,
            inverted = areaInvertedId, allowed = areaAllowedId,
            home = areaHomeCells, strip = areaInvertSurfaces
        }));
        GameDataSaveLoader.SaveGame("RimLayers_V1Areas");
        Check("saved-area-fixture-between-floors", true);

        LayerView.Switch(owner, 1);
        Find.Selector.ClearSelection();
        Find.Selector.Select(areaZone);
        map.areaManager.Home.MarkForDraw();
        Find.CameraDriver.SetRootPosAndSize(At(1, 71, 115).ToVector3Shifted(), 12);
        pendingScreenshot = Path.Combine(directory, mode + "-areas.png");
        screenshotHold = 3;
        stage = 11;
        ticks = 0;
    }

    private static void RestoreAreas()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "areas-baseline.json")));
        var data = baseline.RootElement;
        pawn = map.mapPawns.AllPawnsSpawned.Single(p => p.ThingID == data.GetProperty("pawn").GetString());
        int zoneId = data.GetProperty("zone").GetInt32();
        int invertedId = data.GetProperty("inverted").GetInt32();
        int allowedId = data.GetProperty("allowed").GetInt32();
        int surfaces = data.GetProperty("strip").GetInt32();

        areaZone = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Single(z => z.ID == zoneId);
        areaAllowed = map.areaManager.AllAreas.OfType<Area_Allowed>().Single(a => a.ID == invertedId);
        var wide = map.areaManager.AllAreas.OfType<Area_Allowed>().Single(a => a.ID == allowedId);
        Check("restart-keeps-real-upper-floor-zone", areaZone.label == "Areas zone" && areaZone.CellCount == 1 &&
            owner.TryDecode(areaZone.Cells[0], out var zoneCell) && zoneCell.LayerId == 1);
        // Vanilla re-runs AutoHomeAreaMaker.Notify_BuildingSpawned for every player building while
        // the map spawns, so the exact TrueCount is not a save/load invariant (it legitimately grows
        // when Find.PlaySettings.autoHomeArea is on). What must survive is the layer rule: no home
        // cell may sit off layer 1's real floor, and the zone cell must still be home.
        // The home area is legitimately MULTI-layer: vanilla re-seeds it around every player building
        // on its own layer during map spawn, so layer-2 walls add layer-2 home cells. The invariant is
        // per-cell: every true cell must decode and carry a real floor of its own layer, and no cell
        // may sit in the stride isolation band or on open air.
        int homeAir = 0, homeBand = 0;
        foreach (var cell in map.areaManager.Home.ActiveCells)
        {
            if (!owner.TryDecode(cell, out _)) { homeBand++; continue; }
            if (!owner.Structure.HasSurface(cell)) homeAir++;
        }
        Check("restart-keeps-home-area-on-the-real-upper-floor",
            map.areaManager.Home.TrueCount > 0 && homeAir == 0 && homeBand == 0 &&
            map.areaManager.Home[areaZone.Cells[0]],
            $"home={map.areaManager.Home.TrueCount}, openAir={homeAir}, isolationBand={homeBand}, " +
            $"zoneIsHome={map.areaManager.Home[areaZone.Cells[0]]}");
        // Group 2 deliberately seeds ONE layer-0 cell and asserts the upper-layer invert leaves it
        // alone, so exactly that cell is expected off-layer after a round trip. Anything else off
        // layer 1 means the invert gate leaked.
        var invertGround = At(0, 52, 52);
        int invAir = 0, invBand = 0, invOtherLayer = 0, invUnexpectedLayer = 0;
        foreach (var cell in areaAllowed.ActiveCells)
        {
            if (!owner.TryDecode(cell, out var logical)) { invBand++; continue; }
            if (logical.LayerId != 1)
            {
                invOtherLayer++;
                if (cell != invertGround) invUnexpectedLayer++;
                continue;
            }
            if (!owner.Structure.HasSurface(cell)) invAir++;
        }
        bool invertSeedCleared = !areaAllowed[At(1, areaStrip.minX, areaStrip.minZ)];
        Check("restart-keeps-inverted-allowed-area-off-open-air",
            invAir == 0 && invBand == 0 && invOtherLayer == 1 && invUnexpectedLayer == 0 &&
            invertSeedCleared && areaAllowed[invertGround],
            $"true={areaAllowed.TrueCount}, surfaces={surfaces}, openAir={invAir}, " +
            $"isolationBand={invBand}, otherLayer={invOtherLayer}, unexpectedLayer={invUnexpectedLayer}, " +
            $"seedCleared={invertSeedCleared}, keptSeededGround={areaAllowed[invertGround]}");
        Check("restart-keeps-both-allowed-areas-as-separate-entries",
            !ReferenceEquals(areaAllowed, wide) && areaAllowed.TrueCount != wide.TrueCount,
            $"inverted={areaAllowed.TrueCount}, wide={wide.TrueCount}");

        LayerView.Switch(owner, 1);
        Find.CameraDriver.SetRootPosAndSize(At(1, 71, 115).ToVector3Shifted(), 12);
        pendingScreenshot = Path.Combine(directory, mode + "-areas.png");
        screenshotHold = 3;
        stage = 11;
        ticks = 0;
    }
}
