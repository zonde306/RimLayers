using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Combat;
using RimLayers.Layout;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

/// <summary>Opt-in acceptance scenarios run through the real game, never an ordinary save.</summary>
internal static partial class V1Integration
{
    private static string directory = "";
    private static string mode = "generate";
    private static string scenario = "navigation";
    private static Bill_Production? bill;
    private static readonly List<Thing> workIngredients = new();
    private static Thing? lastCarry;
    private static int pickups;
    private static Frame? constructionFrame;
    private static int constructionPhase;
    private static int constructionBlockedAt;
    private static Thing? fallingItem;
    private static Thing? heldItem;
    private static Frame? fallingFrame;
    private static int steelBefore;
    private static Thing? productToHaul;
    private static IntVec3 storageCell;
    private static int frame;
    private static int stage;
    private static int ticks;
    private static int failures;
    private static bool finished;
    private static bool loadPending;
    private static bool loadIssued;
    private static Game? oldGame;
    private static Pawn pawn = null!;
    private static Map map = null!;
    private static LayerMapComponent owner = null!;
    private static Job? expectedJob;
    private static int expectedJobId;
    private static bool ExpectedJobMatches() => ReferenceEquals(pawn.CurJob, expectedJob) && pawn.CurJob?.loadID == expectedJobId;
    private static Building_Stairs? removedStair;
    private static IntVec3 destination;
    private static bool retainedJob = true;
    private static readonly HashSet<int> visited = new();
    private static readonly List<object> checks = new();
    private static readonly List<string> errors = new();

    internal static void Configure()
    {
        if (!GenCommandLine.CommandLineArgPassed("rimlayersv1test")) return;
        directory = Path.GetFullPath(GenFilePaths.SaveDataFolderPath);
        string allowed = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(V1Integration).Assembly.Location)!, "..", "Tests", "Artifacts"));
        if (!string.Equals(new DirectoryInfo(directory).Parent?.FullName, allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("V1 tests require a dedicated RimLayers/Tests/Artifacts/<run> profile.");
        if (GenCommandLine.TryGetCommandLineArg("rimlayersmode", out var value)) mode = value;
        if (mode != "generate" && mode != "load") throw new ArgumentException("Invalid V1 test mode.");
        if (GenCommandLine.TryGetCommandLineArg("rimlayerscase", out var testCase)) scenario = testCase;
        if (scenario != "aerocraft" && scenario != "collapse-perf" && scenario != "deep-resources" && scenario != "environment" && scenario != "environment-geology" && scenario != "environment-perf" && scenario != "vehicle-flight" && scenario != "vehicle-ramp" && scenario != "vehicle-lift" && scenario != "vehicle-ground" && scenario != "ship-landing-items" && scenario != "temperature" && scenario != "cargo" && scenario != "reinstall" && scenario != "jump" && scenario != "transition" && scenario != "ship-vge" && scenario != "shaft-extension" && scenario != "settings-behavior" && scenario != "settings-perf" && scenario != "settings-quota" && scenario != "settings-ui" && scenario != "settings-recovery" && scenario != "underground" && scenario != "combat-raid-audit" && scenario != "combat-raid-guns" && scenario != "combat-upper" && scenario != "combat-grenade" && scenario != "combat-raid-melee" && scenario != "combat-raid" && scenario != "combat-raid-isolated" && scenario != "combat-manned" && scenario != "combat-3d" && scenario != "ship-coexistence" && scenario != "mountains" && scenario != "ship-structure" && scenario != "elevators" && scenario != "stairs-bootstrap" && scenario != "expansion" && scenario != "gravship" && scenario != "corpse-refresh" && scenario != "command-input" && scenario != "reachability" && scenario != "presentation" && scenario != "usability" && scenario != "migration" && scenario != "world-exit" && scenario != "home" && scenario != "navigation-door" && scenario != "scale" && scenario != "lord" && scenario != "roping" && scenario != "mechanitor" && scenario != "navigation" && scenario != "navigation-maze" && scenario != "navigation-rules" && scenario != "work" && scenario != "work-save" && scenario != "work-container" && scenario != "work-haul" && scenario != "work-hunger" && scenario != "structure" && scenario != "skyfall" && scenario != "skyfall-shuttle" && scenario != "combat-geometry" && scenario != "combat-shot" && scenario != "combat-ai" && scenario != "combat-turret" && scenario != "combat-policy" && scenario != "combat-shields" && scenario != "combat-shield-save" && scenario != "combat-shield-pass" && scenario != "combat-explosion" && scenario != "combat-delay" && scenario != "combat-grenade-flee" && scenario != "combat-roof" && scenario != "combat-ballistics" && scenario != "combat-blast-effects" && scenario != "utilities" && scenario != "air" && scenario != "heat" && scenario != "sky" && scenario != "hazards" && scenario != "hazards-timed" && scenario != "minimap" && scenario != "dubs") throw new ArgumentException("Invalid V1 scenario.");
        Directory.CreateDirectory(directory);
        var h = new Harmony("rimlayers.core.v1test");
        if(scenario=="mountains")
        {
            h.Patch(AccessTools.Method(typeof(RimLayers.Mountains.JobDriver_VerticalMine),"ResetDelay"),postfix:new HarmonyMethod(typeof(V1Integration),nameof(ObserveMountainPickDelay)));
            h.Patch(AccessTools.Method(typeof(RimLayers.Mountains.JobDriver_VerticalMine),"MakeNewToils"),postfix:new HarmonyMethod(typeof(V1Integration),nameof(ObserveMountainWorkTicks)));
        }
        if(scenario=="collapse-perf")ConfigureCollapsePerf(h);
        if(scenario=="jump")ConfigureJumps(h); if(scenario=="gravship")ConfigureGravship(h);
        if(scenario=="ship-coexistence")ConfigureShipCoexistence(h);
        if(scenario=="reachability")ConfigureReachability(h);
        if(scenario=="presentation")ConfigurePresentation(h);
        if(scenario=="corpse-refresh")ConfigureCorpseRefresh(h);
        if(scenario=="scale")ConfigureScale(h);
        if(scenario=="world-exit")
        {
            h.Patch(AccessTools.Method(typeof(Pawn),nameof(Pawn.ExitMap)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(ObserveWorldExit)));
            h.Patch(AccessTools.Method(typeof(CaravanFormingUtility),nameof(CaravanFormingUtility.FormAndCreateCaravan)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(ObserveCaravanSend)));
        }
        if(scenario.StartsWith("vehicle-"))
        {
            h.Patch(AccessTools.Method(typeof(RimLayers.Vehicles.Building_VehicleConnector), nameof(Thing.DeSpawn)), prefix:new HarmonyMethod(typeof(V1Integration), nameof(VehicleConnectorRemoving)));
            h.Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)), postfix:new HarmonyMethod(typeof(V1Integration), nameof(VehicleRootProgress)));
            h.Patch(AccessTools.Method(typeof(Map), nameof(Map.FinalizeInit)), postfix:new HarmonyMethod(typeof(V1Integration), nameof(VehicleMapReady)));
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.InitNewGame)), postfix:new HarmonyMethod(typeof(V1Integration), nameof(VehicleGameReady)));
        }
        h.Patch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
            postfix: new HarmonyMethod(typeof(V1Integration), nameof(Setup)));
        h.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
            prefix: new HarmonyMethod(typeof(V1Integration), nameof(AllowTestTick)));
        h.Patch(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)),
            postfix: new HarmonyMethod(typeof(V1Integration), nameof(Update)));
        h.Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
            postfix: new HarmonyMethod(typeof(V1Integration), nameof(OnGUI)));
        h.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)), prefix: new HarmonyMethod(typeof(V1Integration), nameof(JobEnding)));
        h.Patch(AccessTools.Method(typeof(Pawn),"TickInterval",new[]{typeof(int)}), prefix: new HarmonyMethod(typeof(V1Integration),nameof(AllowCombatPawnTick)));
        h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage),new[]{typeof(DamageInfo)}), prefix: new HarmonyMethod(typeof(V1Integration),nameof(DamageArriving)));
        if(scenario is "combat-shield-save" or "combat-shield-pass")
        {
            h.Patch(AccessTools.Method(typeof(CompProjectileInterceptor),nameof(CompProjectileInterceptor.CheckIntercept)),
                postfix:new HarmonyMethod(typeof(V1Integration),nameof(ShieldDecisionObserved)){after=new[]{"rimlayers.core"},priority=Priority.Last});
            h.Patch(AccessTools.Method(typeof(Projectile),"DrawAt"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(ShieldFlightDrawObserved)));
        }
        if(scenario=="mechanitor")
        {
            h.Patch(AccessTools.Method(typeof(GenDraw),nameof(GenDraw.DrawRadiusRing),new[]{typeof(IntVec3),typeof(float),typeof(Color),typeof(Func<IntVec3,bool>)}),
                postfix:new HarmonyMethod(typeof(V1Integration),nameof(MechRadiusObserved)));
            h.Patch(AccessTools.Method(typeof(MapComponentUtility),nameof(MapComponentUtility.MapComponentOnDraw)),
                prefix:new HarmonyMethod(typeof(V1Integration),nameof(MechRenderBeginning)),
                postfix:new HarmonyMethod(typeof(V1Integration),nameof(MechRenderEnded)){priority=Priority.Last},
                finalizer:new HarmonyMethod(typeof(V1Integration),nameof(MechRenderEnded)));
        }
        h.Patch(AccessTools.Method(typeof(Skyfaller),"Impact"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(SkyImpactObserved)));
        h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
            postfix: new HarmonyMethod(typeof(V1Integration), nameof(Error)));
    }

    private static bool AllowTestTick() => !GenCommandLine.CommandLineArgPassed("rimlayerscorpsefixture") && (mode != "load" || stage != 0);

    private static void VehicleConnectorRemoving(RimLayers.Vehicles.Building_VehicleConnector __instance)=>Log.Message($"[RimLayers.VehicleLifecycle] removing {__instance.ThingID} at {__instance.Position}: {System.Environment.StackTrace}");
    private static long nextVehicleProgress;
    private static void VehicleRootProgress()
    {
        long now=System.Environment.TickCount64;
        if(now<nextVehicleProgress)return;
        nextVehicleProgress=now+15000;
        Log.Message($"[RimLayers.VehicleLifecycle] root state={Current.ProgramState} map={Find.CurrentMap?.uniqueID} longEvent={LongEventHandler.ShouldWaitForEvent} frame={frame} stage={stage} load={loadIssued}/{loadPending}");
    }
    private static void VehicleMapReady(Map __instance)=>Log.Message($"[RimLayers.VehicleLifecycle] FinalizeInit completed: {__instance.uniqueID}");
    private static void VehicleGameReady()=>Log.Message("[RimLayers.VehicleLifecycle] InitNewGame completed");

    private static void Setup()
    {
        // ship-vge honours -MapSize because the layer stride is derived from map height: a 120 map has
        // stride 136 while a 250 map has stride 255. Native CompGravshipFacility.CanBeActive measures
        // storage z (layer*stride+z), so on a 250 map a facility one deck above its engine sits 255
        // cells away and VGE's pilot-console maxDistance of 200 can never be satisfied. Testing only on
        // 120 hid that; ship-vge therefore defaults to the realistic 250.
        Find.GameInitData.mapSize = scenario=="collapse-perf"&&GenCommandLine.TryGetCommandLineArg("rimlayersmapsize",out var collapseSize)?int.Parse(collapseSize):scenario=="environment-perf"?250:scenario=="settings-perf"?250:scenario=="underground"&&GenCommandLine.TryGetCommandLineArg("rimlayersmapsize",out var undergroundSize)?int.Parse(undergroundSize):scenario=="scale"?scaleSize:scenario=="reachability"?250:scenario=="ship-vge"&&GenCommandLine.TryGetCommandLineArg("rimlayersmapsize",out var vgeSize)?int.Parse(vgeSize):120;
        foreach (var tile in Find.WorldGrid.Tiles)
            if (tile.PrimaryBiome == BiomeDefOf.TemperateForest && tile.hilliness == Hilliness.Flat &&
                RimWorld.Planet.TileFinder.IsValidTileForNewSettlement(tile.tile))
            { Find.GameInitData.startingTile = tile.tile; break; }
    }

    private static void JobEnding(Pawn_JobTracker __instance, JobCondition condition)
    {
        if(scenario=="vehicle-flight"&&AccessTools.Field(typeof(Pawn_JobTracker),"pawn").GetValue(__instance) is Pawn airActor&&RimLayers.Vehicles.VehicleCompatibility.Adapter?.IsVehicle(airActor)==true)
            Log.Message($"[RimLayers.FlightJob] job={airActor.CurJob}, condition={condition}, cell={airActor.Position}\n{System.Environment.StackTrace}");
        if(scenario.StartsWith("vehicle-"))
            foreach(var actor in new[]{testVehicle,secondLiftVehicle}.Where(p=>p!=null&&p.CurJobDef==JobDefOf.Goto&&ReferenceEquals(p.jobs,__instance)))
                Log.Message($"[RimLayers.VehicleTrace] vehicle={actor.ThingID}, job={actor.CurJob}, condition={condition}, cell={actor.Position}, trip={actor.MapHeld?.GetComponent<RimLayers.Vehicles.VehicleTraffic>().Trip(actor)?.Phase}\n{System.Environment.StackTrace}");
        if(scenario=="mountains"&&pawn!=null&&ReferenceEquals(pawn.jobs,__instance)&&pawn.CurJob?.def is {} d&&(d==JobDefOf.Mine||d.defName=="RL_MineVertical"))
            Log.Message($"[RimLayers.MountainTrace] {d.defName} end={condition},ticks={ticks},toil={pawn.jobs.curDriver?.CurToilIndex},pawn={pawn.Position},target={pawn.CurJob?.targetA},designation={map.designationManager.DesignationAt(pawn.CurJob!.targetA.Cell,DesignationDefOf.Mine)!=null},hp={pawn.CurJob?.targetA.Thing?.HitPoints}\n{System.Environment.StackTrace}");
        if (scenario == "work" && pawn != null && ReferenceEquals(pawn.jobs, __instance) &&
            ExpectedJobMatches())
            Log.Message("[RimLayers.V1] Bill ending=" + condition + ", toil=" + pawn.jobs.curDriver?.CurToilIndex +
                ", carry=" + pawn.carryTracker.CarriedThing + "\n" + System.Environment.StackTrace);
    }
    private static void Error(string text) { if (!finished) errors.Add(text); }
    private static void Check(string name, bool pass, string detail = "")
    {
        checks.Add(new { name, pass, detail });
        if (!pass) failures++;
        Log.Message($"[RimLayers.V1] {(pass ? "PASS" : "FAIL")} {name}: {detail}");
    }
    private static IntVec3 At(int layer, int x, int z) => owner.ToStorage(new LayerCell(layer, x, z));

    private static void Update()
    {
        if (finished || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null ||
            LongEventHandler.ShouldWaitForEvent || loadPending) return;
        try
        {
            if (mode == "load" && !loadIssued)
            { oldGame = Current.Game; loadPending = true; return; }
            if (mode == "load" && oldGame == Current.Game) return;
            if(scenario!="transition"||stage!=510)Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            frame++;
            if (frame < 20) return;
            if (screenshotHold>0)
            {
                if(--screenshotHold==0 && pendingScreenshot!=null)
                { ScreenCapture.CaptureScreenshot(pendingScreenshot);pendingScreenshot=null; }
                return;
            }
            map = Find.CurrentMap;
            owner = LayerMapComponent.For(map)!;
            if(owner==null&&!GenCommandLine.CommandLineArgPassed("rimlayersvehicleflat")&&!scenario.StartsWith("environment")&&scenario!="migration"&&scenario!="expansion"&&scenario!="stairs-bootstrap"&&scenario!="mountains"&&scenario!="underground"&&scenario!="shaft-extension"&&scenario!="deep-resources"&&scenario!="dubs")throw new Exception("No layer map.");
            if (stage == 0)
            {
                if (mode == "generate") { if(GenCommandLine.CommandLineArgPassed("rimlayerscorpsefixture")) BeginCorpseFixture();else Seed(); }
                else
                {
                    if(scenario=="aerocraft") { RestoreAerocraft(); }
                    else if(scenario=="environment") { RestoreEnvironment(); } else if(scenario=="vehicle-flight") { RestoreVehicleFlight(); } else if(scenario=="vehicle-ramp") { RestoreVehicleRamp(); } else if(scenario=="vehicle-lift") { RestoreVehicleLift(); } else if(scenario=="vehicle-ground") { RestoreVehicleGround(); } else if(scenario=="cargo") { RestoreCargo(); } else if(scenario=="temperature") { RestoreTemperature(); } else if(scenario=="jump") { RestoreJumps(); } else if(scenario=="transition") { RestoreTransitions(); }
                    else if(scenario=="settings-behavior") { RestoreSettingsBehavior(); }
                    else if(scenario=="settings-recovery") { RestoreSettingsRecovery(); }
                    else if(scenario=="combat-raid-audit") { AuditSavedRaid(); }
                    else if(scenario=="combat-grenade") { RestoreGrenadeLayers(); }
                    else if(scenario=="combat-manned") { RestoreManned(); }
                    else if(scenario=="mountains") { RestoreMountains(); }
                    else if(scenario=="shaft-extension") { RestoreShaftExpansion(); }
                    else if(scenario=="deep-resources") { RestoreDeepResources(); }
                    else if(scenario=="underground") { RestoreUnderground(); }
                    else if(scenario=="elevators") { RestoreElevators(); }
                    else if(scenario=="stairs-bootstrap") { RestoreStairBootstrap(); }
                    else if(scenario=="expansion") { RestoreExpansion(); }
                    else if(scenario=="gravship") { RestoreGravship(); }
                    else if(scenario=="ship-vge") { RestoreVge(); }
                    else if(scenario=="ship-coexistence") { RestoreShipCoexistence(); }
                    else if(scenario=="reachability") { RestoreReachability(); }
                    else if(scenario=="presentation") { RestorePresentation(); }
                    else if(scenario=="usability") { RestoreUsability(); }
                    else if(scenario=="migration") { RestoreMigration(); }
                    else if(scenario=="world-exit") { RestoreWorldExit(); }
                    else if(scenario=="home") { RestoreHome(); }
                    else if(scenario=="scale") { RestoreScale(); }
                    else if(scenario=="lord") { RestoreLord(); }
                    else if(scenario=="roping") { RestoreRoping(); }
                    else if(scenario=="mechanitor") { RestoreMechanitor(); }
                    else if (scenario == "navigation-maze") { RestoreMaze(); }
                    else if (scenario == "work-save") { RestoreBill(); }
                    else if (scenario == "work-container") { RestoreContainer(); }
                    else if (scenario == "skyfall") { RestoreSkyfall(); }
                    else if (scenario == "collapse-perf") { RestoreCollapsePerf(); }
                    else if (scenario == "structure") { RestoreStructure(); }
                    else if(scenario is "combat-shield-save" or "combat-shield-pass") { RestoreShieldBoundary(); }
                    else if(scenario=="combat-shot") { RestoreCombatShot(); }
                    else if(scenario=="combat-explosion") { RestoreBlastTests(); }
                    else if(scenario=="combat-delay") { RestoreDelayedBlast(); }
                    else if(scenario=="combat-grenade-flee") { RestoreGrenadeFlee(); }
                    else if(scenario=="utilities") { RestoreUtilities(); }
                    else if(scenario=="air") { RestoreAir(); }
                    else if(scenario=="heat") { RestoreHeat(); }
                    else if(scenario=="sky") { RestoreSky(); }
                    else if(scenario is "hazards" or "hazards-timed") { RestoreHazards(); }
                    else {
                    using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "navigation-baseline.json")));
                    string id = baseline.RootElement.GetProperty("pawn").GetString()!;
                    pawn = map.mapPawns.AllPawnsSpawned.Single(p => p.ThingID == id);
                    destination = At(2, 62, 62);
                    expectedJob = pawn.CurJob; expectedJobId = expectedJob?.loadID ?? -1;
                    Check("restart-keeps-final-target", pawn.CurJob?.targetA.Cell == destination);
                    stage = 3;
                    }
                }
            }
            if(scenario.StartsWith("vehicle-")&&screenshotHold>0)return;
            if(scenario=="transition") { TransitionsStep(); return; }
            for (int i = 0; i < 15 && !finished; i++)
            {
                Step();
                if(screenshotHold>0) return;
                if(scenario=="gravship"&&ShipVisual&&shipVisualLaunch!=null)return;
                if(scenario is "gravship" or "ship-coexistence"&&NativeShip&&WorldComponent_GravshipController.CutsceneInProgress)return;
                if (!finished)
                {
                    long stamp=(scenario=="scale"&&scaleSampling||scenario=="underground"&&undergroundPhase==5)?System.Diagnostics.Stopwatch.GetTimestamp():0;
                    if(scenario=="collapse-perf")CollapsePerfBeforeTick();
                    if(scenario=="environment-perf")EnvironmentPerfBeforeTick();
                    if(scenario=="settings-perf")SettingsPerfBeforeTick();
                    Find.TickManager.DoSingleTick();
                    if(scenario=="collapse-perf")CollapsePerfAfterTick();
                    if(scenario=="environment-perf")EnvironmentPerfAfterTick();
                    if(scenario=="settings-perf")SettingsPerfAfterTick();
                    if(stamp!=0){if(scenario=="underground")undergroundTickMs.Add(ScaleMilliseconds(stamp));else scaleTickMs.Add(ScaleMilliseconds(stamp));}
                    if(scenario=="scale"&&stage==240)ScaleProgress("after-native-tick");
                }
            }
        }
        catch (Exception e) { Check("unhandled-scenario-exception", false, e.ToString()); Finish(); }
    }

    private static void Seed()
    {
        int dlcCount=(ModsConfig.RoyaltyActive?1:0)+(ModsConfig.IdeologyActive?1:0)+(ModsConfig.BiotechActive?1:0)+
            (ModsConfig.AnomalyActive?1:0)+(ModsConfig.OdysseyActive?1:0);
        int expectedDlc=GenCommandLine.CommandLineArgPassed("rimlayersalldlc")?5:scenario=="temperature"&&GenCommandLine.CommandLineArgPassed("rimlayerstemperaturecore")?0:scenario is "temperature" or "jump" or "ship-landing-items" or "ship-coexistence" or "gravship" or "ship-structure" or "mechanitor" or "combat-shields" or "combat-shield-save" or "combat-shield-pass" or "work-container"?1:0;
        Check("test-profile-loads-requested-official-dlc",dlcCount==expectedDlc,$"expected={expectedDlc}, actual={dlcCount}");
        if(scenario.StartsWith("environment")){BeginEnvironment();return;}
        if(scenario=="vehicle-flight"){BeginVehicleFlight();return;}
        if(scenario=="vehicle-ground"&&GenCommandLine.CommandLineArgPassed("rimlayersvehicleflat")){BeginVehicleFlat();return;}
        if(scenario=="jump"){BeginJumps();return;} if(scenario=="settings-behavior"){BeginSettingsBehavior();return;}
        if(scenario=="settings-perf"){BeginSettingsPerf();return;}
        if(scenario=="settings-quota"){BeginSettingsQuota();return;}
        if(scenario=="settings-ui"){BeginSettingsUi();return;}
        if(scenario=="settings-recovery"){BeginSettingsRecovery();return;}
        if(scenario=="shaft-extension"){BeginShaftExpansion();return;}
        if(scenario=="deep-resources"){BeginDeepResources();return;}
        if(scenario=="underground"){BeginUnderground();return;}
        if(scenario=="mountains"){BeginMountains();return;}
        if(scenario=="scale"){SeedScale();return;}
        if(scenario=="stairs-bootstrap"){BeginStairBootstrap();return;}
        if(scenario=="expansion"){pawn=map.mapPawns.FreeColonistsSpawned.First();BeginExpansion();return;}
        if(scenario=="aerocraft"){BeginAerocraft();return;}
        if(scenario=="dubs"){BeginDubs();return;}
        foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
        {
            p.jobs.StopAll();
            p.drafter.Drafted = true;
        }
        pawn = scenario=="work-container" ? map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Hauling"))) : scenario.StartsWith("combat") ?
            map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTagIsDisabled(WorkTags.Violent)) :
            map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking"))) ?? map.mapPawns.FreeColonistsSpawned.First();
        foreach (IntVec3 c in new CellRect(48, 48, 20, 20))
        {
            foreach (Thing t in c.GetThingList(map).ToList()) if (t is not Pawn) { if (t.def.destroyable) t.Destroy(DestroyMode.Vanish); else t.DeSpawn(); }
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
        }
        // Clearing generated ruins can leave already-scheduled native roof impacts. They belong
        // to the erased fixture, not the newly built supported roof. Do not suppress later events.
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c => new CellRect(48,48,20,20).Contains(c));
        for (int layer = 0; layer < 2; layer++)
        {
            if (layer == 1)
                foreach (IntVec3 c in new CellRect(50, 50, 15, 15))
                    map.terrainGrid.SetTerrain(At(1, c.x, c.z), RimLayersDefOf.RL_StructuralFloor);
            for (int x = 50; x <= 64; x += 6)
                for (int z = 50; z <= 64; z += 6)
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column, ThingDefOf.Steel), At(layer, x, z), map);
            foreach (IntVec3 c in new CellRect(50, 50, 15, 15))
                map.roofGrid.SetRoof(At(layer, c.x, c.z), RoofDefOf.RoofConstructed);
        }
        foreach (IntVec3 c in new CellRect(50, 50, 15, 15))
            map.terrainGrid.SetTerrain(At(2, c.x, c.z), RimLayersDefOf.RL_StructuralFloor);
        var connector=GenCommandLine.CommandLineArgPassed("rimlayersliftwork")?DefDatabase<ThingDef>.GetNamed("RL_LiftSingle"):RimLayersDefOf.RL_Stairs;
        GenSpawn.Spawn(connector, At(0, 52, 52), map);
        GenSpawn.Spawn(connector, At(0, 62, 52), map);
        GenSpawn.Spawn(connector, At(1, 60, 60), map);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        pawn.drafter.Drafted = false;
        pawn.jobs.StopAll();
        pawn.Position = At(0, 54, 54);
        pawn.pather.ResetToCurrentPosition();
        pawn.Drawer.tweener.Notify_Teleported();
        if(scenario=="corpse-refresh") { BeginCorpseRefresh();return; }
        if(scenario is "vehicle-ground" or "vehicle-lift" or "vehicle-ramp") { BeginVehicleGround();return; }
        if(scenario=="cargo") { BeginCargo();return; }
        if(scenario=="temperature") { BeginTemperature();return; }
        if(scenario=="reinstall") { BeginReinstall();return; }
        if(scenario=="command-input") { BeginCommandInput();return; }
        if(scenario=="elevators") { BeginElevators();return; }
        if(scenario is "gravship" or "ship-structure") { BeginGravship();return; }
        if(scenario=="ship-vge") { BeginVge();return; }
        if(scenario=="ship-landing-items") { BeginShipLandingItems();return; }
        if(scenario=="ship-coexistence") { BeginShipCoexistence();return; }
        if(scenario=="migration") { BeginMigration();return; }
        if(scenario=="reachability") { BeginReachability();return; }
        if(scenario=="transition") { BeginTransitions();return; }
        if(scenario=="presentation") { BeginPresentation();return; }
        if(scenario=="usability") { BeginUsability();return; }
        if(scenario=="world-exit") { BeginWorldExit();return; }
        if(scenario=="home") { BeginHome();return; }
        if(scenario=="navigation-door") { BeginDoor();return; }
        if(scenario=="lord") { BeginLord();return; }
        if(scenario=="roping") { BeginRoping();return; }
        if(scenario=="mechanitor") { BeginMechanitor();return; }
        if (scenario == "navigation-rules") { BeginNavigationRules(); return; }
        if (scenario == "navigation-maze") { BeginMaze(); return; }
        if (scenario == "utilities") { BeginUtilities(); return; }
        if (scenario == "air") { BeginAirTests(); return; }
        if (scenario == "heat") { BeginHeatTests(); return; }
        if (scenario == "sky") { BeginSkyTests(); return; }
        if (scenario is "hazards" or "hazards-timed") { BeginHazards(); return; }
        if (scenario == "minimap") { BeginMinimap(); return; }
        if (scenario == "work-haul") { BeginHaulIntegrity(); return; }
        if (scenario == "work-container") { BeginContainer(); return; }
        if (scenario is "work" or "work-save" or "work-hunger") { BeginBill(); return; }
        if (scenario == "skyfall-shuttle") { BeginShuttleFall(); return; }
        if (scenario == "skyfall") { BeginSkyfall(); return; }
        if (scenario == "collapse-perf") { BeginCollapsePerf(); return; }
        if (scenario == "structure") { BeginStructure(); return; }
        if (scenario == "combat-geometry") { CheckCombatGeometry(); return; }
        if(scenario=="combat-shot") { BeginCombatShot(); return; }
        if(scenario=="combat-ai") { BeginCombatAI();return; }
        if(scenario is "combat-shield-save" or "combat-shield-pass") { BeginShieldBoundary();return; }
        if(scenario=="combat-turret") { BeginTurret();return; }
        if(scenario=="combat-3d") { BeginCombat3D();return; }
        if(scenario=="combat-manned") { BeginManned();return; }
        if(scenario.StartsWith("combat-raid")) { BeginRaid();return; }
        if(scenario=="combat-grenade") { BeginGrenadeLayers();return; }
        if(scenario=="combat-upper") { BeginUpperCombat();return; }
        if(scenario=="combat-policy") { BeginCombatPolicy();return; }
        if(scenario=="combat-shields") { BeginShieldTests();return; }
        if(scenario=="combat-explosion") { BeginBlastTests();return; }
        if(scenario=="combat-delay") { BeginDelayedBlast();return; }
        if(scenario=="combat-grenade-flee") { BeginGrenadeFlee();return; }
        if(scenario=="combat-roof") { BeginRoofCombat();return; }
        if(scenario=="combat-ballistics") { BeginBallisticTests();return; }
        if(scenario=="combat-blast-effects") { BeginBlastEffects();return; }
        destination = At(2, 62, 62);
        Log.Message("[RimLayers.V1] connectors=" + string.Join("; ", owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Select(e => $"{e.From}->{e.To}")));
        Log.Message($"[RimLayers.V1] surfaces={owner.StructuralCells.Count}, startWalk={pawn.Position.Walkable(map)}, targetWalk={destination.Walkable(map)}");
        using (var path = map.pathFinder.FindPathNow(pawn.Position, destination, pawn))
        {
            Check("three-layer-path-found", path.Found);
            var layers = path.NodesReversed.Select(c => { owner.TryDecode(c, out var logical); return logical.LayerId; }).Distinct().OrderBy(x => x);
            Check("path-visits-middle-layer", layers.SequenceEqual(new[] { 0, 1, 2 }));
            Check("path-keeps-final-destination", path.Found && path.LastNode == destination);
            if (path.Found && LayerRouter.HasMetadata(path, out var tag))
                removedStair = tag.Steps.Values.FirstOrDefault(e => owner.IsGroundCell(e.From)).Stairs;
        }
        Check("reachability-matches-route", pawn.CanReach(destination, PathEndMode.OnCell, Danger.Deadly));
        Check("connector-regions-not-merged", At(0, 52, 52).GetRegion(map) != null && At(1, 52, 52).GetRegion(map) != null &&
            At(0, 52, 52).GetRegion(map) != At(1, 52, 52).GetRegion(map));
        expectedJob = JobMaker.MakeJob(JobDefOf.Goto, destination);
        pawn.jobs.TryTakeOrderedJob(expectedJob); expectedJobId = expectedJob.loadID;
        stage = 1; ticks = 0; visited.Clear();
    }

    private static void Step()
    {
        if(stage==580){AerocraftStep();return;}
        if(stage==570){MinimapStep();return;}
        if(stage==562){CollapsePerfStep();return;}
        if(stage==560){EnvironmentStep();return;}
        if(stage==561){EnvironmentPerfStep();return;}
        if(stage==554){VehicleFlightStep();return;}
        if(stage==553){VehicleFlatStep();return;}
        if(stage==552){VehicleRampStep();return;}
        if(stage==551){VehicleLiftStep();return;}
        if(stage==550){VehicleGroundStep();return;}
        if(scenario=="cargo"){CargoStep();return;}
        if(stage==540){TemperatureStep();return;}
        if(scenario=="jump"){JumpStep();return;} if(stage==460){SettingsPerfStep();return;}
        if(stage==450){SettingsUiStep();return;}
        if(stage==440){ticks++;SettingsRecoveryStep();return;}
        if(stage==490){ticks++;ShaftExpansionStep();return;}
        if(stage==430){ticks++;UndergroundStep();return;}
        if(stage==431){ticks++;DeepStep();return;}
        if(stage==382){if(screenshotHold==0)ContinueShipCoexistence();return;}
        if(stage==381){ShipCoexistLandingStep();return;}
        if(stage==380){ticks++;if(ticks>20)Finish();return;}
        if(stage==370){ticks++;MountainStep();return;}
        if(stage==361){ticks++;ElevatorReturnsLoadStep();return;}
        if(stage==360){ticks++;ElevatorStep();return;}
        if(stage==351){ticks++;StairBootstrapLoadStep();return;}
        if(stage==350){ticks++;StairBootstrapStep();return;}
        if(stage==340){ticks++;ExpansionStep();return;}
        if(stage==320){CorpseRefreshStep();return;}
        if(stage==330){ticks++;GravshipStep();return;}
        if(stage==520){ReinstallStep();return;}
        if(stage==310){CommandInputStep();return;}
        if(stage==290){PresentationStep();return;}
        ticks++;
        if(stage==300){ReachabilityStep();return;}
        if(stage==280) { UsabilityStep();return; }
        if(stage==282) { MigrationStep();return; }
        if(stage==270) { WorldExitStep();return; }
        if(stage==260) { DoorStep();return; }
        if(stage==250) { HomeBuildStep();return; }
        if(stage==251) { HomeJoyStep();return; }
        if(stage==240) { ScaleStep();return; }
        if(stage==230) { LordStep();return; }
        if(stage==222) { BeginRopeRecovery(0);return; }
        if(stage==221) { RopeRecoveryStep();return; }
        if(stage==220) { RopingStep();return; }
        if(stage is >=210 and <=215) { MechanitorStep();return; }
        if(stage==200) { TurretStep();return; }
        if(stage==410) { RaidStep();return; }
        if(stage==412) { GrenadeLayersStep();return; }
        if(stage==413) { RaidRecoveryStep();return; }
        if(stage>=400&&stage<=403) { MannedStep();return; }
        if(stage==190) { ShieldBoundaryStep();return; }
        if(stage is 180 or 181) { GrenadeFleeStep();return; }
        if(stage is 170 or 171 or 172 or 173) { ShuttleFallStep();return; }
        if(stage is 160 or 161) { SkyfallStep();return; }
        if(stage==150) { NavigationRulesStep();return; }
        if(stage is >=130 and <=134) { HaulIntegrityStep();return; }
        if(stage is 140 or 141) { HungerStep();return; }
        if(stage is 120 or 121) { ContainerStep();return; }
        if(stage==110) { MazeStep();return; }
        if(stage==70) { AirStep();return; }
        if(stage==80) { HeatStep();return; }
        if(stage==90) { SkyStep();return; }
        if(stage is 100 or 101 or 102) { HazardsStep();return; }
        if(stage is 60 or 61 or 62 or 64) { UtilitiesStep();return; }
        if(stage is 50 or 51 or 52) { DelayedBlastStep();return; }
        if(stage is 40 or 41) { BlastStep();return; }
        if(stage is 20 or 21 or 22) { CombatShotStep();return; }
        if(stage is 30 or 31) { CombatAIStep();return; }
        if (stage == 10) { StepBill(); return; }
        if (stage == 12) { StepConstruction(); return; }
        if (stage == 14) { StepHaul(); return; }
        if (stage == 13) { if (ticks > 30) CheckCollapse(); return; }
        if (stage == 11) { if (ticks > 50) Finish(); return; }
        if (owner.TryDecode(pawn.Position, out var logical)) visited.Add(logical.LayerId);
        if (pawn.Position != destination && expectedJob != null && !ExpectedJobMatches())
            retainedJob = false;
        if (stage == 1)
        {
            // Remove the actual chosen connector only after the path has been installed.
            if (removedStair != null && pawn.pather.Moving && pawn.pather.curPath != null &&
                owner.TryDecode(pawn.pather.nextCell, out var next) && next.LayerId != logical.LayerId)
            {
                removedStair.Destroy(DestroyMode.Vanish);
                removedStair = null;
                stage = 2;
            }
        }
        if (stage is 1 or 2)
        {
            if (pawn.Position == destination)
            {
                Check("three-layer-goto-arrives", true, ticks.ToString());
                Check("same-job-through-stairs-and-replan", retainedJob);
                Check("middle-layer-was-actually-entered", visited.SetEquals(new[] { 0, 1, 2 }));
                Check("removed-connector-was-not-used", stage == 2);
                BeginSaveTrip();
            }
            else if (ticks > 3500)
            {
                Check("three-layer-goto-arrives", false, DescribePawn());
                Finish();
            }
        }
        else if (stage == 3)
        {
            if (mode == "generate" && pawn.pather.Moving && owner.TryDecode(pawn.pather.nextCell, out var next) &&
                next.LayerId != logical.LayerId && pawn.pather.nextCellCostLeft < pawn.pather.nextCellCostTotal)
            {
                GameDataSaveLoader.SaveGame("RimLayers_V1Navigation");
                File.WriteAllText(Path.Combine(directory, "navigation-baseline.json"), JsonSerializer.Serialize(new { pawn = pawn.ThingID }));
                Check("saved-during-cross-layer-step", true);
                LayerView.Switch(owner, logical.LayerId);
                Find.CameraDriver.SetRootPosAndSize(pawn.Position.ToVector3Shifted(), 12);
                stage = 4; ticks = 0;
            }
            else if (mode == "load" && pawn.Position == destination)
            {
                Check("restart-mid-step-arrives", true, ticks.ToString());
                Check("restart-retains-job", retainedJob);
                stage = 4; ticks = 0;
            }
            else if (ticks > 3500) { Check("mid-step-save-or-resume", false, DescribePawn()); Finish(); }
        }
        else if (stage == 4 && ticks > 30)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(directory, mode + "-navigation.png"));
            stage = 5; ticks = 0;
        }
        else if (stage is 5 or 11 && ticks > 50) Finish();
    }

    private static void BeginSaveTrip()
    {
        pawn.jobs.StopAll();
        pawn.Position = At(0, 54, 54);
        pawn.pather.ResetToCurrentPosition();
        pawn.Drawer.tweener.Notify_Teleported();
        expectedJob = JobMaker.MakeJob(JobDefOf.Goto, destination);
        pawn.jobs.TryTakeOrderedJob(expectedJob); expectedJobId = expectedJob.loadID;
        retainedJob = true; stage = 3; ticks = 0;
    }


    private static void BeginBill()
    {
        pawn.skills.GetSkill(SkillDefOf.Cooking).Level = 15;
        if (pawn.needs.food != null) pawn.needs.food.CurLevelPercentage = 1;
        if (pawn.needs.rest != null) pawn.needs.rest.CurLevelPercentage = 1;
        foreach (WorkTypeDef type in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            pawn.workSettings.SetPriority(type, 0);
        pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking"), 1);
        if (pawn.needs.joy != null) pawn.needs.joy.CurLevelPercentage = 1;
        var stoveDef = DefDatabase<ThingDef>.GetNamed("FueledStove");
        var stove = (Building_WorkTable)ThingMaker.MakeThing(stoveDef);
        GenSpawn.Spawn(stove, At(2, 60, 61), map, Rot4.North);
        stove.GetComp<CompRefuelable>().Refuel(30f);
        bill = new Bill_Production(DefDatabase<RecipeDef>.GetNamed("CookMealSimple"), null);
        bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
        bill.repeatCount = 1;
        bill.ingredientSearchRadius = 12f;
        bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor, null);
        stove.BillStack.AddBill(bill);
        foreach (int layer in new[] { 0, 1 })
        {
            Thing food = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RawPotatoes"));
            food.stackCount = 5;
            GenSpawn.Spawn(food, At(layer, 55 + layer, 56), map);
            workIngredients.Add(food);
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Job? job = new JobGiver_Work().TryIssueJobPackage(pawn, default).Job;
        Check("native-work-scheduler-selects-third-layer-stove", job?.def==JobDefOf.DoBill && job.targetA.Thing==stove);
        Check("native-bill-finds-cross-layer-ingredients", job?.def == JobDefOf.DoBill,
            job == null ? JobFailReason.Reason : job.def.defName);
        Check("bill-keeps-real-table", job?.targetA.Thing == stove);
        Check("bill-queues-both-stacks", job?.targetQueueB?.Count == 2 &&
            job.countQueue?.Sum() == 10);
        if (job == null) { Finish(); return; }
        expectedJob = job;
        if(scenario=="work-hunger")pawn.jobs.StartJob(job,JobCondition.InterruptForced);else pawn.jobs.TryTakeOrderedJob(job); expectedJobId = job.loadID;
        retainedJob = true; pickups = 0; ticks = 0; stage = scenario=="work-hunger"?140:10; visited.Clear();
        LayerView.Switch(owner, 2);
        Find.CameraDriver.SetRootPosAndSize(stove.Position.ToVector3Shifted(), 12);
    }

    private static void StepBill()
    {
        if (owner.TryDecode(pawn.Position, out var logical)) visited.Add(logical.LayerId);
        Thing? carry = pawn.carryTracker.CarriedThing;
        if (carry != null && !ReferenceEquals(carry, lastCarry)) pickups++;
        lastCarry = carry;
        SaveBillIfCrossing(carry);
        if (bill!.repeatCount == 0)
        {
            Check("bill-multiple-pickups", pickups >= 2, pickups.ToString());
            Check("bill-same-job-through-all-trips", retainedJob);
            Check("bill-actually-visits-three-layers", visited.SetEquals(new[] {0,1,2}));
            Check("bill-consumes-exactly-ten", workIngredients.All(t => t.Destroyed));
            int products = map.listerThings.ThingsOfDef(ThingDefOf.MealSimple).Sum(t => t.stackCount);
            Check("bill-produces-once", products == 1, products.ToString());
            ScreenCapture.CaptureScreenshot(Path.Combine(directory, "work.png"));
            BeginHaul();
        }
        else if (!ExpectedJobMatches())
        {
            retainedJob = false;
            Check("bill-job-not-interrupted", false, DescribePawn() + ", toil=" + pawn.jobs.curDriver?.CurToilIndex);
            Finish();
        }
        else if (ticks > 8000)
        { Check("bill-completes", false, DescribePawn() + ", toil=" + pawn.jobs.curDriver?.CurToilIndex); Finish(); }
    }



    private static void BeginHaul()
    {
        // A native production-to-storage handoff is allowed to create a normal haul job.
        pawn.jobs.StopAll();
        productToHaul = map.listerThings.ThingsOfDef(ThingDefOf.MealSimple).Single();
        var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
        map.zoneManager.RegisterZone(zone);
        storageCell = At(1,55,60);
        zone.AddCell(storageCell);
        zone.GetStoreSettings().filter.SetDisallowAll();
        zone.GetStoreSettings().filter.SetAllow(ThingDefOf.MealSimple, true);
        zone.GetStoreSettings().Priority = StoragePriority.Critical;
        var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
        GenSpawn.Spawn(bed, At(1,54,60), map, Rot4.North);
        bed.SetFaction(Faction.OfPlayer);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("native-bed-query-finds-other-layer", RestUtility.FindBedFor(pawn) == bed);
        var giver = (WorkGiver_HaulGeneral)DefDatabase<WorkGiverDef>.GetNamed("HaulGeneral").Worker;
        Job? haul = giver.JobOnThing(pawn, productToHaul, true);
        Check("native-haul-selects-cross-layer-store", haul != null && haul.targetB.Cell == storageCell,
            haul == null ? JobFailReason.Reason : haul.targetB.ToString());
        if (haul == null) { Finish(); return; }
        expectedJob = haul;
        retainedJob = true;
        pawn.jobs.TryTakeOrderedJob(haul); expectedJobId = haul.loadID;
        stage = 14; ticks = 0;
    }
    private static void StepHaul()
    {
        if (productToHaul!.Spawned && productToHaul.Position == storageCell)
        {
            Check("native-haul-stores-real-product-once", productToHaul.stackCount == 1 &&
                map.listerThings.ThingsOfDef(ThingDefOf.MealSimple).Sum(t=>t.stackCount) == 1);
            Check("haul-keeps-job-through-stairs", retainedJob);
            // Search from the third layer after the product has been stored on the second.
            pawn.jobs.StopAll();
            pawn.Position = At(2,60,60); pawn.Notify_Teleported();
            bool foodFound = FoodUtility.TryFindBestFoodSourceFor(pawn,pawn,false,out var food,out _,canUseInventory:false,
                allowCorpse:false,forceScanWholeMap:true);
            Check("native-food-query-finds-other-layer", foodFound && food == productToHaul, food?.ThingID ?? "none");
            stage = 11; ticks = 0;
        }
        else if (!ExpectedJobMatches() || ticks > 4000)
        {
            retainedJob = false;
            Check("native-haul-completes",false,DescribePawn()); Finish();
        }
    }

    private static void RestoreStructure()
    {
        RimLayersMod.Settings.RecoveryMode=RecoveryPreference.ResetFirst;
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "structure-baseline.json")));
        var data = baseline.RootElement;
        pawn = map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("pawn").GetString());
        fallingItem = map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("item").GetString());
        heldItem = pawn.carryTracker.CarriedThing ?? map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("held").GetString());
        fallingFrame = (Frame)map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("frame").GetString());
        steelBefore = data.GetProperty("steelBefore").GetInt32();
        Check("restart-restores-pending-collapse-facts",owner.PendingCollapse.Count>=4);
        Check("restart-restores-carry-owner-once", heldItem.ThingID==data.GetProperty("held").GetString() &&
            ReferenceEquals(pawn.carryTracker.CarriedThing,heldItem));
        stage=13; ticks=0;
    }

    private static Frame FrameFor(BuildableDef def, IntVec3 cell, int steel)
    {
        var frame = (Frame)ThingMaker.MakeThing(def.frameDef, def is ThingDef { MadeFromStuff: true } ? ThingDefOf.Steel : null);
        GenSpawn.Spawn(frame, cell, map);
        frame.SetFaction(Faction.OfPlayer);
        Thing material = ThingMaker.MakeThing(ThingDefOf.Steel);
        material.stackCount = steel;
        frame.resourceContainer.TryAdd(material);
        return frame;
    }

    private static void BeginStructure()
    {
        pawn.skills.GetSkill(SkillDefOf.Construction).Level = 20;
        if (pawn.needs.food != null) pawn.needs.food.CurLevelPercentage = 1;
        if (pawn.needs.rest != null) pawn.needs.rest.CurLevelPercentage = 1;
        var stove = DefDatabase<ThingDef>.GetNamed("FueledStove");
        Check("rotated-whole-footprint-valid", owner.Structure.Validate(stove, At(2, 58, 59), Rot4.East).Accepted);
        IntVec3 missing = GenAdj.OccupiedRect(At(2, 58, 59), Rot4.East, stove.Size).First();
        owner.TryDecode(missing, out var mc);
        IntVec3 roof = At(1, mc.X, mc.Z);
        map.roofGrid.SetRoof(roof, null);
        Check("rotated-partial-roof-rejected", !owner.Structure.Validate(stove, At(2, 58, 59), Rot4.East).Accepted);
        map.roofGrid.SetRoof(roof, RoofDefOf.RoofConstructed);
        Frame guard = FrameFor(ThingDefOf.Wall, At(2, 54, 57), 5);
        guard.workDone = guard.WorkToBuild;
        map.roofGrid.SetRoof(At(1, 54, 57), null);
        guard.CompleteConstruction(pawn);
        Check("commit-guard-preserves-frame", guard.Spawned && !guard.Destroyed);
        Check("commit-guard-preserves-materials", guard.resourceContainer.TotalStackCount == 5);
        map.roofGrid.SetRoof(At(1, 54, 57), RoofDefOf.RoofConstructed);
        guard.CompleteConstruction(pawn);
        Check("valid-commit-creates-one-building", guard.Destroyed && At(2,54,57).GetEdifice(map)?.def == ThingDefOf.Wall);
        At(2,54,57).GetEdifice(map)?.Destroy(DestroyMode.Vanish);
        // Expand one tile into open air from an adjacent existing upper platform.
        map.roofGrid.SetRoof(At(0,65,58), RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(1,65,58), RimLayersDefOf.RL_StructuralFloor);
        map.roofGrid.SetRoof(At(1,65,58), RoofDefOf.RoofConstructed);
        Check("new-slab-uses-legal-neighbor", owner.Structure.Validate(RimLayersDefOf.RL_StructuralFloor, At(2,65,58), Rot4.North).Accepted &&
            !owner.Structure.HasSurface(At(2,65,58)));
        constructionFrame = FrameFor(RimLayersDefOf.RL_StructuralFloor, At(2,65,58), 4);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var giver = (WorkGiver_ConstructFinishFrames)DefDatabase<WorkGiverDef>.GetNamed("ConstructFinishFrames").Worker;
        var job = giver.JobOnThing(pawn, constructionFrame, true);
        Check("native-builder-can-expand-into-air", job != null, JobFailReason.Reason);
        if (job == null) { Finish(); return; }
        expectedJob = job;
        pawn.jobs.TryTakeOrderedJob(job); expectedJobId = job.loadID;
        stage = 12; ticks = 0;
    }

    private static void StepConstruction()
    {
        if (constructionPhase == 0 && constructionFrame!.Spawned && constructionFrame.workDone > 0)
        {
            // Force the next actual construction tick to be the final tick, then remove its roof.
            constructionFrame.workDone = constructionFrame.WorkToBuild - 0.01f;
            map.roofGrid.SetRoof(At(1,65,58), null);
            constructionPhase = 1; constructionBlockedAt = ticks;
            return;
        }
        if (constructionPhase == 1)
        {
            // 1.7 Pawn TickInterval is not guaranteed on every global tick.
            if (ExpectedJobMatches() && ticks-constructionBlockedAt<120) return;
            Check("last-work-tick-keeps-frame-and-materials", constructionFrame!.Spawned &&
                constructionFrame.resourceContainer.TotalStackCount == 4);
            Check("blocked-work-tick-does-not-succeed", !ExpectedJobMatches() &&
                !owner.Structure.HasSurface(At(2,65,58)), $"elapsed={ticks-constructionBlockedAt}, job={pawn.CurJob?.def?.defName}");
            Check("blocked-work-releases-frame-reservation", !map.reservationManager.IsReservedByAnyoneOf(constructionFrame,Faction.OfPlayer));
            map.roofGrid.SetRoof(At(1,65,58), RoofDefOf.RoofConstructed);
            var giver = (WorkGiver_ConstructFinishFrames)DefDatabase<WorkGiverDef>.GetNamed("ConstructFinishFrames").Worker;
            expectedJob = giver.JobOnThing(pawn,constructionFrame,true);
            Check("restored-support-can-resume-construction", expectedJob!=null);
            if(expectedJob==null) { Finish(); return; }
            pawn.jobs.TryTakeOrderedJob(expectedJob); expectedJobId = expectedJob.loadID;
            constructionPhase=2;
            return;
        }
        if (constructionFrame!.Destroyed && owner.Structure.HasSurface(At(2,65,58)))
        {
            Check("native-builder-finishes-slab-from-neighbor", true, ticks.ToString());
            BeginCollapse();
        }
        else if (!ExpectedJobMatches() || ticks > 5000)
        {
            Check("native-builder-finishes-slab-from-neighbor", false, DescribePawn());
            Finish();
        }
    }

    private static void BeginCollapse()
    {
        RimLayersMod.Settings.RecoveryMode=RecoveryPreference.ResetFirst;
        pawn.jobs.StopAll();
        pawn.Position = At(2,55,58);
        pawn.Notify_Teleported();
        var stool = ThingMaker.MakeThing(ThingDefOf.Stool, ThingDefOf.WoodLog);
        GenSpawn.Spawn(stool, pawn.Position, map);
        fallingFrame = FrameFor(ThingDefOf.Wall, At(2,56,58), 5);
        heldItem = ThingMaker.MakeThing(ThingDefOf.WoodLog);
        heldItem.stackCount = 3;
        GenSpawn.Spawn(heldItem, pawn.Position, map);
                Check("fixture-starts-carrying-real-stack", pawn.carryTracker.TryStartCarry(heldItem, 3, reserve: false) == 3 &&
            ReferenceEquals(pawn.carryTracker.CarriedThing, heldItem));
        fallingItem = ThingMaker.MakeThing(ThingDefOf.Silver);
        fallingItem.stackCount = 11;
        GenSpawn.Spawn(fallingItem, pawn.Position, map);
        Check("fixture-loose-stack-stays-in-collapse-cell", fallingItem.Spawned && fallingItem.Position==pawn.Position);
        steelBefore = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount);
        foreach (int x in new[]{55,56})
            map.roofGrid.RemoveRoofUnsafe(map.cellIndices.CellToIndex(At(0,x,58)));
        Check("unsafe-roof-removal-invalidates-entire-column", !At(1,55,58).Walkable(map) && !At(2,55,58).Walkable(map));
        Check("collapse-queue-records-all-invalid-slabs", new[]{At(1,55,58),At(2,55,58),At(1,56,58),At(2,56,58)}.All(owner.PendingCollapse.Contains));
        GameDataSaveLoader.SaveGame("RimLayers_V1Structure");
        File.WriteAllText(Path.Combine(directory,"structure-baseline.json"), JsonSerializer.Serialize(new {
            pawn=pawn.ThingID,item=fallingItem.ThingID,held=heldItem.ThingID,frame=fallingFrame.ThingID,steelBefore }));
        Check("saved-before-collapse-queue-commit",true);
        stage = 13; ticks = 0;
    }

    private static void CheckCollapse()
    {
        Check("normal-collapse-applies-eight-per-level-once-despite-safe-reset-mode",structuralFallHits==1&&Math.Abs(structuralFallDamage-16f)<.001f,$"hits={structuralFallHits},damage={structuralFallDamage}");
        Check("cascade-removes-slabs-and-their-roofs", new[]{At(1,55,58),At(2,55,58),At(1,56,58),At(2,56,58)}
            .All(c=>map.terrainGrid.TerrainAt(c)==RimLayersDefOf.RL_OpenAir && map.roofGrid.RoofAt(c)==null));
        Check("fall-keeps-pawn-and-single-owner-of-carried-stack", pawn.Spawned && owner.IsGroundCell(pawn.Position) &&
            heldItem!.stackCount==3 && heldItem.MapHeld==map && owner.IsGroundCell(heldItem.PositionHeld) &&
            (ReferenceEquals(pawn.carryTracker.CarriedThing, heldItem) || heldItem.Spawned),
            $"pawn={pawn.Position}, spawned={pawn.Spawned}, dead={pawn.Dead}, held={heldItem}, holder={heldItem?.holdingOwner?.Owner}, carry={pawn.carryTracker.CarriedThing}");
        Check("fall-keeps-loose-stack-once", fallingItem!.Spawned && owner.IsGroundCell(fallingItem.Position) && fallingItem.stackCount==11, $"item={fallingItem}, spawned={fallingItem.Spawned}, pos={fallingItem.Position}");
        Check("unsupported-frame-cancelled", fallingFrame!.Destroyed);
        int after = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount);
        Check("frame-refunds-real-materials-and-slab-salvage-once", after==steelBefore+9, $"before={steelBefore}, after={after}");
        Check("collapse-queue-drained", owner.PendingCollapse.Count==0, owner.PendingCollapse.Count.ToString());
        CheckDismantle();
        stage=11; ticks=0;
    }



    private static void CheckCombatGeometry()
    {
        Thing target=ThingMaker.MakeThing(ThingDefOf.Steel);
        GenSpawn.Spawn(target,At(0,70,60),map);
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        var solution=owner.Combat.Solve(pawn,pawn.Position,target,20);
        Check("edge-fire-clears-real-floor-stack",solution.GeometricallyPossible);
        Check("aim-keeps-real-target-layer",solution.AimCell.LayerId==0 && target.Position==At(0,70,60));
        Check("range-uses-logical-plane-not-storage-band",!owner.Combat.Solve(pawn,pawn.Position,target,5).InRange);
        Thing inside=ThingMaker.MakeThing(ThingDefOf.WoodLog);
        GenSpawn.Spawn(inside,At(0,64,60),map);
        solution=owner.Combat.Solve(pawn,At(2,62,60),inside,20);
        Check("interior-fire-blocked-by-real-upper-slab",solution.InRange && !solution.Clear &&
            solution.Obstruction?.Kind==ObstructionKind.Floor);
        Thing wall=ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel);
        GenSpawn.Spawn(wall,At(0,69,60),map);
        solution=owner.Combat.Solve(pawn,pawn.Position,target,20);
        Check("actual-wall-is-nearest-obstruction",!solution.Clear && solution.Obstruction?.ThingKey==wall.thingIDNumber);
        Check("wall-itself-can-be-geometric-target",owner.Combat.Solve(pawn,pawn.Position,wall,20).GeometricallyPossible);
        wall.Destroy(DestroyMode.Vanish);
        // Walking is independent: after removing the only upper stair, a clear shot still exists.
        foreach(var stairs in map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<Building_Stairs>()
            .Where(stairs=>owner.TryDecode(stairs.Position,out var cell)&&cell.LayerId==1).ToList())
            stairs.Destroy(DestroyMode.Vanish);
        Check("shot-does-not-require-walking-route",!pawn.CanReach(target,PathEndMode.Touch,Danger.Deadly) &&
            owner.Combat.Solve(pawn,pawn.Position,target,20).GeometricallyPossible);
        LayerView.Switch(owner,2);
        stage=11;ticks=0;
    }

    private static void CheckDismantle()
    {
        IntVec3 cell=At(2,65,58);
        Thing item=ThingMaker.MakeThing(ThingDefOf.Silver);
        GenSpawn.Spawn(item,cell,map);
        Check("occupied-slab-cannot-be-dismantled",!map.terrainGrid.CanRemoveTopLayerAt(cell));
        map.terrainGrid.RemoveTopLayer(cell);
        Check("occupied-slab-removal-preserves-state",owner.Structure.HasSurface(cell) && item.Spawned);
        item.Destroy(DestroyMode.Vanish);
        Check("empty-top-slab-can-be-dismantled",map.terrainGrid.CanRemoveTopLayerAt(cell));
        int before=map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount);
        map.terrainGrid.RemoveTopLayer(cell);
        Check("slab-removal-exposes-air-not-fake-ground",!owner.Structure.HasSurface(cell) &&
            map.terrainGrid.TerrainAt(cell)==RimLayersDefOf.RL_OpenAir);
        int expected=UnityEngine.Mathf.RoundToInt(4*RimLayersDefOf.RL_StructuralFloor.resourcesFractionWhenDeconstructed);
        Check("dismantle-refund-stays-on-valid-floor",map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount)==before+expected &&
            map.listerThings.ThingsOfDef(ThingDefOf.Steel).All(t=>owner.Structure.HasSurface(t.Position)));
        IntVec3 decorated=At(2,64,58);
        map.terrainGrid.SetTerrain(decorated,DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
        map.terrainGrid.RemoveTopLayer(decorated,false);
        Check("decoration-removal-preserves-structural-base",owner.Structure.HasSurface(decorated) &&
            map.terrainGrid.TerrainAt(decorated)==RimLayersDefOf.RL_StructuralFloor);
    }

    private static string DescribePawn() =>
        $"pawn={pawn.Position}, job={pawn.CurJob?.def?.defName}, next={pawn.pather?.nextCell}, path={pawn.pather?.curPath?.NodesLeftCount}, moving={pawn.pather?.Moving}, dead={pawn.Dead}, spawned={pawn.Spawned}";

    private static void OnGUI()
    {
        JumpsOnGUI();TransitionsOnGUI();
        ReinstallOnGUI();CommandInputOnGUI();
        UsabilityKeysOnGUI();
        if (!loadPending || Event.current.type != EventType.Repaint) return;
        loadPending = false; loadIssued = true;
        string? saveName=scenario switch
        {
            "environment"=>GenCommandLine.CommandLineArgPassed("rimlayersenvironmentlegacy")?"RimLayers_EnvironmentLegacy":"RimLayers_Environment",
            "vehicle-flight"=>"RimLayers_VehicleFlight",
            "vehicle-ramp"=>"RimLayers_VehicleRamp",
            "vehicle-lift"=>"RimLayers_VehicleLift",
            "vehicle-ground"=>"RimLayers_VehicleGround",
            "jump"=>"RimLayers_V1Jump", "transition"=>"RimLayers_Transitions",
            "settings-behavior"=>"RimLayers_SettingsBehavior",
            "settings-recovery"=>"RimLayers_SettingsRecovery",
            "ship-vge"=>"RimLayers_V1Vge",
            "ship-coexistence"=>"RimLayers_V1Coexistence",
            "shaft-extension"=>GenCommandLine.CommandLineArgPassed("rimlayersshaftcompletedreload")?"RimLayers_V1ShaftCompleted":"RimLayers_V1ShaftExpansion",
            "underground"=>"RimLayers_V1Underground",
            "mountains"=>"RimLayers_V1Mountains",
            "stairs-bootstrap"=>"RimLayers_V1StairBootstrap",
            "expansion"=>"RimLayers_V1Expansion",
            "elevators"=>"RimLayers_V1Elevators",
            "gravship"=>GenCommandLine.CommandLineArgPassed("rimlayersshiplandedreload")?"RimLayers_V1GravshipLanded":"RimLayers_V1Gravship",
            "deep-resources"=>"RimLayers_V1DeepResources",
            "reachability"=>"RimLayers_V1Reachability",
            "presentation"=>"RimLayers_V1Presentation",
            "usability"=>"RimLayers_V1Usability",
            "migration"=>GenCommandLine.CommandLineArgPassed("rimlayersmigrationreload")?"RimLayers_V1MigrationConverted":"RimLayers_V1Migration",
            "world-exit"=>"RimLayers_V1Exit",
            "home"=>"RimLayers_V1Home",
            "scale"=>"RimLayers_V1Scale",
            "lord"=>"RimLayers_V1Lord",
            "temperature"=>"RimLayers_Temperature",
            "roping"=>"RimLayers_V1Roping",
            "mechanitor"=>"RimLayers_V1Mechanitor",
            "combat-shield-save" or "combat-shield-pass"=>"RimLayers_V1ShieldBoundary",
            "combat-grenade-flee"=>"RimLayers_V1GrenadeFlee",
            "skyfall"=>"RimLayers_V1Skyfall",
            "cargo"=>"RimLayers_V1Cargo",
            "work-container"=>"RimLayers_V1Container",
            "work-save"=>"RimLayers_V1Work",
            "navigation-maze"=>"RimLayers_V1Maze",
            "collapse-perf"=>"RimLayers_CollapsePerf",
            "structure"=>"RimLayers_V1Structure",
            "combat-shot"=>"RimLayers_V1Combat",
            "combat-grenade"=>"RimLayers_V1Grenade",
            "combat-raid-audit"=>"RimLayers_V1RaidEnd",
            "combat-manned"=>GenCommandLine.CommandLineArgPassed("rimlayersmortarflightreload")?"RimLayers_V1MannedFlight":"RimLayers_V1Manned",
            "combat-explosion"=>"RimLayers_V1Explosion",
            "combat-delay"=>"RimLayers_V1DelayedBlast",
            "utilities"=>"RimLayers_V1Utilities",
            "air"=>"RimLayers_V1Air",
            "heat"=>"RimLayers_V1Heat",
            "sky"=>"RimLayers_V1Sky",
            "hazards" or "hazards-timed"=>"RimLayers_V1Hazards",
            "navigation"=>"RimLayers_V1Navigation",
            "aerocraft"=>"RimLayers_V1Aerocraft",
            _=>null
        };
        if(saveName==null||!File.Exists(Path.Combine(directory,"Saves",saveName+".rws")))
        {
            Check("requested-native-save-checkpoint-exists",false,$"scenario={scenario},save={saveName}");
            Finish();return;
        }
        GameDataSaveLoader.LoadGame(saveName);
    }

    private static void Finish()
    {
        if (finished) return;
        if(scenario=="mechanitor")Check("remote-command-radius-submits-boundary-in-native-overlay-stage",renderedMechRangeFloors.Contains(1)&&mechRingCells>0&&mechRingEdges>0&&mechCenterAllowed,$"floors={string.Join(",",renderedMechRangeFloors)},cells={mechRingCells},edges={mechRingEdges},centerAllowed={mechCenterAllowed},remoteCalls={RemoteMechanitorRadius.DrawCalls}/{RemoteMechanitorRadius.VisibleCalls}; {string.Join(" | ",mechBoundaryFacts.Select(p=>$"L{p.Key}:{p.Value}"))}");
        Check("no-runtime-errors", errors.Count == 0, string.Join("\n", errors.Take(8)));
        finished = true;
        File.WriteAllText(Path.Combine(directory, (GenCommandLine.CommandLineArgPassed("rimlayersenvironmentlegacy")?"legacy-load":GenCommandLine.CommandLineArgPassed("rimlayersshaftcompletedreload")?"completed-load":GenCommandLine.CommandLineArgPassed("rimlayersmortarflightreload")?"flight-load":GenCommandLine.CommandLineArgPassed("rimlayersshiplandedreload")?"landed-load":GenCommandLine.CommandLineArgPassed("rimlayersmigrationreload")?"converted-load":mode) + "-" + scenario + "-results.json"), JsonSerializer.Serialize(new {
            mode, failures, errors, checks,
            activeMods=LoadedModManager.RunningModsListForReading.Select(m=>m.PackageId).ToArray(),
            assemblyMvid=typeof(Pawn).Assembly.ManifestModule.ModuleVersionId.ToString("N")
        }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Quit();
    }
}
