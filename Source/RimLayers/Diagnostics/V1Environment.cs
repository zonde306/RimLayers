using System.Diagnostics;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Environment;
using RimLayers.Generation;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimLayers.Underground;
using RimWorld;
using Verse;
using UnityEngine;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static int environmentPhase;
    private static Building_Door environmentDoor=null!;
    private static GameCondition? environmentFallout;
    private static Hive environmentFinite=null!,environmentContinuous=null!;
    private static Mineable environmentOre=null!;
    private static Building environmentBattery=null!,environmentLoad=null!;
    private static Tornado environmentTornado=null!;
    private static float environmentConcentration;
    private static double environmentAirMass;
    private static IntVec3 EnvAt(int elevation,int x,int z)=>owner.AtElevation(elevation,x,z);
    private static Room EnvRoom(int elevation,int x=53,int z=53)=>EnvAt(elevation,x,z).GetRoom(map);
    private static void EnvClear(IntVec3 at,TerrainDef terrain,RoofDef? roof)
    {
        foreach(var thing in at.GetThingList(map).ToArray())
            if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
        map.terrainGrid.SetTerrain(at,terrain);map.roofGrid.SetRoof(at,roof);map.fogGrid.Unfog(at);
    }
    private static void EnvBox(int elevation,int x,int z,bool roof=true)
    {
        var rect=new CellRect(x,z,7,7);
        foreach(var local in rect)
        {
            var at=EnvAt(elevation,local.x,local.z);
            var terrain=elevation>0?RimLayersDefOf.RL_StructuralFloor:DefDatabase<ThingDef>.GetNamed("Granite").building.naturalTerrain;
            if(elevation>0){owner.StructuralCells.Add(at);map.roofGrid.SetRoof(owner.Adjacent(at,-1),RoofDefOf.RoofConstructed);}
            EnvClear(at,terrain,roof?(elevation<0?RoofDefOf.RoofRockThick:RoofDefOf.RoofConstructed):null);
            if(local.x==x||local.x==x+6||local.z==z||local.z==z+6)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),at,map).SetFaction(Faction.OfPlayer);
        }
    }
    private static void EnvParameters()
    {
        foreach(var p in EnvironmentSettings.Parameters)
        {
            var s=new EnvironmentSettings();if(p.Name=="LavaCap")s.LavaCoverage=0;if(p.Name=="LavaMaxSize")s.LavaMinSize=8;p.Set(s,p.Min);s.Normalize();Check("parameter-min-"+p.Name,p.Get(s)==p.Min);
            s=new();p.Set(s,p.Max);s.Normalize();Check("parameter-max-"+p.Name,p.Get(s)==p.Max);
            if(!p.Integer){s=new();p.Set(s,float.NaN);s.Normalize();Check("parameter-invalid-default-"+p.Name,p.Get(s)==p.Default);}
        }
        var paired=new EnvironmentSettings{LavaCoverage=.04f,LavaCap=.01f,LavaMinSize=96,LavaMaxSize=24};
        paired.Normalize();Check("paired-coverage-and-area-bounds",paired.LavaCap==.04f&&paired.LavaMaxSize==96);
        var old=new LayerEnvironmentGame(Current.Game);Check("missing-old-save-component-is-disabled",!old.Enabled);
        Check("new-game-environment-enabled",LayerEnvironmentGame.Active);
        RimLayersMod.Settings.Reset();Check("settings-reset-does-not-change-save-activation",LayerEnvironmentGame.Active&&RimLayersMod.Settings.UndergroundLimit==2);
    }
    private static void BeginEnvironment()
    {
        Check("environment-localization-loaded",Translator.CanTranslate("RL_SettingsEnvironment")&&Translator.CanTranslate("RL_EnvironmentRockOffset"));
        EnvParameters();
        RimLayersMod.Settings.UndergroundLimit=9;RimLayersMod.Settings.UpperLimit=9;RimLayersMod.Settings.GlobalLimit=100;
        pawn=map.mapPawns.FreeColonistsSpawned.First();foreach(var p in map.mapPawns.FreeColonistsSpawned){p.jobs.StopAll();p.drafter.Drafted=true;}
        LiveMapExpansion.EnsureElevation(map,2);LiveMapExpansion.EnsureElevation(map,-2);owner=LayerMapComponent.For(map)!;
        Check("environment-has-five-real-layers",owner.Layout!.LayerCount==5);
        Check("geology-records-once-per-new-basement",owner.Underground.GenerationRecords.Count==2&&owner.Underground.GenerationRecords.All(r=>r.LavaCells==0));
        if(scenario=="environment-geology"){EnvironmentGeologyBatch();return;}
        if(scenario=="environment-perf"){BeginEnvironmentPerf();return;}
        EnvBox(-1,50,50);EnvBox(-2,50,50);
        // A separate ventilation shaft has an exposed source and an independently sealable lower door.
        EnvBox(0,70,70,false);EnvBox(-1,70,70);
        foreach(int upper in new[]{1,2})foreach(var c in new CellRect(70,70,7,7))
        {var at=EnvAt(upper,c.x,c.z);map.terrainGrid.SetTerrain(at,RimLayersDefOf.RL_OpenAir);map.roofGrid.SetRoof(at,null);}
        var down=(Building_Stairs)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("RL_StairsDown"),EnvAt(0,73,73),map);
        environmentDoor=(Building_Door)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Door,ThingDefOf.Steel),down.UpperCell,map);
        environmentDoor.SetFaction(Faction.OfPlayer);environmentDoor.SetForbidden(true);
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("new-rock-climate-is-local-annual-mean",Math.Abs(owner.Environment.Rock(owner.Layout.LayerAt(-1))-(Find.WorldGrid[map.Tile].temperature+.25f))<.001f);
        var room=EnvRoom(-1);room.Temperature=37.5f;RimLayersMod.Settings.Environment.RockOffset=5;
        Check("parameter-change-does-not-reset-room",room.Temperature==37.5f);RimLayersMod.Settings.Environment.RockOffset=0;
        var deep=AccessTools.Method(typeof(RoomTempTracker),"DeepEqualizationTempChangePerInterval");
        room.Temperature=owner.Environment.Rock(owner.Layout.LayerAt(-1))-5;
        Check("natural-rock-can-warm-a-cold-room",(float)deep.Invoke(room.TempTracker,null)!>0);
        room.Temperature=owner.Environment.Rock(owner.Layout.LayerAt(-1))+5;
        Check("natural-rock-can-cool-a-hot-room",(float)deep.Invoke(room.TempTracker,null)!<0);
        var cell=EnvAt(-1,54,54);var oldTerrain=cell.GetTerrain(map);map.terrainGrid.SetTerrain(cell,DefDatabase<TerrainDef>.GetNamed("RL_LavaDeep"));
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();room=EnvRoom(-1);room.Temperature=20;var steady=AccessTools.Method(typeof(SteadyEnvironmentEffects),"DoCellSteadyEffects");steady.Invoke(map.steadyEnvironmentEffects,new object[]{cell});
        Check("core-magma-heats-actual-cavern",room.Temperature>20);
        room.Temperature=20;RimLayersMod.Settings.Environment.LavaHeat=0;steady.Invoke(map.steadyEnvironmentEffects,new object[]{cell});
        Check("zero-heating-does-not-change-contact-burns",room.Temperature==20&&cell.GetTerrain(map).burnDamage==3);
        Check("magma-is-a-contact-plane-not-a-recovery-void",owner.Structure.HasSurface(cell)&&!RimLayers.Recovery.RecoveryLanding.Legal(owner,pawn,cell));
        RimLayersMod.Settings.Environment.LavaHeat=1;map.terrainGrid.SetTerrain(cell,oldTerrain);
        // Finite exploration hive and an ordinary continuous hive are different native objects.
        environmentFinite=HiveUtility.SpawnHive(EnvAt(-2,52,52),map,WipeMode.VanishOrMoveAside,true,false,false,false,false);
        environmentContinuous=HiveUtility.SpawnHive(EnvAt(-2,54,54),map,WipeMode.VanishOrMoveAside,false,true,true,false,false);
        Check("finite-hive-keeps-native-flags-and-initial-insects",!environmentFinite.GetComp<CompSpawnerPawn>().canSpawnPawns&&
            !environmentFinite.GetComp<CompSpawnerHives>().canSpawnHives&&environmentFinite.GetComp<CompSpawnerPawn>().spawnedPawns.Count>0);
        Check("ordinary-infestation-hive-remains-continuous",environmentContinuous.GetComp<CompSpawnerPawn>().canSpawnPawns&&environmentContinuous.GetComp<CompSpawnerHives>().canSpawnHives&&CompSpawnerHives.MaxHivesPerMap==30);
        environmentContinuous.GetComp<CompSpawnerPawn>().nextPawnSpawnTick=Find.TickManager.TicksGame+5000;
        AccessTools.Field(typeof(CompSpawnerHives),"nextHiveSpawnTick").SetValue(environmentContinuous.GetComp<CompSpawnerHives>(),Find.TickManager.TicksGame+7000);
        RimLayersMod.Settings.Environment.InsectSpawnSpeed=0;RimLayersMod.Settings.Environment.HiveReproductionSpeed=0;
        environmentOre=map.listerThings.AllThings.OfType<Mineable>().First(t=>t.def.building.isResourceRock&&owner.TryDecode(t.Position,out var q)&&owner.Elevation(q.LayerId)<0);
        environmentOre.HitPoints-=7;
        // Keep unrelated native wandering Pawns away from the valve; they must not reopen it during a sealing measurement.
        foreach(var actor in map.mapPawns.AllPawnsSpawned.ToArray())
        {
            actor.jobs.StopAll();actor.pather.StopDead();
            actor.Position=actor.Faction==Faction.OfInsects?EnvAt(-2,53,53):EnvAt(0,15,15);
            actor.Notify_Teleported();
        }
        EnvBox(-1,85,50);
        environmentBattery=(Building)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Battery"),EnvAt(-1,87,52),map);environmentBattery.SetFaction(Faction.OfPlayer);
        environmentBattery.GetComp<CompPowerBattery>().AddEnergy(500);
        environmentLoad=(Building)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("StandingLamp"),EnvAt(-1,89,52),map);environmentLoad.SetFaction(Faction.OfPlayer);
        foreach(int x in Enumerable.Range(86,4))if(!environmentBattery.OccupiedRect().Contains(EnvAt(-1,x,52)))GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("PowerConduit"),EnvAt(-1,x,52),map).SetFaction(Faction.OfPlayer);
        map.powerNetManager.UpdatePowerNetsAndConnections_First();
        environmentFallout=GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("ToxicFallout"),60000);map.gameConditionManager.RegisterCondition(environmentFallout);
        environmentPhase=0;stage=560;ticks=0;
    }
    private static void EnvironmentStep()
    {
        ticks++;
        if(environmentPhase==0&&ticks>=240)
        {
            Check("sealed-basement-blocks-incoming-fallout",owner.Environment.Concentration(EnvAt(-1,72,72),Atmosphere.Toxic)==0);
            Check("exposed-shaft-source-receives-fallout",owner.Environment.Concentration(EnvAt(0,72,72),Atmosphere.Toxic)>.9f);
            Check("closed-door-seals-air-channel",!owner.Air.IsOpen(EnvAt(-1,73,73),EnvAt(0,73,73)),$"open={environmentDoor.Open},edifice={environmentDoor.Position.GetEdifice(map)?.ThingID}");
            AccessTools.Field(typeof(Building_Door),"holdOpenInt").SetValue(environmentDoor,true);
            AccessTools.Method(typeof(Building_Door),"DoorOpen").Invoke(environmentDoor,new object[]{10000});environmentPhase=1;ticks=0;return;
        }
        if(environmentPhase==1&&ticks>=600)
        {
            environmentConcentration=owner.Environment.Concentration(EnvAt(-1,72,72),Atmosphere.Toxic);
            Check("open-shaft-gradually-transmits-fallout",environmentConcentration>0&&environmentConcentration<.25f,environmentConcentration.ToString());
            Check("same-channel-retains-liquid-permission",VolumeLinks.Open(owner,EnvAt(-1,73,73),EnvAt(0,73,73),LayerChangeKind.Liquid));
            AccessTools.Field(typeof(Building_Door),"holdOpenInt").SetValue(environmentDoor,false);
            AccessTools.Method(typeof(Building_Door),"DoorTryClose").Invoke(environmentDoor,null);
            environmentAirMass=owner.Environment.Mass(owner.Layout!.LayerAt(-1),Atmosphere.Toxic);
            Check("door-physically-closes",!environmentDoor.Open);
            environmentPhase=2;ticks=0;return;
        }
        if(environmentPhase==2&&ticks>=240)
        {
            float concentration=owner.Environment.Concentration(EnvAt(-1,72,72),Atmosphere.Toxic);
            double mass=owner.Environment.Mass(owner.Layout!.LayerAt(-1),Atmosphere.Toxic);
            Check("sealed-valve-stays-physically-closed",!environmentDoor.Open);
            Check("sealed-room-keeps-decaying-residual-pollution",concentration>0&&mass>0&&mass<environmentAirMass,$"mass {environmentAirMass} -> {mass}; local {environmentConcentration} -> {concentration}");
            var net=environmentLoad.PowerComp.PowerNet;
            Check("independent-thick-rock-grid-is-shielded",net!=null&&SolarShield.Protected(net));
            var flare=GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("SolarFlare"),60000);map.gameConditionManager.RegisterCondition(flare);
            Check("flare-is-still-global-but-grid-query-is-local",map.gameConditionManager.ElectricityDisabled(map)&&!SolarShield.Disabled(map.gameConditionManager,map,net!));
            RimLayersMod.Settings.Environment.SolarShield=false;Check("shield-setting-zero-restores-native-flare",SolarShield.Disabled(map.gameConditionManager,map,net!));RimLayersMod.Settings.Environment.SolarShield=true;
            EnvironmentPowerIsolation();flare.End();
            EnvironmentDisasterChecks();EnvironmentHydrology();EnvironmentWeatherChecks();EnvironmentLocalEffects();
            EnvironmentSave();
            return;
        }
        if(environmentPhase==3&&ticks>=8)
        {
            var menu=new SettingsMenu();AccessTools.Field(typeof(SettingsMenu),"page").SetValue(menu,5);
            var mod=LoadedModManager.GetMod<RimLayersMod>();AccessTools.Field(typeof(RimLayersMod),"menu").SetValue(mod,menu);
            Find.WindowStack.Add(new Dialog_ModSettings(mod));pendingScreenshot=Path.Combine(directory,mode+"-environment-settings.png");screenshotHold=4;
            environmentPhase=4;ticks=0;return;
        }
        if(environmentPhase==4&&ticks>=8){Finish();return;}
    }
    private static void EnvironmentDisasterChecks()
    {
        var ground=EnvAt(0,102,102);var upper=EnvAt(1,102,102);var below=EnvAt(-1,102,102);
        foreach(var local in new CellRect(100,100,5,5))
        {
            EnvClear(EnvAt(0,local.x,local.z),TerrainDefOf.Concrete,RoofDefOf.RoofConstructed);
            var at=EnvAt(1,local.x,local.z);owner.StructuralCells.Add(at);EnvClear(at,RimLayersDefOf.RL_StructuralFloor,null);
        }
        EnvClear(below,DefDatabase<ThingDef>.GetNamed("Granite").building.naturalTerrain,RoofDefOf.RoofRockThick);
        var walls=new[]{ground,upper,below}.Select(c=>(Building)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.WoodLog),c,map)).ToArray();
        foreach(var wall in walls)wall.SetFaction(Faction.OfPlayer);
        var hp=walls.Select(w=>w.HitPoints).ToArray();map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var tornado=(Tornado)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Tornado"),ground,map);
        Check("one-physical-tornado-for-all-upper-floors",map.listerThings.AllThings.OfType<Tornado>().Count()==1);
        AccessTools.Method(typeof(Tornado),"DamageCloseThings").Invoke(tornado,null);
        Check("wind-column-hits-exterior-upper-and-ground-not-basement",walls[0].HitPoints<hp[0]&&walls[1].HitPoints<hp[1]&&walls[2].HitPoints==hp[2],string.Join(",",walls.Select(w=>w.HitPoints)));
        tornado.Destroy(DestroyMode.Vanish);
        var logicalBolt=EnvAt(0,101,102);
        Check("lightning-selects-highest-exposed-support",LocalLightningStrike.Prefix(ref logicalBolt,map)&&logicalBolt==EnvAt(1,101,102));
        Check("tornado-cannot-traverse-storage-isolation",!TornadoColumn.Bounds(new IntVec3(10,0,owner.Layout!.Height),map));
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
    }
    private static void EnvironmentLocalEffects()
    {
        var sample=EnvAt(-1,72,72);float concentration=owner.Environment.Concentration(sample,Atmosphere.Toxic);
        var meal=ThingMaker.MakeThing(ThingDefOf.MealSimple);GenSpawn.Spawn(meal,sample,map);
        var rot=meal.TryGetComp<CompRottable>();float before=rot.RotProgress;
        ((GameCondition_ToxicFallout)environmentFallout!).DoCellSteadyEffects(sample,map);
        Check("native-fallout-item-effect-scales-by-room-concentration",Math.Abs(rot.RotProgress-before-3000*concentration)<.01f);
        environmentFallout.End();owner.Environment.Invalidate();
        Check("ended-fallout-retains-local-residual-effects",owner.Environment.HasResidual(Atmosphere.Toxic)&&owner.Environment.Concentration(sample,Atmosphere.Toxic)>0);
        before=rot.RotProgress;AccessTools.Method(typeof(SteadyEnvironmentEffects),"DoCellSteadyEffects").Invoke(map.steadyEnvironmentEffects,new object[]{sample});
        Check("native-cell-cadence-applies-residual-to-items",rot.RotProgress>before);meal.Destroy(DestroyMode.Vanish);
        var comp=environmentContinuous.GetComp<CompSpawnerPawn>();int remaining=comp.nextPawnSpawnTick-Find.TickManager.TicksGame;
        RimLayersMod.Settings.Environment.InsectSpawnSpeed=2;Check("resuming-timer-does-not-recalculate-progress",comp.nextPawnSpawnTick-Find.TickManager.TicksGame==remaining);
        int seed=41917;Rand.PushState(seed);comp.CalculateNextPawnSpawnTick(1);int doubled=comp.nextPawnSpawnTick-Find.TickManager.TicksGame;Rand.PopState();
        RimLayersMod.Settings.Environment.InsectSpawnSpeed=1;Rand.PushState(seed);comp.CalculateNextPawnSpawnTick(1);int normal=comp.nextPawnSpawnTick-Find.TickManager.TicksGame;Rand.PopState();
        Check("nonzero-spawn-speed-applies-at-next-native-interval",Math.Abs(doubled*2-normal)<=1);
        comp.nextPawnSpawnTick=Find.TickManager.TicksGame+remaining;RimLayersMod.Settings.Environment.InsectSpawnSpeed=0;
    }
    private static void EnvironmentSave()
    {
        var nativePawn=environmentContinuous.GetComp<CompSpawnerPawn>();
        int remaining=nativePawn.nextPawnSpawnTick-Find.TickManager.TicksGame;
        Check("paused-insect-timer-preserves-remaining-progress",remaining==5000,remaining.ToString());
        var sample=EnvAt(-1,72,72);float concentration=owner.Environment.Concentration(sample,Atmosphere.Toxic);
        EnvRoom(-1).Temperature=42.375f;
        environmentTornado=(Tornado)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Tornado"),EnvAt(0,90,90),map);
        TornadoColumn.Real(environmentTornado)=new(90.25f,90.75f);
        AccessTools.Field(typeof(Tornado),"leftFadeOutTicks").SetValue(environmentTornado,5000);
        File.WriteAllText(Path.Combine(directory,"environment-baseline.json"),JsonSerializer.Serialize(new{
            tornado=environmentTornado.ThingID,tornadoX=90.25f,tornadoZ=90.75f,ore=environmentOre.ThingID,hp=environmentOre.HitPoints,finite=environmentFinite.ThingID,continuous=environmentContinuous.ThingID,
            rock=owner.Environment.Rock(owner.Layout!.LayerAt(-1)),temperature=EnvRoom(-1).Temperature,concentration,remaining,
            generation=owner.Underground.GenerationRecords.Count,pawn=pawn.ThingID,things=map.listerThings.AllThings.Count}));
        GameDataSaveLoader.SaveGame("RimLayers_Environment");
        LayerView.Switch(owner,owner.Layout!.LayerAt(-1));Find.CameraDriver.SetRootPosAndSize(EnvAt(-1,73,73).ToVector3Shifted(),11);
        pendingScreenshot=Path.Combine(directory,mode+"-environment-basement.png");screenshotHold=4;environmentPhase=3;ticks=0;
    }
    private static void RestoreEnvironment()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"environment-baseline.json")));var data=document.RootElement;
        bool legacy=GenCommandLine.CommandLineArgPassed("rimlayersenvironmentlegacy");
        Check(legacy?"old-save-requires-manual-enablement":"save-enablement-survives-native-restart",LayerEnvironmentGame.Active!=legacy);
        environmentTornado=(Tornado)map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("tornado").GetString());
        Check("tornado-native-position-and-fade-progress-survive-reload",
            TornadoColumn.Real(environmentTornado)==new Vector2(data.GetProperty("tornadoX").GetSingle(),data.GetProperty("tornadoZ").GetSingle())&&
            (int)AccessTools.Field(typeof(Tornado),"leftFadeOutTicks").GetValue(environmentTornado)! ==5000);
        environmentOre=(Mineable)map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("ore").GetString());
        environmentFinite=(Hive)map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("finite").GetString());
        environmentContinuous=(Hive)map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("continuous").GetString());
        Check("native-ore-identity-and-damage-survive",environmentOre.HitPoints==data.GetProperty("hp").GetInt32());
        Check("finite-hive-is-not-converted-on-reload",!environmentFinite.GetComp<CompSpawnerPawn>().canSpawnPawns&&!environmentFinite.GetComp<CompSpawnerHives>().canSpawnHives);
        Check("continuous-hive-keeps-remaining-timer",environmentContinuous.GetComp<CompSpawnerPawn>().nextPawnSpawnTick-Find.TickManager.TicksGame==data.GetProperty("remaining").GetInt32());
        Check("global-parameters-are-not-frozen-into-save",RimLayersMod.Settings.Environment.InsectSpawnSpeed==1);
        if(!legacy)Check("rock-temperature-survives-restart",Math.Abs(owner.Environment.Rock(owner.Layout!.LayerAt(-1))-data.GetProperty("rock").GetSingle())<.0001f);
        Check("native-room-temperature-survives-restart",Math.Abs(EnvRoom(-1).Temperature-data.GetProperty("temperature").GetSingle())<.063f);
        if(!legacy)Check("pollution-survives-with-byte-quantization",Math.Abs(owner.Environment.Concentration(EnvAt(-1,72,72),Atmosphere.Toxic)-data.GetProperty("concentration").GetSingle())<=1/255f+.00001f);
        Check("generation-records-do-not-repeat",owner.Underground.GenerationRecords.Count==(legacy?0:data.GetProperty("generation").GetInt32()));
        if(legacy)
        {
            float temperature=EnvRoom(-1).Temperature;
            Current.Game.GetComponent<LayerEnvironmentGame>().Enabled=true;
            Check("manual-opt-in-does-not-reset-existing-room-or-ore",EnvRoom(-1).Temperature==temperature&&environmentOre.HitPoints==data.GetProperty("hp").GetInt32());
            RimLayersMod.Settings.Reset();Check("old-save-opt-in-survives-settings-reset",LayerEnvironmentGame.Active);
        }
        int count=map.listerThings.AllThings.Count;LiveMapExpansion.EnsureElevation(map,-2);
        Check("reentering-generated-layer-does-not-refresh-resources",map.listerThings.AllThings.Count==count);
        RimLayersMod.Settings.UndergroundLimit=9;RimLayersMod.Settings.GlobalLimit=100;
        var size=map.Size;bool failed=false;var records=owner.Underground.GenerationRecords.Count;
        try{LiveMapExpansion.EnsureElevation(map,-6,()=>throw new InvalidOperationException("environment rollback fixture"));}
        catch(InvalidOperationException e)when(e.Message=="environment rollback fixture"){failed=true;}
        Check("failed-expansion-rolls-back-geology-and-climate",failed&&map.Size==size&&owner.Underground.GenerationRecords.Count==records&&owner.Layout!.LowestElevation==-2);
        environmentPhase=3;stage=560;ticks=0;
    }
}
