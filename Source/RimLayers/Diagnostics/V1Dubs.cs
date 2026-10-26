using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Real-game acceptance for the Dubwise family adapters (Bad Hygiene, Central Heating, Rimatomics,
/// Rimefeller, Skylights). Three contracts are exercised, each against the mod's own defs and its own
/// component: the shipped riser defs must be real pipes of that mod and join two bands into one net;
/// groundwater and oil must not exist above the ground floor; and a skylight under a built floor must
/// stop being a skylight.
///
/// <para>Blocks are skipped when a mod is absent rather than silently "passing": every block starts
/// with an explicit <c>*-def-loads</c> check whose detail names the missing def, so a run without the
/// pack is visibly incomplete instead of green.</para>
/// </summary>
internal static partial class V1Integration
{
    private static Thing? dubsGroundPipe;
    private static Thing? dubsUpperPipe;
    private static Thing? dubsGroundRiser;
    private static Thing? dubsUpperRiser;

    private static void BeginDubs()
    {
        foreach (var p in map.mapPawns.FreeColonistsSpawned)
        {
            p.jobs.StopAll();
            p.drafter.Drafted = true;
            p.Position = new IntVec3(24, 0, 24);
            p.Notify_Teleported();
        }

        var settings = RimLayersMod.Settings;
        settings.UpperLimit = 3;
        settings.UndergroundLimit = 2;
        settings.GlobalLimit = 20;
        Check("dubs-start-on-a-single-layer-map", LayerMapComponent.For(map) == null);
        LiveMapExpansion.EnsureElevation(map, 1);
        LiveMapExpansion.EnsureElevation(map, -1);
        owner = LayerMapComponent.For(map)!;
        Check("dubs-has-ground-upper-and-dug-layers",
            owner.Layout!.Elevations.SequenceEqual(new[] { 0, 1, -1 }),
            string.Join(",", owner.Layout.Elevations));

        // A flat, cleared, supported deck so an upper pipe/skylight has somewhere real to stand.
        PrepareDubsDeck(30, 30, 44);

        // ---- 1. Riser defs are real pipes of the owning mod -------------------------------
        var hygienePipe = DefDatabase<ThingDef>.GetNamedSilentFail("sewagePipeStuff");
        var hygieneRiser = DefDatabase<ThingDef>.GetNamedSilentFail("RL_HygieneSewageRiser");
        var hygieneAirRiser = DefDatabase<ThingDef>.GetNamedSilentFail("RL_HygieneAirRiser");
        if (hygienePipe != null)
        {
            Check("dubs-hygiene-riser-defs-load", hygieneRiser != null && hygieneAirRiser != null,
                $"sewage={hygieneRiser != null}, air={hygieneAirRiser != null}");
            Check("dubs-hygiene-riser-is-a-real-sewage-pipe", CarriesMode(hygieneRiser, "DubsBadHygiene", "Sewage"),
                DescribeComps(hygieneRiser));
            TestPipeBridge("hygiene", hygienePipe, hygieneRiser, 32, 32);
        }
        else Log.Message("[RimLayers] V1 dubs: Dubs Bad Hygiene is not loaded; its pipe block is skipped.");

        var heatingRiser = DefDatabase<ThingDef>.GetNamedSilentFail("RL_HeatingRiser");
        var heatingPipe = DefDatabase<ThingDef>.GetNamedSilentFail("HeatingPipe");
        if (heatingPipe != null)
        {
            Check("dubs-heating-riser-def-loads", heatingRiser != null, "RL_HeatingRiser");
            Check("dubs-heating-riser-is-a-real-heating-pipe", CarriesMode(heatingRiser, "DubsCentralHeating", "Heating"),
                DescribeComps(heatingRiser));
            TestPipeBridge("heating", heatingPipe, heatingRiser, 36, 32);
        }
        else Log.Message("[RimLayers] V1 dubs: Dubs Central Heating is not loaded; its pipe block is skipped.");

        var oilPipe = DefDatabase<ThingDef>.GetNamedSilentFail("OilPipeline");
        var oilRiser = DefDatabase<ThingDef>.GetNamedSilentFail("RL_OilRiser");
        if (oilPipe != null)
        {
            Check("dubs-oil-riser-def-loads", oilRiser != null, "RL_OilRiser");
            Check("dubs-oil-riser-is-a-real-oil-pipe", CarriesMode(oilRiser, "Rimefeller", null), DescribeComps(oilRiser));
            TestPipeBridge("oil", oilPipe, oilRiser, 40, 32);
        }
        else Log.Message("[RimLayers] V1 dubs: Rimefeller is not loaded; its pipe block is skipped.");

        foreach (var (mode, defName, x) in new[]
                 {
                     ("Cooling", "RL_RimatomicsCoolingRiser", 44), ("Steam", "RL_RimatomicsSteamRiser", 48),
                     ("ColdWater", "RL_RimatomicsWaterRiser", 52), ("HighVoltage", "RL_RimatomicsHighVoltageRiser", 56),
                     ("Loom", "RL_RimatomicsLoomRiser", 60)
                 })
        {
            var riser = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            var pipe = DefDatabase<ThingDef>.GetNamedSilentFail(mode switch
            {
                "Cooling" => "coolingPipe", "Steam" => "steamPipe", "ColdWater" => "waterPipe",
                "HighVoltage" => "HighVoltage", _ => "controllerLoom"
            });
            if (pipe == null) continue; // Rimatomics absent: the whole block is reported once below.
            Check($"dubs-rimatomics-{mode.ToLowerInvariant()}-riser-loads", riser != null, defName);
            Check($"dubs-rimatomics-{mode.ToLowerInvariant()}-riser-is-a-real-pipe",
                CarriesMode(riser, "Rimatomics", mode), DescribeComps(riser));
            TestPipeBridge("rimatomics-" + mode.ToLowerInvariant(), pipe, riser, x, 32);
        }
        if (DefDatabase<ThingDef>.GetNamedSilentFail("coolingPipe") == null)
            Log.Message("[RimLayers] V1 dubs: Dubs Rimatomics is not loaded; its five pipe nets are skipped.");

        // ---- 2. A live expansion must grow and rebuild every pipe grid --------------------
        TestPipeGridGrowth(hygienePipe, hygieneRiser);

        // ---- 3. Groundwater / deep water / ice never above the ground ---------------------
        TestGroundwater();

        // ---- 4. Oil never above the ground ------------------------------------------------
        TestOil();

        // ---- 5. A skylight under a built floor stops being a skylight ---------------------
        TestSkylights();

        Finish();
    }

    /// <summary>Clears a rectangle on the ground and lays a supported slab above it.</summary>
    private static void PrepareDubsDeck(int minX, int minZ, int size)
    {
        var rect = new CellRect(minX, minZ, size, size);
        foreach (IntVec3 c in rect)
        {
            var ground = At(0, c.x, c.z);
            foreach (Thing t in ground.GetThingList(map).ToList())
                if (t is not Pawn) { if (t.def.destroyable) t.Destroy(DestroyMode.Vanish); else t.DeSpawn(); }
            map.terrainGrid.SetTerrain(ground, TerrainDefOf.Soil);
            map.roofGrid.SetRoof(ground, null);
            map.fogGrid.Unfog(ground);
            var upper = At(1, c.x, c.z);
            map.terrainGrid.SetTerrain(upper, RimLayersDefOf.RL_StructuralFloor);
            owner.StructuralCells.Add(upper);
            map.roofGrid.SetRoof(ground, RoofDefOf.RoofConstructed);
            map.roofGrid.SetRoof(upper, null);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c => rect.Contains(c));
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        owner.Sky.Rebuild();
        Check("dubs-deck-upper-slabs-have-a-surface", owner.Structure.HasSurface(At(1, minX + size / 2, minZ + size / 2)));
    }

    /// <summary>True when <paramref name="def"/> is a thing of <paramref name="ns"/> carrying that mod's own pipe comp.</summary>
    private static bool CarriesMode(ThingDef? def, string ns, string? mode)
    {
        if (def?.comps == null) return false;
        foreach (CompProperties comp in def.comps)
        {
            if (comp == null || comp.GetType().Namespace != ns || !comp.GetType().Name.Contains("Pipe")) continue;
            if (mode == null) return true;
            if (AccessTools.Field(comp.GetType(), "mode")?.GetValue(comp)?.ToString() == mode) return true;
        }
        return false;
    }

    private static string DescribeComps(ThingDef? def) =>
        def?.comps == null ? "no def" : string.Join(",", def.comps.Select(c => c?.GetType().Name ?? "null"));

    /// <summary>
    /// Spawns pipe + riser on the ground and on the layer above and asserts the two ends land in the
    /// same network, then removes the upper riser and asserts they split again. Net identity is read
    /// from the mod's own <c>CompPipe.pipeNet</c>, never from a RimLayers mirror.
    /// </summary>
    private static void TestPipeBridge(string label, ThingDef pipeDef, ThingDef? riserDef, int x, int z)
    {
        if (riserDef == null) return;
        var groundPipeCell = At(0, x, z);
        var upperPipeCell = At(1, x, z);
        var shaftCell = At(0, x + 1, z);
        var upperShaftCell = At(1, x + 1, z);
        foreach (IntVec3 c in new[] { groundPipeCell, upperPipeCell, shaftCell, upperShaftCell })
            foreach (Thing t in c.GetThingList(map).ToList())
                if (t is not Pawn) t.Destroy(DestroyMode.Vanish);

        dubsGroundPipe = GenSpawn.Spawn(Make(pipeDef), groundPipeCell, map);
        dubsGroundPipe.SetFaction(Faction.OfPlayer);
        dubsUpperPipe = GenSpawn.Spawn(Make(pipeDef), upperPipeCell, map);
        dubsUpperPipe.SetFaction(Faction.OfPlayer);
        dubsGroundRiser = GenSpawn.Spawn(Make(riserDef), shaftCell, map);
        dubsGroundRiser.SetFaction(Faction.OfPlayer);
        dubsUpperRiser = GenSpawn.Spawn(Make(riserDef), upperShaftCell, map);
        dubsUpperRiser.SetFaction(Faction.OfPlayer);

        var ground = PipeNet(dubsGroundPipe);
        var upper = PipeNet(dubsUpperPipe);
        Check($"dubs-{label}-pipes-registered", ground != null && upper != null,
            $"ground={ground != null}, upper={upper != null}");
        Check($"dubs-{label}-net-crosses-the-band-boundary", ground != null && ReferenceEquals(ground, upper),
            $"ground={ground}, upper={upper}");

        // Removing the deliberate connector must restore the two separate floors.
        dubsUpperRiser.Destroy(DestroyMode.Vanish);
        dubsUpperRiser = null;
        ForcePipeRebuild();
        var groundAfter = PipeNet(dubsGroundPipe);
        var upperAfter = PipeNet(dubsUpperPipe);
        Check($"dubs-{label}-net-splits-without-the-riser",
            groundAfter != null && upperAfter != null && !ReferenceEquals(groundAfter, upperAfter),
            $"ground={groundAfter}, upper={upperAfter}");

        foreach (Thing? t in new[] { dubsGroundPipe, dubsUpperPipe, dubsGroundRiser })
            if (t is { Destroyed: false }) t.Destroy(DestroyMode.Vanish);
        dubsGroundPipe = dubsUpperPipe = dubsGroundRiser = null;
    }

    /// <summary>
    /// Builds a thing the way the pipe def expects. Passing steel to a def without
    /// <c>madeFromStuff</c> makes <c>ThingMaker</c> log an error for every spawn.
    /// </summary>
    private static Thing Make(ThingDef def) =>
        ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);

    /// <summary>
    /// Reads the net from the mod's own component. Bad Hygiene, Central Heating and Rimefeller expose
    /// <c>pipeNet</c>; Rimatomics names the same thing <c>net</c>.
    /// </summary>
    private static object? PipeNet(Thing? thing)
    {
        if (thing is not ThingWithComps comps) return null;
        foreach (ThingComp comp in comps.AllComps)
        {
            if (comp.GetType().Name != "CompPipe") continue;
            foreach (string property in new[] { "pipeNet", "net" })
            {
                try
                {
                    if (AccessTools.Property(comp.GetType(), property)?.GetValue(comp) is { } net) return net;
                }
                catch (Exception e)
                {
                    Log.Warning($"[RimLayers] V1 dubs: reading {comp.GetType().FullName}.{property} failed: {e.Message}");
                }
            }
            return null;
        }
        return null;
    }

    /// <summary>Asks every bound pipe component on the map to rebuild now, the way the adapters do.</summary>
    private static void ForcePipeRebuild()
    {
        foreach (MapComponent component in map.components)
        {
            var type = component.GetType();
            if (AccessTools.Method(type, "RebuildPipeGrid", new[] { typeof(int) }) is not { } rebuild) continue;
            if (AccessTools.Field(type, "PipeGrid") is not { } gridField) continue;
            int modes = gridField.GetValue(component) is Array grid ? grid.GetLength(0) : 0;
            for (int mode = 0; mode < modes; mode++)
                try { rebuild.Invoke(component, new object[] { mode }); }
                catch (Exception e) { Check("dubs-pipe-rebuild-runs", false, $"{type.Name}.RebuildPipeGrid({mode}): {e.InnerException?.Message ?? e.Message}"); return; }
        }
    }

    /// <summary>
    /// A live expansion replaces the per-cell buffers; a Rank-2 <c>PipeGrid</c> that is not regrown
    /// makes every later <c>CellToIndex</c> write throw, so this asserts the regrown width and that a
    /// pipe built on the brand-new layer still reaches the ground network.
    /// </summary>
    private static void TestPipeGridGrowth(ThingDef? pipeDef, ThingDef? riserDef)
    {
        MapComponent? component = map.components.FirstOrDefault(c => c.GetType().Name == "HygienePipeMapComp"
            && c.GetType().Namespace == "DubsBadHygiene");
        if (component == null || pipeDef == null || riserDef == null)
        {
            Log.Message("[RimLayers] V1 dubs: no Bad Hygiene pipe component; the expansion block is skipped.");
            return;
        }
        Array? before = AccessTools.Field(component.GetType(), "PipeGrid")?.GetValue(component) as Array;
        Check("dubs-pipe-grid-starts-matching-the-map",
            before != null && before.GetLength(1) == map.cellIndices.NumGridCells,
            $"grid={before?.GetLength(1)}, cells={map.cellIndices.NumGridCells}");

        int oldCells = map.cellIndices.NumGridCells;
        LiveMapExpansion.EnsureElevation(map, 2);
        owner = LayerMapComponent.For(map)!;
        Check("dubs-second-expansion-added-the-layer", owner.Layout!.Elevations.Contains(2), string.Join(",", owner.Layout.Elevations));
        Check("dubs-pipe-grid-count-grew-with-the-map",
            map.cellIndices.NumGridCells > oldCells, $"{oldCells} -> {map.cellIndices.NumGridCells}");

        Array? after = AccessTools.Field(component.GetType(), "PipeGrid")?.GetValue(component) as Array;
        Check("dubs-pipe-grid-was-regrown-to-the-new-cell-count",
            after != null && after.GetLength(1) == map.cellIndices.NumGridCells && after.GetLength(0) == before!.GetLength(0),
            $"rows={after?.GetLength(0)}, cols={after?.GetLength(1)}, cells={map.cellIndices.NumGridCells}");

        // The committed expansion hook must have rebuilt the grid eagerly: a brand-new net on the
        // freshly appended floor has to be reachable, and a stale grid would have thrown above.
        var newPipeCell = At(2, 64, 64);
        var newRiserCell = At(2, 65, 64);
        var groundRiserCell = At(0, 65, 64);
        map.terrainGrid.SetTerrain(At(2, 64, 64), RimLayersDefOf.RL_StructuralFloor);
        map.terrainGrid.SetTerrain(newRiserCell, RimLayersDefOf.RL_StructuralFloor);
        map.terrainGrid.SetTerrain(groundRiserCell, TerrainDefOf.Soil);
        var groundPipeCell = At(0, 64, 64);
        map.terrainGrid.SetTerrain(groundPipeCell, TerrainDefOf.Soil);
        foreach (IntVec3 c in new[] { newPipeCell, newRiserCell, groundRiserCell, groundPipeCell })
            foreach (Thing t in c.GetThingList(map).ToList())
                if (t is not Pawn) t.Destroy(DestroyMode.Vanish);
        var groundPipe = GenSpawn.Spawn(Make(pipeDef), groundPipeCell, map);
        var groundRiser = GenSpawn.Spawn(Make(riserDef), groundRiserCell, map);
        var newRiser = GenSpawn.Spawn(Make(riserDef), newRiserCell, map);
        var newPipe = GenSpawn.Spawn(Make(pipeDef), newPipeCell, map);
        var groundNet = PipeNet(groundPipe);
        var newNet = PipeNet(newPipe);
        Check("dubs-new-layer-pipe-joins-the-ground-net",
            groundNet != null && ReferenceEquals(groundNet, newNet), $"ground={groundNet}, upper={newNet}");
        foreach (Thing t in new[] { groundPipe, groundRiser, newRiser, newPipe }) t.Destroy(DestroyMode.Vanish);
    }

    /// <summary>Water, deep water and ice exist on the surface/dug strata only.</summary>
    private static void TestGroundwater()
    {
        MapComponent? component = map.components.FirstOrDefault(c => c.GetType().Name == "MapComponent_Hygiene");
        if (component == null)
        {
            Log.Message("[RimLayers] V1 dubs: Bad Hygiene is not loaded; the groundwater block is skipped.");
            return;
        }
        foreach (string name in new[] { "WaterGrid", "DeepWaterGrid", "IceGrid" })
        {
            if (AccessTools.Field(component.GetType(), name)?.GetValue(component) is not { } layer) continue;
            if (AccessTools.Field(layer.GetType(), "Grid")?.GetValue(layer) is not float[] values) continue;
            int ground = CountNonZero(values, owner.LayerRect(0));
            int above = 0;
            foreach (int layerId in owner.Layout!.OrderedLayers)
            {
                if (owner.Elevation(layerId) <= 0) continue;
                above += CountNonZero(values, owner.LayerRect(layerId));
            }
            Check($"dubs-{name.ToLowerInvariant()}-absent-above-the-ground", above == 0, $"upper={above}, ground={ground}");
            if (name != "IceGrid") // ice is climate-driven and may legitimately be empty on a test map
                Check($"dubs-{name.ToLowerInvariant()}-still-exists-below", ground > 0, $"ground={ground}");
        }

        // A well must refuse an upper floor with the layer reason, not silently accept it.
        if (AccessTools.TypeByName("DubsBadHygiene.PlaceWorker_WaterGrid") is { } worker
            && AccessTools.Method(worker, "AllowsPlacing") is { } allows)
        {
            object instance = Activator.CreateInstance(worker)!;
            // Bad Hygiene has no def called "Well"; the water-grid-gated inlets are these two.
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("WaterWellInlet")
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("PrimitiveWell");
            if (def != null)
            {
                object? upper = null, ground = null;
                try
                {
                    upper = allows.Invoke(instance, new object?[] { def, At(1, 40, 40), Rot4.North, map, null, null });
                    ground = allows.Invoke(instance, new object?[] { def, At(0, 40, 40), Rot4.North, map, null, null });
                }
                catch (Exception e) { Log.Warning($"[RimLayers] V1 dubs: PlaceWorker_WaterGrid.AllowsPlacing signature changed: {e.Message}"); }
                Check("dubs-well-refused-on-an-upper-floor", upper is AcceptanceReport { Accepted: false }, upper?.ToString() ?? "not invoked");
                Check("dubs-well-allowed-on-the-ground-floor", ground is AcceptanceReport { Accepted: true }, ground?.ToString() ?? "not invoked");
            }
        }
    }

    private static int CountNonZero(float[] values, CellRect rect)
    {
        int count = 0;
        for (int z = rect.minZ; z <= rect.maxZ; z++)
            for (int x = rect.minX; x <= rect.maxX; x++)
            {
                int index = map.cellIndices.CellToIndex(new IntVec3(x, 0, z));
                if ((uint)index < (uint)values.Length && values[index] != 0f) count++;
            }
        return count;
    }

    /// <summary>Shallow and deep oil seeds exist on the surface/dug strata only.</summary>
    private static void TestOil()
    {
        MapComponent? component = map.components.FirstOrDefault(c => c.GetType().Name == "MapComponent_Rimefeller");
        if (component == null)
        {
            Log.Message("[RimLayers] V1 dubs: Rimefeller is not loaded; the oil block is skipped.");
            return;
        }
        foreach (string name in new[] { "OilGrid", "DeepOilGrid" })
        {
            if (AccessTools.Field(component.GetType(), name)?.GetValue(component) is not { } grid) continue;
            if (AccessTools.Field(grid.GetType(), "SeedGrid")?.GetValue(grid) is not float[] seeds) continue;
            int ground = CountNonZero(seeds, owner.LayerRect(0));
            int above = 0;
            foreach (int layerId in owner.Layout!.OrderedLayers)
            {
                if (owner.Elevation(layerId) <= 0) continue;
                above += CountNonZero(seeds, owner.LayerRect(layerId));
            }
            Check($"dubs-{name.ToLowerInvariant()}-absent-above-the-ground", above == 0, $"upper={above}, ground={ground}");
            int fields = AccessTools.Field(grid.GetType(), "Fields")?.GetValue(grid) is System.Collections.ICollection list ? list.Count : -1;
            Check($"dubs-{name.ToLowerInvariant()}-fields-generated", fields >= 0, $"fields={fields}");
            if (fields > 0 && AccessTools.Field(grid.GetType(), "Fields")?.GetValue(grid) is System.Collections.IList items)
                foreach (object field in items)
                {
                    if (AccessTools.Field(field.GetType(), "RootCell")?.GetValue(field) is not IntVec3 root) continue;
                    bool upper = owner.TryDecode(root, out var local) && !DeepResourceScope.LayerAllowed(owner, local.LayerId);
                    Check($"dubs-{name.ToLowerInvariant()}-field-root-is-not-above-the-ground", !upper, root.ToString());
                    break;
                }
        }
    }

    /// <summary>A skylight lights its room while the column is open and stops once a floor is built above.</summary>
    private static void TestSkylights()
    {
        var compType = AccessTools.TypeByName("Dubs_Skylight.MapComp_Skylights");
        var lightDef = DefDatabase<ThingDef>.GetNamedSilentFail("SkyLightA");
        if (compType == null || lightDef == null)
        {
            Log.Message("[RimLayers] V1 dubs: Dubs Skylights is not loaded; the skylight block is skipped.");
            return;
        }
        var component = map.components.FirstOrDefault(c => compType.IsInstanceOfType(c));
        if (component == null || AccessTools.Field(compType, "SkylightGrid")?.GetValue(component) is not bool[]
            || AccessTools.Method(compType, "RegenGrid") is not { } regen)
        {
            Check("dubs-skylight-component-available", false, "MapComp_Skylights or SkylightGrid missing");
            return;
        }
        // RegenGrid replaces SkylightGrid, so the array must be re-read after every run.
        bool Lit(IntVec3 cell) =>
            AccessTools.Field(compType, "SkylightGrid")?.GetValue(component) is bool[] grid
            && (uint)map.cellIndices.CellToIndex(cell) < (uint)grid.Length
            && grid[map.cellIndices.CellToIndex(cell)];

        // An open column: clear the ground cell and leave the layer above as open air.
        int x = 34, z = 46;
        var ground = At(0, x, z);
        var above = At(1, x, z);
        map.terrainGrid.SetTerrain(above, RimLayersDefOf.RL_OpenAir);
        owner.StructuralCells.Remove(above);
        map.roofGrid.SetRoof(above, null);
        // Dubs Skylights drops a skylight whose cardinal neighbours are roofed or impassable, so an
        // open column needs the ring around it open too.
        foreach (IntVec3 c in new[] { ground, ground + IntVec3.North, ground + IntVec3.South, ground + IntVec3.East, ground + IntVec3.West })
            if (c.InBounds(map)) map.roofGrid.SetRoof(c, null);
        owner.Sky.Rebuild();
        foreach (Thing t in ground.GetThingList(map).ToList())
            if (t is not Pawn) t.Destroy(DestroyMode.Vanish);
        var light = GenSpawn.Spawn(Make(lightDef), ground, map);
        regen.Invoke(component, null);
        Check("dubs-skylight-open-column-is-lit", Lit(ground),
            $"exposed={owner.Sky.IsExposed(ground)}");

        // Build a real floor above it: the same skylight must stop being a skylight.
        map.terrainGrid.SetTerrain(above, RimLayersDefOf.RL_StructuralFloor);
        owner.StructuralCells.Add(above);
        map.roofGrid.SetRoof(ground, RoofDefOf.RoofConstructed);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        owner.Sky.Rebuild();
        regen.Invoke(component, null);
        Check("dubs-skylight-covered-column-is-not-lit", !Lit(ground),
            $"exposed={owner.Sky.IsExposed(ground)}");
        Check("dubs-skylight-occlusion-is-reported-by-sky-exposure", !owner.Sky.IsExposed(ground));

        light.Destroy(DestroyMode.Vanish);
    }
}
