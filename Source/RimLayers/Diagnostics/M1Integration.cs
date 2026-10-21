using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>Opt-in real-game acceptance harness; never active in ordinary sessions.</summary>
internal static class M1Integration
{
    private static bool active;
    private static string directory = "";
    private static string mode = "generate";
    private static int frame;
    private static bool loadPending;
    private static bool loadIssued;
    private static bool finished;
    private static Game? beforeLoad;
    private static readonly List<object> results = new();
    private static readonly List<string> errors = new();
    private static int failures;

    internal static void Configure()
    {
        active = GenCommandLine.CommandLineArgPassed("rimlayerstest") && GenCommandLine.CommandLineArgPassed("quicktest");
        if (!active) return;
        directory = Path.GetFullPath(GenFilePaths.SaveDataFolderPath);
        string artifacts = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(M1Integration).Assembly.Location)!, "..", "Tests", "Artifacts"));
        // Test code is prohibited from loading/writing a player's ordinary savedata folder.
        if (!string.Equals(new DirectoryInfo(directory).Parent?.FullName, artifacts, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("[RimLayers] Tests require RimLayers/Tests/Artifacts/<run> as -savedatafolder.");
        Directory.CreateDirectory(directory);
        if (GenCommandLine.TryGetCommandLineArg("rimlayersmode", out string value)) mode = value;
        if (mode != "generate" && mode != "load") throw new ArgumentException("Unknown test mode.");
        var h = new Harmony("rimlayers.core.m1test");
        h.Patch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
            postfix: new HarmonyMethod(typeof(M1Integration), nameof(Setup)));
        h.Patch(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)),
            postfix: new HarmonyMethod(typeof(M1Integration), nameof(Update)));
        h.Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
            postfix: new HarmonyMethod(typeof(M1Integration), nameof(OnGUI)));
        h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)), prefix: new HarmonyMethod(typeof(M1Integration), nameof(WatchDestroy)));
        h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
            postfix: new HarmonyMethod(typeof(M1Integration), nameof(Error)));
    }

    private static int destroyedLogs;
    private static void WatchDestroy(Thing __instance)
    {
        if (active && mode == "load" && loadIssued && __instance is Filth && __instance.Map?.uniqueID == 0 && destroyedLogs++ < 5)
            File.AppendAllText(Path.Combine(directory, "filth-destroy.log"), __instance.ThingID + "\n" + System.Environment.StackTrace + "\n");
    }

    private static void Error(string text) { if (active && !finished) errors.Add(text); }

    private static void Setup()
    {
        Find.GameInitData.mapSize = 120;
        if (GenCommandLine.CommandLineArgPassed("rimlayersriver"))
        {
            bool found = false;
            foreach (SurfaceTile tile in Find.WorldGrid.Tiles)
                if (!tile.Rivers.NullOrEmpty() && !tile.Roads.NullOrEmpty() &&
                    tile.PrimaryBiome == BiomeDefOf.TemperateForest && TileFinder.IsValidTileForNewSettlement(tile.tile))
                {
                    Find.GameInitData.startingTile = tile.tile;
                    found = true;
                    break;
                }
            Check("river-road-tile-found", found);
        }
        // Assertions do not assume generated terrain or Thing IDs.
    }

    private static void Check(string name, bool pass, string detail = "")
    {
        results.Add(new { name, pass, detail });
        if (!pass) failures++;
        Log.Message($"[RimLayers.M1] {(pass ? "PASS" : "FAIL")} {name}: {detail}");
    }

    private static void Update()
    {
        if (!active || finished || Current.ProgramState != ProgramState.Playing ||
            Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent || loadPending) return;
        try
        {
            if (mode == "load" && !loadIssued)
            {
                beforeLoad = Current.Game;
                loadPending = true;
                return;
            }
            if (mode == "load" && beforeLoad == Current.Game) return;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            frame++;
            if (frame == 30) Verify(Find.CurrentMap);
            if (frame == 45)
            {
                var c = LayerMapComponent.For(Find.CurrentMap)!;
                LayerView.Switch(c, 1);
                Find.CameraDriver.SetRootPosAndSize(new Vector3(60, 0, c.Layout!.Stride + 60), 25);
            }
            if (frame == 65) ScreenCapture.CaptureScreenshot(Path.Combine(directory, mode + "-upper.png"));
            if (frame == 85)
            {
                var c = LayerMapComponent.For(Find.CurrentMap)!;
                LayerView.Switch(c, 0);
                Find.CameraDriver.SetRootPosAndSize(new Vector3(60, 0, 60), 25);
            }
            if (frame == 105) ScreenCapture.CaptureScreenshot(Path.Combine(directory, mode + "-ground.png"));
            if (frame == 130)
            {
                if (mode == "generate")
                {
                    var c = LayerMapComponent.For(Find.CurrentMap)!;
                    LayerView.Switch(c, 2);
                    GameDataSaveLoader.SaveGame("RimLayers_M1");
                    File.WriteAllText(Path.Combine(directory, "baseline.json"), JsonSerializer.Serialize(new {
                        mapId = Find.CurrentMap.uniqueID, width = c.Layout!.Width, height = c.Layout.Height,
                        layers = c.Layout.LayerCount, stride = c.Layout.Stride,
                        pawns = Find.CurrentMap.mapPawns.AllPawnsSpawned.Select(p => p.ThingID).OrderBy(x => x).ToArray(),
                        things = PersistentThings(Find.CurrentMap),
                        nativeLoadWipes = Find.CurrentMap.listerThings.AllThings.Where(IsNativeLoadWiped).Select(t => t.ThingID).ToArray()
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Check("save-created", File.Exists(Path.Combine(directory, "Saves", "RimLayers_M1.rws")));
                }
                Finish();
            }
        }
        catch (Exception e)
        {
            Check("unhandled-test-exception", false, e.ToString());
            Finish();
        }
    }

    private static void OnGUI()
    {
        if (!active || !loadPending || finished || Event.current.type != EventType.Repaint) return;
        loadPending = false; loadIssued = true;
        GameDataSaveLoader.LoadGame("RimLayers_M1");
    }

    private static void Verify(Map map)
    {
        Check("test-process-keeps-running-in-background",Application.runInBackground);
        if(GenCommandLine.TryGetCommandLineArg("rimlayerslanguage",out var requestedLanguage))
        {
            Check("requested-language-is-active",Prefs.LangFolderName==requestedLanguage,Prefs.LangFolderName);
            if(requestedLanguage=="ChineseSimplified")
                Check("chinese-layer-control-and-floor-copy-loaded","RL_Layer".Translate(1,3).ToString()=="楼层 1/3"&&RimLayersDefOf.RL_StructuralFloor.label=="结构楼板");
        }
        var c = LayerMapComponent.For(map);
        Check("managed-player-map", c?.Layout != null);
        if (c?.Layout == null) return;
        var l = c.Layout;
        Check("layout-storage", l.Width == 120 && l.Height == 120 && l.LayerCount == 3 &&
            l.Stride == 136 && map.Size == new IntVec3(120, 1, 408), map.Size.ToString());
        Check("allocated-cell-indices", map.cellIndices.NumGridCells == 120 * 408);
        if (GenCommandLine.CommandLineArgPassed("rimlayersriver"))
        {
            var flow = map.waterInfo.riverFlowMap;
            Check("river-full-sized-flow", flow != null && flow.Count == l.Width * l.StorageHeight * 2);
            if (flow != null && flow.Count == l.Width * l.StorageHeight * 2)
            {
                bool anyFlow = false, upperZero = true;
                for (int x = 0; x < l.Width; x++)
                    for (int z = 0; z < l.StorageHeight; z++)
                    {
                        int i = (x * l.StorageHeight + z) * 2;
                        if (z < l.Height) anyFlow |= flow[i] != 0 || flow[i + 1] != 0;
                        else upperZero &= flow[i] == 0 && flow[i + 1] == 0;
                    }
                Check("river-flow-confined-to-ground", anyFlow && upperZero);
            }
        }
        int open = 0, gap = 0, invalid = 0;
        for (int z = l.Height; z < l.StorageHeight; z++)
            for (int x = 0; x < l.Width; x++)
            {
                IntVec3 cell = new(x, 0, z);
                bool logical = c.TryDecode(cell, out _);
                TerrainDef expected = logical ? RimLayersDefOf.RL_OpenAir : RimLayersDefOf.RL_Isolation;
                if (logical) open++; else gap++;
                if (cell.GetTerrain(map) != expected || cell.Walkable(map) || cell.Roofed(map) ||
                    cell.GetThingList(map).Count != 0 || cell.Fogged(map)) invalid++;
            }
        Check("all-upper-and-gap-cells", invalid == 0 && open == 120 * 120 * 2 && gap == 120 * 16 * 3,
            $"open={open}, gap={gap}, invalid={invalid}");
        Check("ground-has-generated-contents", map.listerThings.AllThings.Count > 0);
        Check("all-generated-things-on-ground", map.listerThings.AllThings.All(t => c.IsGroundCell(t.Position)));
        var gapCell = new IntVec3(60, 0, l.Height);
        Check("isolation-gas-blocked", !map.gasGrid.GasCanMoveTo(gapCell));
        Check("isolation-sight-blocked", !gapCell.CanBeSeenOverFast(map));
        // Home maps disable the public Grid getter. Exercise the actual patched rebuilder
        // without changing that native policy, then inspect its stored grid.
        AccessTools.Method(typeof(ExitMapGrid), "Rebuild").Invoke(map.exitMapGrid, null);
        var exitGrid = (BoolGrid)AccessTools.Field(typeof(ExitMapGrid), "exitMapGrid").GetValue(map.exitMapGrid)!;
        Check("upper-and-gap-not-exits", map.AllCells.Where(cell => cell.z >= l.Height)
            .All(cell => !exitGrid[cell]));
        Check("ground-grid-has-logical-exits", c.LayerRect(0).Any(cell => exitGrid[cell]));
        Check("isolation-build-rejected", !GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall,
            gapCell, Rot4.North, map).Accepted);
        Check("unsupported-build-rejected", !GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall,
            c.ToStorage(new LayerCell(1, 60, 60)), Rot4.North, map).Accepted);
        var savedPositions = map.mapPawns.AllPawnsSpawned.ToDictionary(p => p, p => p.Position);
        var savedJobs = map.mapPawns.AllPawnsSpawned.ToDictionary(p => p, p => p.CurJob);
        int initialLayer = c.ViewedLayer;
        for (int layer = 0; layer < l.LayerCount; layer++)
        {
            Check("switch-layer-" + layer, LayerView.Switch(c, layer) && c.ViewedLayer == layer);
            var cam = Find.CameraDriver;
            cam.SetRootPosAndSize(new Vector3(-1000, 0, 100000), 1000);
            CellRect view = cam.CurrentViewRect;
            Check("view-bounds-" + layer, view.minX <= view.maxX && view.minZ <= view.maxZ &&
                view.minX >= 0 && view.maxX < l.Width &&
                view.minZ >= layer * l.Stride && view.maxZ < layer * l.Stride + l.Height, view.ToString());
            var mouse = UI.UIToMapPosition(new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f)).ToIntVec3();
            Check("screen-center-target-" + layer, c.IsViewedCell(mouse), mouse.ToString());
            Check("other-layer-picking-rejected-" + layer, GenUI.ThingsUnderMouse(
                new Vector3(60, 0, ((layer + 1) % 3) * l.Stride + 60), 1,
                TargetingParameters.ForAttackAny()).Count == 0);
        }
        Check("switch-preserves-pawn-positions", savedPositions.All(p => p.Key.Position == p.Value));
        Check("switch-preserves-jobs", savedJobs.All(p => ReferenceEquals(p.Key.CurJob, p.Value)));
        Check("invalid-layer-rejected", !LayerView.Switch(c, -1) && !LayerView.Switch(c, 3));
        Pawn targetPawn = map.mapPawns.FreeColonistsSpawned.First();
        LayerView.Switch(c, 2);
        CameraJumper.TryJump(new GlobalTargetInfo(targetPawn), CameraJumper.MovementMode.Cut);
        Check("jump-to-pawn-shows-target-layer", c.ViewedLayer == 0);
        LayerView.Switch(c, 2);
        Find.Selector.Select(targetPawn);
        Check("select-pawn-shows-target-layer", c.ViewedLayer == 0);
        Find.Selector.ClearSelection();
        Check("logical-column", c.Column(0, 0).Select(cell => cell.z).SequenceEqual(new[] { 0, 136, 272 }));
        if (mode == "load")
        {
            using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "baseline.json")));
            var data = baseline.RootElement;
            Check("restart-restores-layout", data.GetProperty("mapId").GetInt32() == map.uniqueID &&
                data.GetProperty("stride").GetInt32() == l.Stride && initialLayer == 2);
            var pawns = data.GetProperty("pawns").EnumerateArray().Select(x => x.GetString()).ToArray();
            var things = data.GetProperty("things").EnumerateArray().Select(x => x.GetString()).ToArray();
            File.WriteAllText(Path.Combine(directory, "load-things.json"), JsonSerializer.Serialize(PersistentThings(map)));
            File.WriteAllText(Path.Combine(directory, "load-maps.json"), JsonSerializer.Serialize(Find.Maps.Select(m => new {
                id = m.uniqueID, size = m.Size.ToString(), managed = LayerMapComponent.For(m) != null
            })));
            Check("restart-preserves-pawn-identities", pawns.SequenceEqual(map.mapPawns.AllPawnsSpawned.Select(p => p.ThingID).OrderBy(x => x)));
            Check("restart-preserves-persistent-things", things.SequenceEqual(PersistentThings(map)),
                $"before={things.Length}, after={PersistentThings(map).Length}");
        }
        VerifyOtherMap(map, c);
        LayerView.Switch(c, initialLayer);
    }

    private static void VerifyOtherMap(Map playerMap, LayerMapComponent component)
    {
        Map? other = Find.Maps.FirstOrDefault(m => m != playerMap && LayerMapComponent.For(m) == null);
        if (other == null && mode == "generate")
        {
            var settlement = Find.WorldObjects.Settlements.First(s => s.Faction != null && !s.Faction.IsPlayer && !s.HasMap);
            other = MapGenerator.GenerateMap(new IntVec3(60, 1, 60), settlement, MapGeneratorDefOf.Encounter);
            // A visited NPC map needs a player pawn to survive native automatic removal.
            Pawn visitor = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(visitor, other.AllCells.First(cell => cell.Standable(other)), other);
        }
        Check("unmanaged-map-preserved", other != null && other.Size == new IntVec3(60, 1, 60) &&
            LayerMapComponent.For(other) == null);
        if (other == null) return;
        LayerView.Switch(component, 1);
        Current.Game.CurrentMap = other;
        Check("switch-to-unmanaged-map", LayerView.Current == null && !LayerView.Switch(component, 0));
        Current.Game.CurrentMap = playerMap;
        Check("switch-back-preserves-view", LayerView.Current == component && component.ViewedLayer == 1);
    }

    // Native MapFileCompressor deliberately regenerates IDs of save-compressible things.
    // Compare all persisted facts; also retain exact IDs for non-compressible definitions.
    private static string[] PersistentThings(Map map) => map.listerThings.AllThings
        .Where(t => t.def.isSaveable && !IsNativeLoadWiped(t))
        .Select(t => string.Join("|", t.def.saveCompressible ? "compressible-definition" : t.ThingID,
            t.def.defName, t.Position.x, t.Position.z, t.stackCount, t.HitPoints))
        .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // Map.FinalizeLoading respawns with FullRefund: both blocking items (e.g. chunks)
    // and buildings wipe overlapping filth. Preserve every other saveable entity.
    private static bool IsNativeLoadWiped(Thing t) => t is Filth && t.Position.GetThingList(t.Map)
        .Any(other => other != t && GenSpawn.SpawningWipes(other.def, t.def));

    private static void Finish()
    {
        if (finished) return;
        Check("no-game-errors", errors.Count == 0, string.Join("\n", errors.Take(8)));
        finished = true;
        File.WriteAllText(Path.Combine(directory, mode + "-results.json"), JsonSerializer.Serialize(new {
            mode, failures, errors, checks = results,
            activeMods=LoadedModManager.RunningModsListForReading.Select(m=>m.PackageId).ToArray(),
            language=Prefs.LangFolderName,assemblyMvid=typeof(Pawn).Assembly.ManifestModule.ModuleVersionId.ToString("N")
        }, new JsonSerializerOptions { WriteIndented = true }));
        Log.Message($"[RimLayers.M1] Finished {mode}: {failures} failure(s).");
        Application.Quit();
    }
}
