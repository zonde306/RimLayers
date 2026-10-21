using System.Collections;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimLayers.Ships;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly CellRect shipBaseRect=new(74,70,17,13);
    private static Building_GravEngine shipEngine=null!;
    private static CompPilotConsole shipConsole=null!;
    private static CompRefuelable shipTank=null!;
    private static Gravship layerShip=null!;
    private static Map shipSource=null!,shipDestination=null!;
    private static int shipPhase;
    private static Pawn shipPawn=null!;
    private static Thing shipOutside=null!;
    private static Thing shipItem=null!;
    private static string shipContainedId="",shipStandId="";
    private static Building_WorkTable shipTable=null!;
    private static List<Thing> shipExpected=new();
    private static Dictionary<string,IntVec3> shipExpectedPositions=new();
    private static IntVec3 shipLandingRoot;
    private static PlanetTile shipTargetTile;
    private static bool ExistingShipDestination=>GenCommandLine.CommandLineArgPassed("rimlayersexistingmap");
    private static int ShipEngineLayer=>GenCommandLine.TryGetCommandLineArg("rimlayersenginelayer",out var value)&&int.TryParse(value,out var layer)?layer:0;
    private static int ShipDecks=>ScaleArgument("rimlayersshipdecks",3,3,6);
    private static bool NativeShip => GenCommandLine.CommandLineArgPassed("rimlayersshipnative");


    private static void ConfigureGravship(Harmony h)
    {
        if(ShipVisual)ConfigureShipVisual(h);
        h.Patch(AccessTools.Method(typeof(GravshipPlacementUtility),nameof(GravshipPlacementUtility.ApplyTemperatureVacuumFromBase)),
            postfix:new HarmonyMethod(typeof(V1Integration),nameof(ObserveShipClimate)){priority=Priority.Last});
        h.Patch(AccessTools.Method(typeof(GravshipUtility),nameof(GravshipUtility.GenerateGravship)),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(ObserveShipManifest)){priority=Priority.First});
    }
    private static void ObserveShipClimate(Gravship gravship)
    {if(gravship.Engine==shipEngine&&shipPhase==2)ValidateShipCabin();}
    private static void ObserveShipManifest(Building_GravEngine engine)
    {
        if(engine!=shipEngine)return;
        if(engine.Map==shipSource)
        {
        var cabin=At(1,86,73).GetRoom(engine.Map);
        File.WriteAllText(Path.Combine(directory,"ship-cabin-baseline.json"),JsonSerializer.Serialize(new{temperature=cabin.Temperature,vacuum=cabin.Vacuum,detailTop=engine.Map.terrainGrid.TopTerrainAt(At(1,83,78))?.defName,detailUnder=engine.Map.terrainGrid.UnderTerrainAt(At(1,83,78))?.defName,detailFoundation=engine.Map.terrainGrid.FoundationAt(At(1,83,78))?.defName,detailTemp=engine.Map.terrainGrid.TempTerrainAt(At(1,83,78))?.defName}));
        Check("source-upper-cabin-is-sealed-before-native-capture",!cabin.UsesOutdoorTemperature&&!cabin.ExposedToSpace);
        }
        var source=engine.Map;var layout=LayerMapComponent.For(source)!;layout.TryDecode(engine.Position,out var origin);
        shipExpected=source.listerThings.AllThings.Where(t=>t is not Mote && t.def.bringAlongOnGravship && t is not Skyfaller &&
            (engine.OnValidSubstructure(t)||t==engine)).Distinct().ToList();
        shipExpectedPositions=shipExpected.ToDictionary(t=>t.ThingID,t=>{layout.TryDecode(t.Position,out var c);return new IntVec3(c.X-origin.X,c.LayerId,c.Z-origin.Z);});
    }

    private static void BeginGravship()
    {
        Check("gravship-profile-has-odyssey",ModsConfig.OdysseyActive);
        shipSource=map;shipPawn=pawn;
        if(SpaceFixture)WearVacuumGear(shipPawn);
        foreach(var c in shipBaseRect.ExpandedBy(8))
        {
            foreach(var thing in c.GetThingList(map).ToList())if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>shipBaseRect.ExpandedBy(8).Contains(c));
        foreach(var c in shipBaseRect)
        {
            if(map.terrainGrid.UnderTerrainAt(c)!=null)map.terrainGrid.RemoveTopLayer(c,false);
            map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
        }
        shipEngine=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(80,0,75),map);
        shipEngine.SetFaction(Faction.OfPlayer);shipEngine.nameHidden=false;
        var console=(ThingWithComps)GenSpawn.Spawn(ThingDefOf.PilotConsole,new IntVec3(85,0,75),map);
        console.SetFaction(Faction.OfPlayer);shipConsole=console.GetComp<CompPilotConsole>();
        var tank=(ThingWithComps)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("ChemfuelTank"),new IntVec3(85,0,79),map);
        tank.SetFaction(Faction.OfPlayer);shipTank=tank.GetComp<CompRefuelable>();shipTank.Refuel(250);
        var thruster=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("SmallThruster")),new IntVec3(76,0,70),map,Rot4.North);
        thruster.SetFaction(Faction.OfPlayer);
        // Independent roof holders on every deck, away from core/console/tank.
        for(int layer=0;layer<3;layer++)
        {
            if(layer>0)foreach(var c in shipBaseRect)ShipDeckTerrain.Install(owner,At(layer,c.x,c.z),false);
            foreach(var c in new[]{new IntVec3(75,0,75),new IntVec3(75,0,81),new IntVec3(81,0,81),new IntVec3(89,0,81),new IntVec3(89,0,71),new IntVec3(81,0,71),new IntVec3(87,0,75),new IntVec3(87,0,81)})
            {var col=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,c.x,c.z),map);col.SetFaction(Faction.OfPlayer);}
            foreach(var c in shipBaseRect)map.roofGrid.SetRoof(At(layer,c.x,c.z),RoofDefOf.RoofConstructed);
        }
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(1,74,76),map).SetFaction(Faction.OfPlayer);
        var wallLamp=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("WallLamp")),At(1,73,76),map,Rot4.East);
        wallLamp.SetFaction(Faction.OfPlayer);
        Check("upper-wall-attachment-protrudes-outside-captured-floor",GenConstruct.GetWallAttachedTo(wallLamp)!=null&&!shipBaseRect.Contains(new IntVec3(73,0,76)));
        GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(0,78,77),map).SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(1,82,77),map).SetFaction(Faction.OfPlayer);
        map.terrainGrid.SetTerrain(At(1,83,78),DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
        map.terrainGrid.SetTempTerrain(At(1,83,78),DefDatabase<TerrainDef>.GetNamed("ShallowFloodwater"));
        shipTable=(Building_WorkTable)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TableButcher"),ThingDefOf.WoodLog),At(1,81,72),map);
        shipTable.SetFaction(Faction.OfPlayer);
        shipTable.BillStack.AddBill(new Bill_Production(DefDatabase<RecipeDef>.GetNamed("ButcherCorpseFlesh"),null){repeatCount=7});
        shipItem=ThingMaker.MakeThing(ThingDefOf.Silver);shipItem.stackCount=37;GenSpawn.Spawn(shipItem,At(2,86,78),map);
        AddShipCargoFixture();
        shipPawn.Position=At(2,83,77);shipPawn.Notify_Teleported();shipPawn.drafter.Drafted=true;
        var inventory=ThingMaker.MakeThing(ThingDefOf.Steel);inventory.stackCount=5;shipPawn.inventory.innerContainer.TryAdd(inventory);
        var clothes=(Building_OutfitStand)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Building_OutfitStand"),ThingDefOf.WoodLog),At(1,87,78),map);
        clothes.SetFaction(Faction.OfPlayer);
        var apparel=(Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_Duster"),ThingDefOf.Cloth);
        clothes.AddApparel(apparel);shipContainedId=apparel.ThingID;shipStandId=clothes.ThingID;
        // A real structure above ordinary ground but outside native connected substructure stays behind.
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),new IntVec3(94,0,75),map);
        foreach(var c in new CellRect(91,73,5,5))map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(1,91,75),RimLayersDefOf.RL_StructuralFloor);
        shipOutside=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel),At(1,91,75),map);shipOutside.stackCount=13;
        foreach(var cell in shipBaseRect)map.areaManager.Home[At(1,cell.x,cell.z)]=true;
        map.areaManager.TryMakeNewAllowed(out var area);area.SetLabel("Ship crew");area[shipPawn.Position]=true;shipPawn.playerSettings.AreaRestrictionInPawnCurrentMap=area;
        var store=new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager){label="Upper ship stores"};map.zoneManager.RegisterZone(store);
        store.AddCell(At(2,86,78));store.AddCell(At(2,87,78));store.settings.Priority=StoragePriority.Critical;
        foreach(var c in new CellRect(84,71,5,5).EdgeCells)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(1,c.x,c.z),map).SetFaction(Faction.OfPlayer);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var cabin=At(1,86,73).GetRoom(map);cabin.Temperature=23.75f;cabin.Vacuum=.12f;
        shipEngine.ForceSubstructureDirty();
        if(NativeShip)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("GravAnchor"),new IntVec3(65,0,80),map).SetFaction(Faction.OfPlayer);
        for(int l=1;l<3;l++)
        {
            var torch=(ThingWithComps)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TorchLamp")),At(l,84,80),map);
            torch.SetFaction(Faction.OfPlayer);torch.GetComp<CompRefuelable>().Refuel(30);
        }
        if(ShipEngineLayer>0){shipEngine.DeSpawn();GenSpawn.Spawn(shipEngine,At(ShipEngineLayer,80,75),map);}
        if(ShipEngineLayer==0)
        {
            console.DeSpawn();GenSpawn.Spawn(console,At(2,85,75),map);
            tank.DeSpawn();GenSpawn.Spawn(tank,At(1,85,79),map);
            thruster.DeSpawn();GenSpawn.Spawn(thruster,At(1,76,70),map,Rot4.North);
        }
        GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("LargeThruster"),At(2,87,70),map,Rot4.North).SetFaction(Faction.OfPlayer);
        var largeTank=(ThingWithComps)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("LargeChemfuelTank"),At(2,77,80),map);largeTank.SetFaction(Faction.OfPlayer);largeTank.GetComp<CompRefuelable>().Refuel(100);
        Find.ResearchManager.gravEngineInspected=true;
        foreach(var pos in new[]{At(0,78,80),At(2,88,80)})GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("GravFieldExtender"),pos,map).SetFaction(Faction.OfPlayer);
        owner.Ships.MarkDirty();owner.Ships.Tick();
        shipPhase=0;stage=330;ticks=0;
    }

    private static void GravshipStep()
    {
        if(shipPhase==0)
        {
            if(ticks<90)return;
            var launch=shipEngine.CanLaunch(shipConsole);
            Check("native-legal-core-console-fuel-thruster-can-launch",launch.Accepted,$"reason={launch.Reason},fuel={shipEngine.TotalFuel},range={shipEngine.MaxLaunchDistance},cells={shipEngine.ValidSubstructure.Count},console={shipConsole.CanBeActive}");
            Check("native-footprint-remains-core-deck-with-logical-cross-layer-ownership",shipEngine.ValidSubstructure.Count<=shipBaseRect.Area&&shipEngine.ValidSubstructure.All(c=>owner.TryDecode(c,out var l)&&l.LayerId==ShipEngineLayer)&&owner.Ships.Get(shipEngine)?.Cells.Count==shipBaseRect.Area*3);
            Check("cross-layer-extenders-share-native-capacity",shipEngine.GetStatValue(StatDefOf.SubstructureSupport)>=1000&&shipEngine.GravshipComponents.Count(c=>c.parent.def.defName=="GravFieldExtender")==2);
            Check("native-substructure-cannot-bypass-upper-ship-floor-designator",!owner.Structure.Validate(TerrainDefOf.Substructure,At(1,83,78),Rot4.North,planning:true).Accepted);
            Check("explicit-ship-decks-retain-foundation-and-finish",map.terrainGrid.FoundationAt(At(1,83,78))?.defName=="RL_ShipStructuralFloor"&&map.terrainGrid.TopTerrainAt(At(1,83,78)).defName=="WoodPlankFloor");
            Check("upper-large-thruster-and-fuel-tank-are-real-native-components",shipEngine.GravshipComponents.Any(c=>c.parent.def.defName=="LargeThruster")&&shipEngine.GravshipComponents.Any(c=>c.parent.def.defName=="LargeChemfuelTank")&&shipEngine.MaxFuel>=1000);
            foreach(var component in shipEngine.GravshipComponents)
            {
                var thing=component.parent;
                var placement=GenConstruct.CanPlaceBlueprintAt(thing.def,thing.Position,thing.Rotation,map,thingToIgnore:thing,ignoreInteractionSpots:true);
                Check("native-placement-on-ship-foundation-"+thing.def.defName,placement.Accepted,placement.Reason);
            }
            var roomForCore=shipBaseRect.Cells.First(c=>GenAdj.OccupiedRect(c,Rot4.North,ThingDefOf.GravEngine.size).All(p=>shipBaseRect.Contains(p)&&!p.GetThingList(map).Any(t=>t is Building or Pawn)));
            var second=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,roomForCore,map);second.SetFaction(Faction.OfPlayer);owner.Ships.MarkDirty();
            var ambiguousCapture=ShipLayers.Prepare(shipEngine,out var ambiguous);
            Check("two-cores-in-connected-ship-reject-ambiguous-capture",ambiguousCapture==null);
            Check("ambiguous-owner-is-rejected-before-capture",!ambiguous.Accepted&&shipPawn.Spawned);second.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
            var extenders=shipEngine.GravshipComponents.Where(c=>c.parent.def.defName=="GravFieldExtender").Select(c=>c.parent).ToArray();
            foreach(var e in extenders)e.DeSpawn();owner.Ships.MarkDirty();owner.Ships.Tick();
            Check("total-decks-over-capacity-block-launch-without-destruction",!shipEngine.CanLaunch(shipConsole).Accepted&&shipPawn.Spawned&&shipItem.Spawned);
            foreach(var e in extenders){var at=e.Position;GenSpawn.Spawn(e,at,map);}owner.Ships.MarkDirty();owner.Ships.Tick();
            Check("upper-pawn-native-onboard-cell-is-recognized",shipEngine.ValidSubstructureAt(shipPawn.Position)&&GravshipUtility.IsOnboardGravship_NewTemp(shipPawn.Position,shipEngine,shipPawn,true));
            Check("unconnected-upper-structure-is-not-onboard",!shipEngine.ValidSubstructureAt(shipOutside.Position));
            var fake=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(105,0,105),map);fake.SetFaction(Faction.OfPlayer);
            Check("core-without-valid-substructure-does-not-enable-layer-capture",!ShipLayers.Eligible(fake));fake.Destroy(DestroyMode.Vanish);
            var tankFuel=shipEngine.GravshipComponents.Where(c=>c.Props.providesFuel).Select(c=>c.parent.GetComp<CompRefuelable>()).ToDictionary(c=>c,c=>c.Fuel);
            foreach(var pair in tankFuel)pair.Key.ConsumeFuel(pair.Value);
            Check("no-fuel-still-blocked-by-native-launch",!shipEngine.CanLaunch(shipConsole).Accepted);
            foreach(var pair in tankFuel)pair.Key.Refuel(pair.Value);
            CheckShipFloorConstruction();
            shipEngine.cooldownCompleteTick=Find.TickManager.TicksGame+300;Check("native-cooldown-is-not-bypassed",!shipEngine.CanLaunch(shipConsole).Accepted);shipEngine.cooldownCompleteTick=0;
            var split=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TableButcher"),ThingDefOf.WoodLog),At(1,90,75),map);
            Check("partial-upper-building-is-classified-without-preview-destruction",ShipLayers.Prepare(shipEngine,out var splitReport) is {} splitCapture&&splitReport.Accepted&&splitCapture.Straddlers.Contains(split)&&split.Spawned&&shipPawn.Spawned&&shipItem.Spawned);split.Destroy(DestroyMode.Vanish);
            CheckIndependentShipLinks();
            CheckCargoShipBoundary();
            if(scenario=="ship-structure"){CheckShipBoarding();CheckSingleElevatedDeck();Finish();return;}
            if(ShipDecks>3)ExtendShipTestDecks();
            shipExpected=map.listerThings.AllThings.Where(t=>t is not Mote && t.def.bringAlongOnGravship && t is not Skyfaller &&
                (shipEngine.OnValidSubstructure(t)||t==shipEngine)).Distinct().ToList();
            shipExpectedPositions=shipExpected.ToDictionary(t=>t.ThingID,t=>{owner.TryDecode(t.Position,out var c);return new IntVec3(c.X-80,c.LayerId,c.Z-75);});
            shipEngine.launchInfo=new LaunchInfo{pilot=shipPawn,quality=1,doNegativeOutcome=false};
            shipTargetTile=Find.WorldGrid.Tiles.First(t=>t.tile!=map.Tile&&t.PrimaryBiome==BiomeDefOf.TemperateForest&&TileFinder.IsValidTileForNewSettlement(t.tile)).tile;
            if(ExistingShipDestination)PrepareExistingShipDestination();
            else if(SpaceFixture)shipTargetTile=Find.WorldGrid.Orbit.Tiles.First(t=>Find.WorldObjects.MapParentAt(t.tile)==null).tile;
            if(NativeShip)
            {
                if(!launch.Accepted)throw new InvalidOperationException("Fixture must obey native launch eligibility: "+launch.Reason);
                if(ShipVisual)shipVisualLaunch=()=>{if(ExistingShipDestination)BeginConfirmedShipTakeoff();else Find.GravshipController.InitiateTakeoff(shipEngine,shipTargetTile);};
                else if(ExistingShipDestination)BeginConfirmedShipTakeoff();
                else Find.GravshipController.InitiateTakeoff(shipEngine,shipTargetTile);
                shipPhase=3;ticks=0;return;
            }
            layerShip=GravshipUtility.GenerateGravship(shipEngine);
            GravshipUtility.TravelTo(layerShip,map.Tile,shipTargetTile);
            CompleteShipCapture();return;
        }
        if(shipPhase==8){LandExistingShip();return;}
        if(shipPhase==3)
        {
            var travelling=Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault(s=>s.Engine==shipEngine);
            if(travelling==null)return;
            layerShip=travelling;
            Check("native-takeoff-cutscene-finishes-before-flight-save",!WorldComponent_GravshipController.CutsceneInProgress&&shipEngine.Map==null);
            CompleteShipCapture();return;
        }
        if(shipPhase==2)
        {
            if(!shipEngine.Spawned||WorldComponent_GravshipController.CutsceneInProgress)return;
            Check("native-landing-cutscene-completes-and-releases-flight-owner",Current.Game.Gravship==null);
            ValidateShipLanding();return;
        }
        if(shipPhase==5)
        {
            if(ticks<90)return;
            var report=shipEngine.CanLaunch(shipConsole);
            Check("landed-ship-can-make-second-native-flight",report.Accepted,report.Reason);
            if(!report.Accepted)throw new InvalidOperationException("Second fixture flight must be legal.");
            shipReturnView=LayerMapComponent.For(shipSource)!.ViewedLayer;
            if(ShipVisual){shipVisualLaunchFrames=0;shipVisualLaunch=()=>Find.GravshipController.InitiateTakeoff(shipEngine,shipSource.Tile); }
            else Find.GravshipController.InitiateTakeoff(shipEngine,shipSource.Tile);
            shipPhase=6;return;
        }
        if(shipPhase==6)
        {
            var marker=(GravshipLandingMarker?)AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").GetValue(Find.GravshipController);
            if(marker==null||Find.GravshipController.landingMap!=shipSource||WorldComponent_GravshipController.CutsceneInProgress)return;
            layerShip=marker.gravship;map=shipSource;owner=LayerMapComponent.For(map)!;
            Check("native-existing-map-arrival-retains-player-selected-floor",owner.ViewedLayer==shipReturnView&&Find.CurrentMap==map,$"expectedView={shipReturnView}; actualView={owner.ViewedLayer}; current={Find.CurrentMap?.uniqueID}; destination={map.uniqueID}");
            foreach(var c in new CellRect(75,25,35,35))
            {
                foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn and not GravshipLandingMarker){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
                map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
            }
            map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(75,25,35,35).Contains(c));
            shipLandingRoot=new IntVec3(90,0,40);marker.GravshipRotation=Rot4.South;
            var designator=(Designator_MoveGravship)Find.DesignatorManager.SelectedDesignator;
            var atZero=(IntVec3)AccessTools.Method(typeof(Designator_MoveGravship),"GetSizeRotAdjustedCell").Invoke(designator,new object[]{IntVec3.Zero})!;
            var bias=PrefabUtility.GetRoot(atZero,layerShip.Bounds.Size,marker.GravshipRotation);
            var designateCell=shipLandingRoot-bias;
            var returnReport=designator.CanDesignateCell(designateCell);
            Check("native-designator-accepts-return-landing",returnReport.Accepted,returnReport.Reason);
            if(!returnReport.Accepted)throw new InvalidOperationException("Return landing fixture rejected: "+returnReport.Reason);
            designator.DesignateSingleCell(designateCell);
            Check("second-landing-validates-existing-map-with-different-stride",ShipLanding.Check(layerShip,shipLandingRoot,map).Accepted&&marker.Position==shipLandingRoot&&marker.GravshipRotation==Rot4.South);
            marker.BeginLanding(Find.GravshipController);
            AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").SetValue(Find.GravshipController,null);
            shipPhase=7;return;
        }
        if(shipPhase==7)
        {
            if(!shipEngine.Spawned||WorldComponent_GravshipController.CutsceneInProgress)return;
            owner=LayerMapComponent.For(shipEngine.Map)!;
            Check("second-native-landing-brings-all-decks-back-to-existing-main-map",Find.CurrentMap==shipSource&&shipEngine.Map==shipSource&&
                shipPawn.Map==shipSource&&owner.TryDecode(shipPawn.Position,out var at)&&at.LayerId==2&&at.X==88&&at.Z==43&&owner.Structure.HasSurface(shipPawn.Position),$"current={Find.CurrentMap?.uniqueID}, coreMap={shipEngine.Map?.uniqueID}, pawn={shipPawn.Position}, expected={shipSource.uniqueID}:(88,2,43)");
            Check("second-native-flight-releases-world-ownership",Current.Game.Gravship==null&&!Find.WorldObjects.AllWorldObjects.OfType<Gravship>().Any());
            CameraJumper.TryJump(shipPawn,CameraJumper.MovementMode.Cut);LayerView.Switch(owner,2);
            pendingScreenshot=Path.Combine(directory,"returned-layer-ship.png");screenshotHold=3;stage=11;ticks=0;return;
        }
        if(shipPhase==1)
        {
            if(ticks<180)return;
            Check("landed-upper-pawn-can-route-through-native-stairs",shipPawn.CanReach(At(0,shipLandingRoot.x,shipLandingRoot.z),PathEndMode.OnCell,Danger.Deadly));
            pendingScreenshot=Path.Combine(directory,"landed-layer-ship.png");screenshotHold=3;
            Current.Game.Gravship=null;
            GameDataSaveLoader.SaveGame("RimLayers_V1GravshipLanded");
            stage=11;ticks=0;return;
        }
    }
    private static void CompleteShipCapture()
    {
        map=shipSource;owner=LayerMapComponent.For(map)!;
        VerifyShipCargo(layerShip.Things,"departed");
            Check("native-gravship-has-persistent-layer-facts",ShipLayers.TryData(layerShip,out var payload)&&payload.floors.Count>0,$"floors={payload?.floors.Count}");
            Check("native-world-container-owns-original-upper-pawn",layerShip.Pawns.Contains(shipPawn)&&!shipPawn.Spawned&&shipExpectedPositions.ContainsKey(shipPawn.ThingID));
            Check("native-world-container-owns-upper-items-and-workbench",layerShip.Things.Contains(shipItem)&&layerShip.Things.Contains(shipTable)&&!shipItem.Spawned&&!shipTable.Spawned);
            Check("native-public-footprint-excludes-storage-bands",layerShip.Terrains.Keys.All(c=>c.y==0)&&layerShip.Bounds.Width<=20&&layerShip.Bounds.Height<=20,$"bounds={layerShip.Bounds}");
            Check("source-captured-upper-floor-is-removed-without-cargo-damage",shipExpected.All(t=>!t.Destroyed)&&!owner.StructuralCells.Contains(At(2,83,77))&&!owner.PendingCollapse.Contains(At(2,83,77)));
            Check("source-nonship-structure-and-item-stay-on-original-map",shipOutside.Spawned&&shipOutside.Map==map&&owner.Structure.HasSurface(shipOutside.Position));
            var all=layerShip.Things.Concat(layerShip.Pawns.Cast<Thing>()).ToList();
            var records=all.Select(t=>new{t.ThingID,position=new{x=shipExpectedPositions[t.ThingID].x,y=shipExpectedPositions[t.ThingID].y,z=shipExpectedPositions[t.ThingID].z},t.stackCount,rotation=t.Rotation.AsInt}).ToArray();
            File.WriteAllText(Path.Combine(directory,"gravship-baseline.json"),JsonSerializer.Serialize(new{
                source=map.uniqueID,engine=shipEngine.ThingID,pawn=shipPawn.ThingID,item=shipItem.ThingID,table=shipTable.ThingID,outside=shipOutside.ThingID,
                stand=shipStandId,contained=shipContainedId,inventory=inventoryIds(shipPawn),actors=records,floorCount=payload!.floors.Count,oldStride=owner.Layout!.Stride,
                destination=shipTargetTile.ToString(),sourceThingIds=map.listerThings.AllThings.Where(t=>t is not Mote).Select(t=>t.ThingID).ToArray()
            },new JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
            if(NativeShip)Check("departed-ship-keeps-persistable-reservation",LayerExpansionQuota.Current!.Reservations.Any(r=>r.EngineId==shipEngine.thingIDNumber&&r.Destination==layerShip.destinationTile&&r.Top==ShipDecks-1));
            Check("recovery-does-not-own-normal-ship-payload",!RimLayers.Recovery.EntityRecoveryService.Current!.Records.Any(r=>r.Thing!=null&&all.Contains(r.Thing)));
            GameDataSaveLoader.SaveGame("RimLayers_V1Gravship");
            Check("in-flight-native-ship-is-saved-with-no-duplicate-map-pawn",Find.WorldObjects.AllWorldObjects.Contains(layerShip)&&!map.mapPawns.AllPawnsSpawned.Contains(shipPawn));
        Finish();
    }

    private static void ExtendShipTestDecks()
    {
        RimLayersMod.Settings.UpperLimit=ShipDecks-1;
        RimLayers.Generation.LiveMapExpansion.EnsureElevation(map,ShipDecks-1);
        for(int layer=3;layer<ShipDecks;layer++)
        {
            foreach(var c in shipBaseRect)ShipDeckTerrain.Install(owner,At(layer,c.x,c.z),false);
            foreach(var c in new[]{new IntVec3(75,0,75),new IntVec3(75,0,81),new IntVec3(81,0,81),new IntVec3(89,0,81),new IntVec3(89,0,71),new IntVec3(81,0,71),new IntVec3(87,0,75),new IntVec3(87,0,81)})
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,c.x,c.z),map).SetFaction(Faction.OfPlayer);
            foreach(var c in shipBaseRect)map.roofGrid.SetRoof(At(layer,c.x,c.z),RoofDefOf.RoofConstructed);
        }
        foreach(int layer in new[]{ShipDecks-2,ShipDecks-1})GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("GravFieldExtender"),At(layer,85,80),map).SetFaction(Faction.OfPlayer);
        var cargo=ThingMaker.MakeThing(ThingDefOf.Gold);cargo.stackCount=23;GenSpawn.Spawn(cargo,At(ShipDecks-1,85,77),map);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();owner.Ships.MarkDirty();owner.Ships.Tick();shipEngine.ForceSubstructureDirty();
        Check("extended-native-ship-contains-all-requested-decks",owner.Ships.Get(shipEngine)?.Top==ShipDecks-1&&owner.Ships.Get(shipEngine)?.Cells.Count==shipBaseRect.Area*ShipDecks);
        var report=shipEngine.CanLaunch(shipConsole);
        Check("extended-ship-obeys-native-mass-capacity-and-launch-rules",report.Accepted,report.Reason);
        if(!report.Accepted)throw new InvalidOperationException("Extended fixture must obey native launch requirements: "+report.Reason);
    }
    private static void CheckIndependentShipLinks()
    {
        foreach(var c in new CellRect(23,83,5,6))
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.roofGrid.SetRoof(c,null);if(map.terrainGrid.UnderTerrainAt(c)!=null)map.terrainGrid.RemoveTopLayer(c,false);
            map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
        }
        var other=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(25,0,85),map);other.SetFaction(Faction.OfPlayer);
        var console=(ThingWithComps)GenSpawn.Spawn(ThingDefOf.PilotConsole,new IntVec3(25,0,87),map);console.SetFaction(Faction.OfPlayer);
        owner.Ships.MarkDirty();owner.Ships.Tick();
        Check("two-independent-ships-keep-distinct-core-ownership",owner.Ships.OwnerAt(other.Position)==other&&owner.Ships.OwnerAt(shipConsole.parent.Position)==shipEngine);
        Check("native-facility-links-do-not-cross-to-another-nearby-ship",console.GetComp<CompPilotConsole>().engine==other&&shipConsole.engine==shipEngine&&!other.AffectedByFacilities.CanLinkTo(shipConsole.parent)&&!shipEngine.AffectedByFacilities.CanLinkTo(console));
        other.SetFaction(Faction.OfAncientsHostile);
        Check("claimed-core-ownership-change-invalidates-derived-structure",owner.Ships.OwnerAt(other.Position)==null);
        other.SetFaction(Faction.OfPlayer);
        Check("reclaimed-core-is-recognized-without-terrain-edits",owner.Ships.OwnerAt(other.Position)==other);
        console.Destroy(DestroyMode.Vanish);other.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
    }

    private static void CheckSingleElevatedDeck()
    {
        var rect=new CellRect(28,90,5,6);
        foreach(var c in rect)
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);map.roofGrid.SetRoof(At(1,c.x,c.z),RoofDefOf.RoofConstructed);
            ShipDeckTerrain.Install(owner,At(2,c.x,c.z),false);
        }
        for(int layer=0;layer<3;layer++)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,28,90),map).SetFaction(Faction.OfPlayer);
        var core=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,At(2,30,92),map);core.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(ThingDefOf.PilotConsole,At(2,30,94),map).SetFaction(Faction.OfPlayer);owner.Ships.MarkDirty();owner.Ships.Tick();
        core.launchInfo=new LaunchInfo{pilot=shipPawn,quality=1,doNegativeOutcome=false};
        var wood=DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");var flood=DefDatabase<TerrainDef>.GetNamed("ShallowFloodwater");
        var detail=At(2,32,94);map.terrainGrid.SetTerrain(detail,wood);map.terrainGrid.SetTempTerrain(detail,flood);
        var cargo=ThingMaker.MakeThing(ThingDefOf.Silver);cargo.stackCount=19;GenSpawn.Spawn(cargo,detail,map);
        owner.Ships.MarkDirty();var structure=owner.Ships.Get(core);
        Check("elevated-single-deck-does-not-absorb-ordinary-supporting-colony",structure is {Bottom:2,Top:2}&&structure.Cells.Count==30&&owner.Ships.OwnerAt(At(1,30,92))==null);
        var oldFlight=Current.Game.Gravship;
        var single=GravshipUtility.GenerateGravship(core);
        Check("single-deck-payload-normalizes-high-engine-to-bottom-zero",ShipLayers.TryData(single,out var data)&&data.layerCount==1&&data.engineDeckOffset==0&&single.ThingPlacements[core].local.y==0&&single.Foundations.Values.All(t=>t==TerrainDefOf.Substructure));
        var parent=Find.WorldObjects.Settlements.First(s=>s.Faction!=null&&!s.Faction.IsPlayer&&!s.HasMap);
        var destination=MapGenerator.GenerateMap(new IntVec3(60,1,60),parent,MapGeneratorDefOf.Encounter);
        foreach(var c in new CellRect(20,20,12,12))
        {
            foreach(var t in c.GetThingList(destination).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            destination.roofGrid.SetRoof(c,null);destination.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
        }
        var root=new IntVec3(25,0,25);single.Rotation=Rot4.North;
        Check("single-deck-landing-does-not-require-or-create-extra-floor",LayerMapComponent.For(destination)==null&&ShipLanding.Check(single,root,destination).Accepted);
        GravshipPlacementUtility.PlaceGravshipInMap(single,root,destination,out _);
        var landed=root+new IntVec3(2,0,2);
        Check("elevated-single-deck-native-placement-keeps-foundation-finish-cover-cargo",core.Spawned&&core.Map==destination&&core.Position==root&&destination.terrainGrid.FoundationAt(landed)==TerrainDefOf.Substructure&&destination.terrainGrid.TopTerrainAt(landed)==wood&&destination.terrainGrid.TempTerrainAt(landed)==flood&&cargo.Spawned&&cargo.Position==landed&&cargo.stackCount==19&&LayerMapComponent.For(destination)==null);
        Check("single-deck-takeoff-leaves-ordinary-lower-support-in-place",owner.Structure.HasSurface(At(1,30,92))&&map.roofGrid.RoofAt(new IntVec3(30,0,92))!=null);
        Current.Game.Gravship=oldFlight;Current.Game.CurrentMap=map;
    }

    private static void CheckShipFloorConstruction()
    {
        var cell=At(2,90,82);var below=At(1,90,82);var deck=ShipDeckTerrain.Def;
        Check("ordinary-colony-and-ship-floor-planning-are-distinct",owner.Structure.Validate(RimLayersDefOf.RL_StructuralFloor,shipOutside.Position,Rot4.North,true).Accepted&&!owner.Structure.Validate(deck,shipOutside.Position,Rot4.North,true).Accepted&&!owner.Structure.Validate(RimLayersDefOf.RL_StructuralFloor,cell,Rot4.North,true).Accepted);
        var occupied=shipTable.Position;var foundation=map.terrainGrid.FoundationAt(occupied);
        map.terrainGrid.RemoveFoundation(occupied,true);
        Check("foundation-removal-cannot-delete-support-under-occupied-building",map.terrainGrid.FoundationAt(occupied)==foundation&&shipTable.Spawned);
        var before=map.listerThings.AllThings.ToHashSet();
        map.roofGrid.SetRoof(cell,null);
        Check("bare-deck-is-walkable-and-can-be-dismantled",cell.Walkable(map)&&owner.Structure.CanDismantleSlab(cell),$"walk={cell.Walkable(map)},bare={owner.Structure.IsBareSlab(cell)},can={owner.Structure.CanDismantleSlab(cell)},contents={string.Join(",",cell.GetThingList(map).Select(t=>t.ThingID))}");
        map.terrainGrid.RemoveFoundation(cell,true);
        var refunds=map.listerThings.AllThings.Where(t=>!before.Contains(t)).ToArray();
        Check("bare-ship-foundation-refunds-exact-native-recipe-once",!owner.Structure.HasSurface(cell)&&refunds.Where(t=>t.def==ThingDefOf.Steel).Sum(t=>t.stackCount)==4&&refunds.Where(t=>t.def.defName=="GravlitePanel").Sum(t=>t.stackCount)==1);
        foreach(var t in refunds)t.Destroy(DestroyMode.Vanish);
        var placement=GenConstruct.CanPlaceBlueprintAt(deck,cell,Rot4.North,map);
        Check("native-ship-floor-blueprint-accepts-supported-open-air",placement.Accepted,placement.Reason);
        var blueprint=GenConstruct.PlaceBlueprintForBuild(deck,cell,map,Rot4.North,Faction.OfPlayer,null);blueprint.Destroy(DestroyMode.Cancel);
        var frame=(Frame)ThingMaker.MakeThing(deck.frameDef);frame.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(frame,cell,map);
        foreach(var cost in deck.CostListAdjusted(null)){var t=ThingMaker.MakeThing(cost.thingDef);t.stackCount=cost.count;frame.resourceContainer.TryAdd(t);}
        var original=frame.resourceContainer.ToArray();map.roofGrid.SetRoof(below,null);frame.CompleteConstruction(pawn);
        Check("ship-floor-final-roof-loss-keeps-frame-and-original-materials",frame.Spawned&&frame.resourceContainer.SequenceEqual(original)&&original.All(t=>!t.Destroyed));
        map.roofGrid.SetRoof(below,RoofDefOf.RoofConstructed);frame.CompleteConstruction(pawn);
        Check("native-frame-completion-builds-real-ship-foundation",frame.Destroyed&&map.terrainGrid.FoundationAt(cell)==deck&&owner.Structure.HasSurface(cell)&&original.All(t=>t.Destroyed));
        before=map.listerThings.AllThings.ToHashSet();map.roofGrid.SetRoof(below,null);owner.Collapses.Tick();
        var salvage=map.listerThings.AllThings.Where(t=>!before.Contains(t)).ToArray();
        Check("ship-floor-collapse-salvage-uses-actual-foundation-once",map.terrainGrid.FoundationAt(cell)==null&&!owner.StructuralCells.Contains(cell)&&salvage.Where(t=>t.def==ThingDefOf.Steel).Sum(t=>t.stackCount)==1&&salvage.All(t=>t.def.defName!="GravlitePanel"));
        foreach(var t in salvage)t.Destroy(DestroyMode.Vanish);
        map.roofGrid.SetRoof(below,RoofDefOf.RoofConstructed);ShipDeckTerrain.Install(owner,cell,false);map.roofGrid.SetRoof(cell,RoofDefOf.RoofConstructed);owner.PendingCollapse.Remove(cell);owner.Ships.MarkDirty();owner.Ships.Tick();
    }

    private static string[] inventoryIds(Pawn p)=>p.inventory.innerContainer.Select(t=>t.ThingID).ToArray();

    private static void RestoreGravship()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"gravship-baseline.json")));var d=baseline.RootElement;
        if(GenCommandLine.CommandLineArgPassed("rimlayersshiplandedreload"))
        {
            shipEngine=(Building_GravEngine)Find.Maps.SelectMany(m=>m.listerThings.AllThings).Single(t=>t.ThingID==d.GetProperty("engine").GetString());
            shipDestination=shipEngine.Map;map=shipDestination;owner=LayerMapComponent.For(map)!;
            VerifyShipCargo(map.listerThings.AllThings,"landed");
            if(ExistingShipDestination)ValidateExistingShipDestination();
            shipPawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==d.GetProperty("pawn").GetString());
            Check("landed-save-retains-upper-pawn-and-floor",owner.TryDecode(shipPawn.Position,out var p)&&p.LayerId==2&&owner.Structure.HasSurface(shipPawn.Position));
            Check("landed-save-keeps-native-inventory-once",inventoryIds(shipPawn).SequenceEqual(d.GetProperty("inventory").EnumerateArray().Select(x=>x.GetString())));
            Check("landed-save-has-no-second-native-flight-owner",Current.Game.Gravship==null);
            LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(shipPawn.Position.ToVector3Shifted(),16);
            pendingScreenshot=Path.Combine(directory,"reloaded-layer-ship.png");screenshotHold=3;
            if(NativeShip)
            {
                shipSource=Find.Maps.Single(m=>m.uniqueID==d.GetProperty("source").GetInt32());
                shipConsole=shipEngine.GravshipComponents.OfType<CompPilotConsole>().Single();
                shipEngine.cooldownCompleteTick=0; // Fixture time advance only; production launch rules stay native.
                shipPhase=5;stage=330;ticks=0;return;
            }
            stage=11;ticks=0;return;
        }
        layerShip=Find.WorldObjects.AllWorldObjects.OfType<Gravship>().Single();shipEngine=layerShip.Engine;
        shipSource=Find.Maps.Single(m=>m.uniqueID==d.GetProperty("source").GetInt32());
        shipPawn=layerShip.Pawns.Single(p=>p.ThingID==d.GetProperty("pawn").GetString());
        shipItem=layerShip.Things.Single(t=>t.ThingID==d.GetProperty("item").GetString());
        shipTable=(Building_WorkTable)layerShip.Things.Single(t=>t.ThingID==d.GetProperty("table").GetString());
        VerifyShipCargo(layerShip.Things,"reloaded-flight");
        shipOutside=shipSource.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty("outside").GetString());
        Check("in-flight-restart-keeps-layer-facts-and-native-identities",ShipLayers.TryData(layerShip,out var payload)&&payload.floors.Count==d.GetProperty("floorCount").GetInt32()&&!shipPawn.Spawned&&!shipItem.Spawned);
        Check("flight-payload-reads-or-upgrades-to-version-two",payload is {version:2}&&payload.engineDeckOffset==ShipEngineLayer);
        Check("in-flight-restart-keeps-native-inventory-once",inventoryIds(shipPawn).SequenceEqual(d.GetProperty("inventory").EnumerateArray().Select(x=>x.GetString())));
        if(NativeShip)
        {
            bool legacy=GenCommandLine.CommandLineArgPassed("rimlayerslegacyflight");
            var quota=LayerExpansionQuota.Current!;
            Check(legacy?"legacy-flight-load-has-no-quota-reservation":"flight-load-retains-saved-reservation",
                legacy?quota.Reservations.Count==0:quota.Reservations.Any(r=>r.EngineId==shipEngine.thingIDNumber&&r.Destination==layerShip.destinationTile&&r.Top==ShipDecks-1));
            int generatedBeforeArrival=quota.Generated;
            RimLayersMod.Settings.SpaceExpansion=false;RimLayersMod.Settings.UpperLimit=0;RimLayersMod.Settings.GlobalLimit=0;
            if(ExistingShipDestination)
            {
                shipDestination=Find.WorldObjects.MapParentAt(layerShip.destinationTile).Map;
                Check("flight-save-keeps-reservation-without-expanding-destination",LayerMapComponent.For(shipDestination)==null&&(legacy||LayerExpansionQuota.Current!.Reservations.Any(r=>r.EngineId==shipEngine.thingIDNumber&&r.Destination==layerShip.destinationTile&&r.Top==ShipDecks-1)));
                RimLayersMod.Settings.SpaceExpansion=false;RimLayersMod.Settings.UpperLimit=0;RimLayersMod.Settings.GlobalLimit=0;
                GravshipUtility.ArriveExistingMap(layerShip);ValidateExistingShipDestination();
                Check("promised-arrival-converts-reservation-after-settings-lowered",LayerExpansionQuota.Current!.Reserved==0&&LayerMapComponent.For(shipDestination)!.Layout!.LayerCount==ShipDecks);
                Check("higher-arrival-needs-new-quota-after-allocation",!LayerExpansionQuota.Current.Check(shipDestination,ShipDecks,shipEngine.thingIDNumber).Accepted);
                map=shipDestination;owner=LayerMapComponent.For(map)!;
                shipPhase=8;stage=330;ticks=0;return;
            }
            else
            {
            var previous=Find.World.info.initialMapSize;
            try {Find.World.info.initialMapSize=new IntVec3(145,1,157);GravshipUtility.ArriveNewMap(layerShip);}
            finally {Find.World.info.initialMapSize=previous;}
            shipDestination=Find.WorldObjects.MapParentAt(layerShip.destinationTile).Map;
            Check("native-arrival-generates-layered-destination-map",LayerMapComponent.For(shipDestination)?.Layout?.LayerCount==ShipDecks);
            Check("new-map-promised-arrival-converts-only-needed-reservation",quota.Reserved==0&&quota.Generated==generatedBeforeArrival+ShipDecks-1);
            Check("new-map-higher-arrival-still-rechecks-lowered-limits",!quota.Check(shipDestination,ShipDecks,shipEngine.thingIDNumber).Accepted);
            }
        }
        else
        {
            var parent=SettleUtility.AddNewHome(layerShip.destinationTile,Faction.OfPlayer);
            shipDestination=MapGenerator.GenerateMap(new IntVec3(145,1,157),parent,MapGeneratorDefOf.Encounter);
        }
        map=shipDestination;owner=LayerMapComponent.For(map)!;Current.Game.CurrentMap=map;
        Check("destination-map-has-different-native-layer-stride",owner!=null&&owner.Layout!.Stride!=d.GetProperty("oldStride").GetInt32());
        shipLandingRoot=new IntVec3(65,0,70);layerShip.Rotation=Rot4.East;
        // The fixture clears only its native landing footprint; production validation never wipes upper structures.
        foreach(var c in new CellRect(45,45,45,45))
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn and not GravshipLandingMarker){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(45,45,45,45).Contains(c));
        var first=payload!.floors.First();var rotated=PrefabUtility.GetAdjustedLocalPosition(first.local,Rot4.East);
        var blocked=owner!.ToStorage(new LayerCell(rotated.y,shipLandingRoot.x+rotated.x,shipLandingRoot.z+rotated.z));
        var blocker=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),blocked,map);
        Check("upper-landing-collision-rejected-before-spawning-cargo",!ShipLanding.Check(layerShip,shipLandingRoot,map).Accepted&&!shipPawn.Spawned);
        if(NativeShip)
        {
            var preview=map.listerThings.AllThings.OfType<GravshipLandingMarker>().Single();
            preview.Position=shipLandingRoot;preview.GravshipRotation=Rot4.East;preview.CacheCells();preview.Notify_Moved();
            preview.BeginLanding(Find.GravshipController);
            Check("blocked-confirm-keeps-marker-and-does-not-start-cutscene",preview.Spawned&&!WorldComponent_GravshipController.CutsceneInProgress&&!shipPawn.Spawned);
        }
        blocker.Destroy(DestroyMode.Vanish);
        Check("upper-or-gap-band-is-not-a-landing-origin",!ShipLanding.Check(layerShip,At(1,65,70),map).Accepted);
        Check("legal-rotated-ground-origin-accepted",ShipLanding.Check(layerShip,shipLandingRoot,map).Accepted);
        if(NativeShip)
        {
            var marker=map.listerThings.AllThings.OfType<GravshipLandingMarker>().Single();
            marker.Position=shipLandingRoot;marker.GravshipRotation=Rot4.East;marker.CacheCells();marker.Notify_Moved();
            Find.DesignatorManager.Deselect();
            marker.BeginLanding(Find.GravshipController);
            // Match the native ConfirmLandGravship caller: it releases the destroyed marker immediately.
            AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").SetValue(Find.GravshipController,null);
            shipPhase=2;stage=330;ticks=0;return;
        }
        Find.WorldObjects.Remove(layerShip);
        AccessTools.Method(typeof(WorldComponent_GravshipController),"PlaceGravship").Invoke(Find.GravshipController,new object[]{layerShip,shipLandingRoot,map});
        ValidateShipLanding();
    }
    private static int shipReturnView;
    private static void ValidateShipLanding()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"gravship-baseline.json")));var d=baseline.RootElement;
        VerifyShipCargo(map.listerThings.AllThings,"landed");
        if(ExistingShipDestination)ValidateExistingShipDestination();
        Check("core-retains-selected-deck-offset-after-rotation-and-landing",owner.TryDecode(shipEngine.Position,out var coreAt)&&coreAt.LayerId==ShipEngineLayer);
        Check("native-placement-returns-original-core-on-new-main-map",shipEngine.Spawned&&shipEngine.Map==map&&Find.CurrentMap==map);
        Check("native-placement-keeps-upper-pawn-layer-and-draft",owner!.TryDecode(shipPawn.Position,out var here)&&here.LayerId==2&&shipPawn.Drafted&&owner.Structure.HasSurface(shipPawn.Position));
        Check("native-placement-keeps-original-silver-stack",shipItem.Map==map&&shipItem.stackCount==37&&owner.TryDecode(shipItem.Position,out var itemCell)&&itemCell.LayerId==2);
        var deckDetail=At(1,shipLandingRoot.x+3,shipLandingRoot.z-3); // East rotates source offset (3,3) to (3,-3).
        using var floorBaseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"ship-cabin-baseline.json")));var originalFloor=floorBaseline.RootElement;
        Check("decorated-floor-underfloor-and-temporary-terrain-survive",map.terrainGrid.TopTerrainAt(deckDetail)?.defName==originalFloor.GetProperty("detailTop").GetString()&&
            map.terrainGrid.UnderTerrainAt(deckDetail)?.defName==originalFloor.GetProperty("detailUnder").GetString()&&map.terrainGrid.FoundationAt(deckDetail)?.defName==originalFloor.GetProperty("detailFoundation").GetString()&&map.terrainGrid.TempTerrainAt(deckDetail)?.defName==originalFloor.GetProperty("detailTemp").GetString(),$"cell={deckDetail}, top={map.terrainGrid.TopTerrainAt(deckDetail)?.defName}, under={map.terrainGrid.UnderTerrainAt(deckDetail)?.defName}, temp={map.terrainGrid.TempTerrainAt(deckDetail)?.defName}");
        var stand=map.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty("stand").GetString());
        Check("native-holder-retains-original-contained-apparel-once",((IThingHolder)stand).GetDirectlyHeldThings().Count(t=>t.ThingID==d.GetProperty("contained").GetString())==1);
        Check("native-workbench-bills-survive-cross-map-placement",shipTable.Map==map&&shipTable.BillStack.Bills.OfType<Bill_Production>().Single().repeatCount==7);
        Check("native-zone-settings-and-allowed-area-follow-upper-deck",map.zoneManager.AllZones.OfType<Zone_Stockpile>().Any(z=>z.label=="Upper ship stores"&&z.settings.Priority==StoragePriority.Critical&&z.Cells.All(c=>owner.TryDecode(c,out var l)&&l.LayerId==2))&&shipPawn.playerSettings.AreaRestrictionInPawnCurrentMap?.Label=="Ship crew");
        var actors=layerShip.Things.Concat(layerShip.Pawns.Cast<Thing>()).ToList();
        bool identity=actors.Select(t=>t.ThingID).OrderBy(x=>x).SequenceEqual(d.GetProperty("actors").EnumerateArray().Select(x=>x.GetProperty("ThingID").GetString()!).OrderBy(x=>x));
        bool placement=true;
        foreach(var actor in d.GetProperty("actors").EnumerateArray())
        {
            var pos=actor.GetProperty("position");var encoded=new IntVec3(pos.GetProperty("x").GetInt32(),pos.GetProperty("y").GetInt32(),pos.GetProperty("z").GetInt32());
            var rotatedCell=PrefabUtility.GetAdjustedLocalPosition(encoded,Rot4.East);
            var real=actors.Single(t=>t.ThingID==actor.GetProperty("ThingID").GetString());
            var expectedRotation=new Rot4((actor.GetProperty("rotation").GetInt32()+1)%4);
            var size=real.def.size;
            if(!real.def.rotatable&&size.x==size.z)
            {GenAdj.AdjustForRotation(ref rotatedCell,ref size,real.def.defaultPlacingRot,Rot4.East);expectedRotation=real.def.defaultPlacingRot;}
            else if(!real.def.rotatable&&real.def.category!=ThingCategory.Building)expectedRotation=real.def.defaultPlacingRot;
            placement&=real.Spawned&&real.Map==map&&owner.TryDecode(real.Position,out var l)&&
                l.LayerId==rotatedCell.y&&l.X==shipLandingRoot.x+rotatedCell.x&&l.Z==shipLandingRoot.z+rotatedCell.z&&real.Rotation==expectedRotation;
        }
        Check("all-native-cargo-identities-are-owned-and-spawned-once",identity&&placement);
        Check("nonship-upper-cargo-stays-on-source-map-after-arrival",shipOutside.Spawned&&shipOutside.Map==shipSource&&shipOutside.stackCount==13);
        CameraJumper.TryJump(shipPawn,CameraJumper.MovementMode.Cut);LayerView.Switch(owner,2);
        shipPhase=1;stage=330;ticks=0;
    }
}
