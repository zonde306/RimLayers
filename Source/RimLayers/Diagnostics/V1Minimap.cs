using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Regression for Dubs Mint Minimap on a layered map. The minimap assumes Map.Size and
/// Map.AllCells cover the whole playable map, which stops being true once floors share one
/// coordinate band. This scenario builds the same real window and drives the same native entries
/// the minimap uses, then asserts the results a player would see: a 1:1 minimap texture, a camera
/// box that tracks the viewed floor, working click-to-jump, and no per-frame exception.
/// </summary>
internal static partial class V1Integration
{
    private static bool minimapCompatibility;
    private static object? minimapWindow;
    private static Texture2D? minimapTexture;
    private static bool[]? minimapDirty;
    private static Color[,]? minimapPixels;
    private static int minimapPhase;
    private static int minimapFrame;
    private static int minimapErrorsBefore;
    private static Vector3 minimapCameraRoot;
    private static IntVec3 minimapJumpTarget;

    private static bool MinimapActive => AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap") != null;

    private static void BeginMinimap()
    {
        minimapCompatibility = MinimapActive;
        Check("minimap-fixture-map-is-layered", owner.Layout is { LayerCount: >= 3 });
        Check("minimap-fixture-uses-one-storage-band-per-floor",
            owner.Layout!.StorageHeight == owner.Layout.Stride * owner.Layout.LayerCount &&
            map.Size.z == owner.Layout.StorageHeight && owner.Layout.Height < owner.Layout.Stride,
            $"size={map.Size}, stride={owner.Layout.Stride}, layers={owner.Layout.LayerCount}");
        if (!minimapCompatibility)
        {
            // The dependency is optional; the remaining assertions need the real assembly.
            Check("minimap-mod-present-for-compatibility-run", false, "Dubs Mint Minimap is not loaded; run with -Minimap");
            Finish();
            return;
        }
        minimapErrorsBefore = errors.Count;
        var windowType = AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap")!;
        var settingsType = AccessTools.TypeByName("DubsMintMinimap.DubsMintMinimapMod")!;
        var settings = AccessTools.Field(settingsType, "Settings")!.GetValue(null)!;
        AccessTools.Field(settings.GetType(), "MinimapOpen")!.SetValue(settings, true);
        AccessTools.Field(settings.GetType(), "ShowFog")!.SetValue(settings, false);
        AccessTools.Field(settings.GetType(), "NaturalTerrain")!.SetValue(settings, true);
        AccessTools.Field(settings.GetType(), "ShowMinimapButt")!.SetValue(settings, false);
        // Construct the real window; its constructor is what builds the texture and arrays.
        minimapWindow = AccessTools.Constructor(windowType)!.Invoke(null);
        Find.WindowStack.Add((Window)minimapWindow);
        minimapTexture = (Texture2D?)AccessTools.Field(windowType, "MapTexture")!.GetValue(null);
        minimapDirty = (bool[]?)AccessTools.Field(windowType, "dirtyGirls")!.GetValue(null);
        minimapPixels = (Color[,]?)AccessTools.Field(windowType, "pixels")!.GetValue(null);
        minimapPhase = 0; minimapFrame = 0; stage = 570; ticks = 0;
    }

    private static void MinimapStep()
    {
        minimapFrame++;
        if (minimapPhase == 0)
        {
            // The constructor has already run UpdateMapTex on the layered map.
            Check("minimap-compatibility-is-active", DubsMinimapCompatibility.RedirectedCalls > 0,
                $"calls={DubsMinimapCompatibility.RedirectedCalls}");
            var layout = owner.Layout!;
            Check("minimap-texture-is-one-floor-not-the-storage-band",
                minimapTexture != null && minimapTexture.width == layout.Width && minimapTexture.height == layout.Height,
                $"texture={minimapTexture?.width}x{minimapTexture?.height}, expected={layout.Width}x{layout.Height}, storage={map.Size}");
            Check("minimap-pixel-array-is-one-floor", minimapPixels != null &&
                minimapPixels.GetLength(0) == layout.Width && minimapPixels.GetLength(1) == layout.Height,
                $"pixels={minimapPixels?.GetLength(0)}x{minimapPixels?.GetLength(1)}");
            Check("minimap-dirty-array-is-one-floor-not-native-area",
                minimapDirty != null && minimapDirty.Length == layout.Width * layout.Height,
                $"dirty={minimapDirty?.Length}, floor={layout.Width * layout.Height}, nativeArea={map.Area}");
            Check("minimap-aspect-ratio-is-square-for-a-square-floor",
                Math.Abs((float)layout.Height / layout.Width - 1f) < .001f,
                $"logical={layout.Width}x{layout.Height}, native={map.Size.z / (float)map.Size.x:0.###}");
            // Ground floor: the terrain drawn must be the real surface terrain, not isolation.
            var groundTerrain = map.terrainGrid.TerrainAt(At(0, 60, 60));
            Check("minimap-reads-real-ground-terrain-through-translated-cell",
                minimapPixels != null && groundTerrain != RimLayersDefOf.RL_Isolation,
                $"terrain={groundTerrain.defName}, pixel={minimapPixels?[60, 60]}");
            // A distinctive floor makes the texture-vs-floor check observable rather than assumed.
            foreach (var c in new CellRect(55, 55, 12, 12))
                map.terrainGrid.SetTerrain(At(layout.LayerCount - 1, c.x, c.z), TerrainDefOf.WoodPlankFloor);
            // Switch to the top floor and confirm the camera box still resolves.
            LayerView.Switch(owner, layout.LayerCount - 1);
            minimapCameraRoot = LayerView.CameraRoot;
            minimapJumpTarget = new IntVec3(70, 0, 70);
            minimapPhase = 1; minimapFrame = 0;
            return;
        }
        if (minimapPhase == 1)
        {
            if (minimapFrame < 5) return;
            // The camera box is normalised against the logical size, so it must stay inside the floor.
            var camera = Find.CameraDriver;
            var localZ = minimapCameraRoot.z - owner.ViewedLayer * owner.Layout!.Stride;
            Check("camera-position-maps-into-viewed-floor",
                localZ >= 0 && localZ <= owner.Layout.Height,
                $"root={minimapCameraRoot}, viewed={owner.ViewedLayer}, localZ={localZ:0.#}, height={owner.Layout.Height}");
            // Drive the minimap's own click-to-jump path with a floor-local target.
            var jump = AccessTools.Method(typeof(CameraDriver), nameof(CameraDriver.JumpToCurrentMapLoc), new[] { typeof(Vector3) });
            // The minimap always calls this from inside its own window draw, which is the scope the
            // compatibility layer keys on; reproduce that scope rather than calling it bare.
            DubsMinimapCompatibility.Enter(map);
            try { jump.Invoke(camera, new object[] { minimapJumpTarget.ToVector3() }); }
            finally { DubsMinimapCompatibility.Exit(); }
            Find.CameraDriver.Update();
            var landed = (Vector3)AccessTools.Field(typeof(CameraDriver), "rootPos")!.GetValue(camera)!;
            var expectedZ = owner.ViewedLayer * owner.Layout.Stride + 70;
            Check("minimap-jump-lands-on-viewed-floor-not-a-storage-band",
                Math.Abs(landed.z - expectedZ) < 1.5f && Math.Abs(landed.x - 70f) < 1.5f,
                $"landed={landed}, expectedZ={expectedZ}, viewed={owner.ViewedLayer}");
            minimapPhase = 2; minimapFrame = 0;
            return;
        }
        if (minimapPhase == 2)
        {
            if (minimapFrame < 8) return;
            // The floor change must have rebuilt the texture, so the top floor's own terrain wins.
            var topTerrain = map.terrainGrid.TerrainAt(At(owner.ViewedLayer, 60, 60));
            Check("minimap-texture-refreshes-when-the-viewed-floor-changes",
                minimapPixels != null && topTerrain == TerrainDefOf.WoodPlankFloor &&
                minimapPixels[60, 60].r > .5f && Math.Abs(minimapPixels[60, 60].r - minimapPixels[60, 60].g) < .05f,
                $"terrain={topTerrain.defName}, pixel={minimapPixels?[60, 60]}");
            // Force the minimap's Section.TryUpdate prefix over every dirty section, which is the
            // path that used to write past the end of its per-cell array.
            var before = DubsMinimapCompatibility.IndexMisses;
            map.mapDrawer.MapMeshDirty(At(0, 60, 60), (ulong)MapMeshFlagDefOf.Terrain);
            map.mapDrawer.MapMeshDirty(At(1, 60, 60), (ulong)MapMeshFlagDefOf.Terrain);
            map.mapDrawer.MapMeshDirty(At(owner.ViewedLayer, 60, 60), (ulong)MapMeshFlagDefOf.Terrain);
            map.mapDrawer.MapMeshDrawerUpdate_First();
            var after = DubsMinimapCompatibility.IndexMisses;
            var sectionLayout = owner.Layout!;
            Check("minimap-section-prefix-never-indexes-outside-its-array",
                minimapDirty != null && minimapDirty.Length == sectionLayout.Width * sectionLayout.Height,
                $"dirty={minimapDirty?.Length}, floor={sectionLayout.Width * sectionLayout.Height}, misses={after - before}");
            // Upper floors are mostly open air. The engine's missing-texture magenta would make the
            // minimap unreadable, so RimLayers' non-rendering placeholders need a flat colour.
            // Sample a cell the fixture actually revealed; fogged cells are transparent by design.
            var openAirCell = At(owner.ViewedLayer, 60, 90);
            map.fogGrid.Unfog(openAirCell);
            AccessTools.Method(typeof(DubsMinimapCompatibility), "Enter", new[] { typeof(Map) })
                ?.Invoke(null, new object[] { map });
            try { AccessTools.Method(AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap")!, "UpdateMapTex", new[] { typeof(Map), typeof(bool) })
                    ?.Invoke(null, new object[] { map, true }); }
            finally { DubsMinimapCompatibility.Exit(); }
            minimapPixels = (Color[,]?)AccessTools.Field(AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap")!, "pixels")!.GetValue(null);
            var openAirPixel = minimapPixels?[60, 90] ?? default;
            var openAirTerrain = map.terrainGrid.TerrainAt(openAirCell);
            Check("minimap-open-air-is-a-flat-neutral-colour-not-missing-texture-magenta",
                openAirTerrain == RimLayersDefOf.RL_OpenAir && openAirPixel.a > .5f &&
                openAirPixel.r < .3f && openAirPixel.g < .3f && openAirPixel.b < .3f,
                $"terrain={openAirTerrain.defName}, pixel={openAirPixel}");
            Check("minimap-update-survived-layered-sections-without-new-errors",
                errors.Count == minimapErrorsBefore, string.Join("\n", errors.Skip(minimapErrorsBefore).Take(3)));
            minimapPhase = 3; minimapFrame = 0;
            return;
        }
        if (minimapPhase == 3)
        {
            if (minimapFrame < 4) return;
            pendingScreenshot = Path.Combine(directory, mode + "-minimap-upper.png");
            screenshotHold = 3;
            minimapPhase = 4; minimapFrame = 0;
            return;
        }
        if (minimapPhase == 4)
        {
            if (screenshotHold > 0) return;
            File.WriteAllText(Path.Combine(directory, "minimap-compatibility.json"), JsonSerializer.Serialize(new
            {
                size = map.Size.ToString(), viewed = owner.ViewedLayer,
                logical = new { width = owner.Layout!.Width, height = owner.Layout.Height, stride = owner.Layout.Stride, layers = owner.Layout.LayerCount },
                texture = new { width = minimapTexture?.width ?? 0, height = minimapTexture?.height ?? 0 },
                dirtyLength = minimapDirty?.Length ?? 0,
                redirectedCalls = DubsMinimapCompatibility.RedirectedCalls,
                indexMisses = DubsMinimapCompatibility.IndexMisses
            }, new JsonSerializerOptions { WriteIndented = true }));
            Finish();
        }
    }
}
