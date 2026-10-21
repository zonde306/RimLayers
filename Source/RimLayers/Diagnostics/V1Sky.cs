using System.Text.Json;
using HarmonyLib;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static IntVec3 CoveredSky=>At(0,57,57);
    private static IntVec3 ExposedSky=>At(2,62,57);
    private static void BeginSkyTests()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.Position=At(0,48,48);pawn.Notify_Teleported();
        for(int l=0;l<3;l++)
            foreach(IntVec3 c in new CellRect(55,55,5,5))
            {
                IntVec3 at=At(l,c.x,c.z);
                foreach(Thing t in at.GetThingList(map).ToList())
                    if(t is not Pawn) { if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn(); }
                if(l>0)map.terrainGrid.SetTerrain(at,RimLayersDefOf.RL_OpenAir);
                if(l!=1)map.roofGrid.SetRoof(at,null);
            }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("sky-fixture-has-real-overhead-canopy-not-local-roof",map.roofGrid.RoofAt(CoveredSky)==null && !owner.Sky.IsExposed(CoveredSky) && owner.Sky.IsExposed(ExposedSky));
        Check("sky-query-does-not-rewrite-native-roof-facts",!CoveredSky.Roofed(map) && !At(1,57,57).Walkable(map));
        map.skyManager.ForceSetCurSkyGlow(1);
        map.glowGrid.GlowGridUpdate_First();
        Check("covered-void-does-not-receive-sky-game-light",map.glowGrid.GroundGlowAt(CoveredSky)==0);
        Check("open-upper-terrace-receives-sky-game-light",map.glowGrid.GroundGlowAt(ExposedSky)==1);
        var shade=SectionLayer_LightingOverlay.Bake(map,new CellRect(CoveredSky.x,CoveredSky.z,1,1),null,null);
        var sun=SectionLayer_LightingOverlay.Bake(map,new CellRect(ExposedSky.x,ExposedSky.z,1,1),null,null);
        Check("rendered-light-mesh-agrees-with-sky-cover",shade.mesh.colors32.Max(c=>c.a)>=100 && sun.mesh.colors32.All(c=>c.a==0),
            $"coveredAlpha={shade.mesh.colors32.Max(c=>c.a)}, exposedAlpha={sun.mesh.colors32.Max(c=>c.a)}");
        int light=map.glowGrid.RegisterCustomGlower(CoveredSky,8,new ColorInt(255,255,255),0,false);
        map.glowGrid.GlowGridUpdate_First();
        Check("artificial-light-still-works-below-canopy",map.glowGrid.GroundGlowAt(CoveredSky)>0);
        map.glowGrid.DeregisterCustomGlower(light);map.glowGrid.GlowGridUpdate_First();
        light=map.glowGrid.RegisterCustomGlower(At(0,80,119),30,new ColorInt(255,255,255),0,false);map.glowGrid.GlowGridUpdate_First();
        Check("artificial-light-cannot-cross-storage-gap",map.glowGrid.GroundGlowAt(At(0,80,118),ignoreSky:true)>0 && map.glowGrid.GroundGlowAt(At(1,80,0),ignoreSky:true)==0);
        map.glowGrid.DeregisterCustomGlower(light);map.glowGrid.GlowGridUpdate_First();
        var weather=map.steadyEnvironmentEffects;
        map.snowGrid.SetDepth(CoveredSky,0);map.snowGrid.SetDepth(ExposedSky,0);
        weather.AddFallenSnowAt(CoveredSky,0.4f);weather.AddFallenSnowAt(ExposedSky,0.4f);
        Check("real-precipitation-reaches-only-exposed-floor",map.snowGrid.GetDepth(CoveredSky)==0 && map.snowGrid.GetDepth(ExposedSky)>0);
        map.snowGrid.SetDepth(At(1,57,57),0);weather.AddFallenSnowAt(At(1,57,57),0.4f);
        Check("precipitation-does-not-rest-on-open-air",map.snowGrid.GetDepth(At(1,57,57))==0);
        if(ModsConfig.OdysseyActive)
        {
            map.sandGrid.SetDepth(CoveredSky,0);map.sandGrid.SetDepth(ExposedSky,0);
            weather.AddFallenSandAt(CoveredSky,0.4f);weather.AddFallenSandAt(ExposedSky,0.4f);
            Check("odyssey-sand-obeys-same-sky-cover",map.sandGrid.GetDepth(CoveredSky)==0 && map.sandGrid.GetDepth(ExposedSky)>0);
        }
        map.snowGrid.SetDepth(At(2,63,57),0);map.snowGrid.SetDepth(At(0,63,57),0);
        map.roofGrid.SetRoof(At(0,63,57),null);map.terrainGrid.SetTerrain(At(1,63,57),RimLayersDefOf.RL_OpenAir);
        map.roofGrid.SetRoof(At(1,63,57),null);map.terrainGrid.SetTerrain(At(2,63,57),RimLayersDefOf.RL_OpenAir);
        // Compare noise at one geographic x/z without replacing stored snow positions.
        var noise=AccessTools.Field(typeof(SteadyEnvironmentEffects),"snowNoise").GetValue(weather);
        var noiseMethod=AccessTools.Method(typeof(RimLayers.Environment.SkyQueries),"WeatherNoise");
        float groundNoise=(float)noiseMethod.Invoke(null,new[]{noise,(object)At(0,62,57),weather})!;
        float topNoise=(float)noiseMethod.Invoke(null,new[]{noise,(object)At(2,62,57),weather})!;
        Check("weather-noise-uses-logical-geography",groundNoise==topNoise);
        var coveredFilth=(Filth)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Filth_Dirt),CoveredSky,map);
        var openFilth=(Filth)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Filth_Dirt),ExposedSky,map);
        AccessTools.Field(typeof(SteadyEnvironmentEffects),"rainRate").SetValue(weather,1f);
        AccessTools.Field(typeof(SteadyEnvironmentEffects),"snowRate").SetValue(weather,0f);
        var steady=AccessTools.Method(typeof(SteadyEnvironmentEffects),"DoCellSteadyEffects");
        steady.Invoke(weather,new object[]{CoveredSky});steady.Invoke(weather,new object[]{ExposedSky});
        Check("rain-washes-only-sky-exposed-filth",coveredFilth.Spawned && openFilth.Destroyed,
            $"rainWashes={coveredFilth.def.filth.rainWashes}, covered={coveredFilth.Spawned}, open={openFilth.Spawned}");
        map.gasGrid.SetDirect(CoveredSky,100u<<16);map.gasGrid.SetDirect(ExposedSky,100u<<16);
        var dissipate=AccessTools.Method(typeof(GasGrid),"TryDissipateGases");
        dissipate.Invoke(map.gasGrid,new object[]{map.cellIndices.CellToIndex(CoveredSky)});
        dissipate.Invoke(map.gasGrid,new object[]{map.cellIndices.CellToIndex(ExposedSky)});
        Check("gas-dissipation-uses-same-sky-cover",Rot(CoveredSky)==98 && Rot(ExposedSky)==96);
        var coveredFire=(Fire)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Fire),CoveredSky,map);
        var openFire=(Fire)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Fire),ExposedSky,map);
        var rain=AccessTools.Method(typeof(Fire),"VulnerableToRain");
        Check("overhead-canopy-shelters-fire-from-rain",!(bool)rain.Invoke(coveredFire,null)! && (bool)rain.Invoke(openFire,null)!);
        coveredFire.Destroy(DestroyMode.Vanish);openFire.Destroy(DestroyMode.Vanish);
        CheckWeatherMemory(false);
        GameDataSaveLoader.SaveGame("RimLayers_V1Sky");
        File.WriteAllText(Path.Combine(directory,"sky-baseline.json"),JsonSerializer.Serialize(new { snow=map.snowGrid.GetDepth(ExposedSky),pawn=pawn.ThingID }));
        ShowCoveredSky();
    }
    private static void RestoreSky()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"sky-baseline.json")));
        Check("restart-rebuilds-sky-cover-from-native-roofs",!owner.Sky.IsExposed(CoveredSky) && owner.Sky.IsExposed(ExposedSky) && !CoveredSky.Roofed(map));
        Check("restart-preserves-native-weather-buildup",Math.Abs(map.snowGrid.GetDepth(ExposedSky)-baseline.RootElement.GetProperty("snow").GetSingle())<0.00001);
        map.skyManager.ForceSetCurSkyGlow(1);map.glowGrid.GlowGridUpdate_First();
        Check("restart-game-light-respects-overhead-cover",map.glowGrid.GroundGlowAt(CoveredSky)==0 && map.glowGrid.GroundGlowAt(ExposedSky)==1);
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==baseline.RootElement.GetProperty("pawn").GetString());
        CheckWeatherMemory(true);
        ShowCoveredSky();
    }
    private static void ShowCoveredSky()
    {
        LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.CameraDriver.SetRootPosAndSize(CoveredSky.ToVector3Shifted(),8);
        pendingScreenshot=Path.Combine(directory,mode+"-sky-covered.png");screenshotHold=3;stage=90;ticks=0;
    }
    private static void SkyStep()
    {
        if(ticks<10)return;
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(ExposedSky.ToVector3Shifted(),8);
        pendingScreenshot=Path.Combine(directory,mode+"-sky-terrace.png");screenshotHold=3;stage=11;ticks=0;
    }
}
