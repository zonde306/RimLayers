using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Ships;
using RimLayers.Presentation;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void CheckVgeFuel(Building_GravEngine engine)
    {
        var tank=(ThingWithComps)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("ChemfuelTank"),At(2,82,72),map);tank.SetFaction(Faction.OfPlayer);
        var thruster=(ThingWithComps)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("SmallThruster"),At(2,77,78),map,Rot4.South);thruster.SetFaction(Faction.OfPlayer);
        var pipes=new List<Thing>();
        foreach(var c in Enumerable.Range(77,6).Select(x=>At(2,x,72)).Concat(Enumerable.Range(73,5).Select(z=>At(2,77,z))))
        {var pipe=GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("VGE_AstrofuelPipe"),c,map);pipe.SetFaction(Faction.OfPlayer);pipes.Add(pipe);}
        owner.Ships.MarkDirty();owner.Ships.Tick();
        var facility=thruster.GetComp<CompGravshipThruster>();
        var resource=thruster.AllComps.First(c=>c.GetType().Name=="CompResourceThruster");
        var storage=tank.AllComps.First(c=>c.GetType().Name=="CompResourceStorage");
        Check("vge-upper-fuel-provider-links-to-engine",tank.GetComp<CompGravshipFacility>().engine==engine);
        Check("vge-dry-upper-thruster-remains-disabled",!facility.CanBeActive);
        AccessTools.Method(storage.GetType(),"AddResource").Invoke(storage,new object[]{200f});
        var net=AccessTools.PropertyGetter(resource.GetType(),"AstrofuelNet").Invoke(resource,null);
        if(net!=null)AccessTools.Method(net.GetType(),"PipeSystemTick").Invoke(net,null);
        Check("vge-real-pipe-network-enables-upper-thruster",net!=null&&facility.CanBeActive,$"net={net}; linked={facility.engine}; canLink={facility.CanLink()}");
        Find.ResearchManager.gravEngineInspected=true;
        var console=engine.GravshipComponents.OfType<CompPilotConsole>().Single();
        var ready=engine.CanLaunch(console);
        Check("vge-native-launch-eligibility-with-real-fuel",ready.Accepted,ready.Reason);
        if(VanillaGravshipCompatibility.Chapter2)
        {
            var combatType=AccessTools.TypeByName("VanillaGravshipExpanded2.WorldComponent_GravshipCombat");
            var combat=AccessTools.Field(combatType,"Instance").GetValue(null);
            var locked=AccessTools.Field(combatType,"engineLockedRemotely");
            var old=locked.GetValue(combat);
            try { locked.SetValue(combat,true);var report=engine.CanLaunch(console);
                Check("chapter2-remote-lock-still-vetoes-launch",!report.Accepted&&report.Reason=="VGE_EngineLockedRemotely".Translate().ToString(),report.Reason); }
            finally { locked.SetValue(combat,old); }
        }
        var stateField=AccessTools.Field(AccessTools.TypeByName("VanillaGravshipExpanded.Dialog_BeginRitual_ShowRitualBeginWindow_Patch"),"state");
        var oldState=stateField.GetValue(null);
        try
        {
            var state=Activator.CreateInstance(stateField.FieldType,new object?[]{null,new TargetInfo(engine),null,pawn,null,PlanetTile.Invalid});
            stateField.SetValue(null,state);
            Action openRitual=()=>{};
            var args=new object[]{PlanetTile.Invalid,engine,openRitual};
            AccessTools.Method(typeof(LayerShipConfirmedDeparture),"Prefix").Invoke(null,args);
            Check("vge-tile-selection-keeps-cancellable-ritual-action-unwrapped",ReferenceEquals(args[2],openRitual));
        }
        finally { stateField.SetValue(null,oldState); }
        float before=(float)AccessTools.PropertyGetter(storage.GetType(),"AmountStored").Invoke(storage,null)!;
        var launch=AccessTools.Method(AccessTools.TypeByName("VanillaGravshipExpanded.GravshipUtility_PreLaunchConfirmation_Patch"),"ExecuteGravshipLaunch");
        launch.Invoke(null,new object[]{engine,PlanetTile.Invalid});
        float after=(float)AccessTools.PropertyGetter(storage.GetType(),"AmountStored").Invoke(storage,null)!;
        Check("vge-direct-ritual-rejection-precedes-fuel-and-cutscene",before==after&&!WorldComponent_GravshipController.CutsceneInProgress&&engine.Spawned);
        var fuel=AccessTools.TypeByName("VanillaGravshipExpanded.GravshipFuelProviderUtility");
        AccessTools.Method(fuel,"ConsumeFuelRatioForAllProviders").Invoke(null,new object?[]{engine,.25f,null});
        float spent=(float)AccessTools.PropertyGetter(storage.GetType(),"AmountStored").Invoke(storage,null)!;
        Check("vge-native-provider-consumes-real-upper-tank-fuel",spent<before&&spent>=0,$"before={before}; after={spent}");
        // These fixtures have no independent roofs; keep the manifest test focused on its known cargo.
        foreach(var pipe in pipes)pipe.Destroy(DestroyMode.Vanish);
        thruster.Destroy(DestroyMode.Vanish);tank.Destroy(DestroyMode.Vanish);
        owner.Ships.MarkDirty();owner.Ships.Tick();
    }
    private static void RestoreVge()
    {
        var ship=Find.WorldObjects.AllWorldObjects.OfType<Gravship>().Single();
        Check("reloaded-vge-layer-payload",ship!=null&&ShipLayers.TryData(ship,out var data)&&data.layerCount==2&&data.floors.Count==98);
        var extra=ship!.AllComps.First(c=>c.GetType().Name=="ExtraGravshipDataComp");
        var colors=(Dictionary<IntVec3,Color>)AccessTools.Field(extra.GetType(),"vacBarrierRoofColors").GetValue(extra)!;
        Check("reloaded-color-retains-upper-deck",colors.Any(p=>p.Key.y==1&&p.Value==Color.magenta));
        var root=At(1,80,75);
        var report=ShipLanding.Check(ship,root,map);Check("reloaded-ship-can-land-high",report.Accepted,report.Reason);
        Find.WorldObjects.Remove(ship);Current.Game.Gravship=ship;
        GravshipPlacementUtility.PlaceGravshipInMap(ship,root,map,out _);
        Current.Game.Gravship=null;
        Check("reloaded-core-and-cargo-spawned",ship.Engine.Spawned&&ship.Things.Any(t=>t.def==ThingDefOf.Silver&&t.Spawned&&t.stackCount==37));
        CameraJumper.TryHideWorld();Current.Game.CurrentMap=map;
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(At(2,80,75).ToVector3Shifted(),12);
        pendingScreenshot=Path.Combine(directory,"vge-landed-upper-deck.png");screenshotHold=3;stage=380;ticks=0;
    }
    private static void BeginVge()
    {
        Check("vge-optional-adapter-enabled",VanillaGravshipCompatibility.Active);
        Check("no-compile-time-vge-or-vef-dependencies",!typeof(RimLayersMod).Assembly.GetReferencedAssemblies().Any(a=>a.Name!.Contains("GravshipExpanded")||a.Name=="VEF"));
        var roofUtility=AccessTools.TypeByName("VanillaGravshipExpanded.VacBarrierRoofUtility");
        var setColor=AccessTools.Method(roofUtility,"SetVacBarrierRoofColorAt");
        var getColor=AccessTools.Method(roofUtility,"VacBarrierRoofColorAt");
        var rect=new CellRect(77,72,7,7);
        var source=At(1,80,75);
        foreach(var name in new[]{"VGE_GravjumperEngine","VGE_GravhulkEngine","GravEngine"})
        {
            CoexistPlatform(rect.ExpandedBy(1));
            foreach(var c in rect)map.terrainGrid.SetFoundation(At(1,c.x,c.z),TerrainDefOf.Substructure);
            var engine=(Building_GravEngine)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed(name),source,map);
            engine.SetFaction(Faction.OfPlayer);
            foreach(var c in rect)
            {
                map.roofGrid.SetRoof(At(1,c.x,c.z),RoofDefOf.RoofConstructed);
                ShipDeckTerrain.Install(owner,At(2,c.x,c.z),false);map.fogGrid.Unfog(At(2,c.x,c.z));
            }
            var consoleDef=DefDatabase<ThingDef>.GetNamed(name=="VGE_GravjumperEngine"?"VGE_PilotCockpit":name=="VGE_GravhulkEngine"?"VGE_PilotBridge":"PilotConsole");
            var console=(ThingWithComps)GenSpawn.Spawn(consoleDef,At(2,80,75),map);
            console.SetFaction(Faction.OfPlayer);
            var cargo=ThingMaker.MakeThing(ThingDefOf.Silver);cargo.stackCount=37;GenSpawn.Spawn(cargo,At(2,81,76),map);
            foreach(var c in new[]{new IntVec3(77,0,72),new IntVec3(83,0,78),new IntVec3(83,0,72),new IntVec3(77,0,78)})
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(1,c.x,c.z),map).SetFaction(Faction.OfPlayer);
            var roofCell=At(2,82,77);
            map.roofGrid.SetRoof(roofCell,DefDatabase<RoofDef>.GetNamed("VGE_VacBarrierRoof"));
            setColor.Invoke(null,new object[]{map,roofCell,Color.magenta});
            var scaffold=At(2,77,75);
            map.terrainGrid.SetFoundation(scaffold,DefDatabase<TerrainDef>.GetNamed("VGE_GravshipSubscaffold"));
            owner.Ships.MarkDirty();owner.Ships.Tick();
            Check(name+"-both-decks-owned",owner.Ships.Get(engine)?.Cells.Count==98);
            Check(name+"-upper-console-linked",console.GetComp<CompPilotConsole>().engine==engine&&console.GetComp<CompPilotConsole>().CanBeActive,$"engine={console.GetComp<CompPilotConsole>().engine}; links={engine.AffectedByFacilities.LinkedFacilitiesListForReading.Count}");
            Check(name+"-scaffold-not-charged",VanillaGravshipCompatibility.SupportCost(map,owner.Ships.Get(engine)!.Cells)==97);
            Check(name+"-launch-rules-accept-structure",owner.Ships.LaunchReport(engine).Accepted,owner.Ships.LaunchReport(engine).Reason);
            if(name=="GravEngine")CheckVgeFuel(engine);
            engine.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
            var ship=GravshipUtility.GenerateGravship(engine);
            Current.Game.Gravship=ship;
            Check(name+"-capture-has-two-decks-and-upper-cargo",ShipLayers.TryData(ship,out var data)&&data.layerCount==2&&data.floors.Count==98&&ship.Things.Contains(cargo)&&!cargo.Spawned);
            Check(name+"-ordinary-platform-stays",rect.All(c=>map.terrainGrid.TopTerrainAt(At(1,c.x,c.z))==RimLayersDefOf.RL_StructuralFloor));
            var extra=ship.AllComps.First(c=>c.GetType().Name=="ExtraGravshipDataComp");
            var colors=(Dictionary<IntVec3,Color>)AccessTools.Field(extra.GetType(),"vacBarrierRoofColors").GetValue(extra)!;
            Check(name+"-color-encoded-on-upper-deck",colors.TryGetValue(new IntVec3(2,1,2),out var col)&&col==Color.magenta,string.Join(";",colors.Keys));
            ship.Rotation=Rot4.East;
            if(name=="GravEngine"&&VanillaGravshipCompatibility.Chapter2)
            {
                var jammer=(ThingWithComps)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("VGE_MechanoidSignalJammer"),new IntVec3(95,0,75),map);
                jammer.SetFaction(Faction.OfMechanoids);
                if(jammer.GetComp<CompPowerTrader>() is {} power)power.PowerOn=true;
                var blocked=ShipLanding.Check(ship,source,map);
                Check("chapter2-ground-jammer-blocks-upper-deck-landing",!blocked.Accepted&&blocked.Reason=="VGE_DestinationJammed".Translate().ToString(),blocked.Reason);
                jammer.Destroy(DestroyMode.Vanish);
                Check("chapter2-jammer-removal-restores-landing",ShipLanding.Check(ship,source,map).Accepted);
            }
            var check=ShipLanding.Check(ship,source,map);
            Check(name+"-rotated-high-landing-valid",check.Accepted,check.Reason);
            GravshipPlacementUtility.PlaceGravshipInMap(ship,source,map,out _);
            Current.Game.Gravship=null;
            var rotated=PrefabUtility.GetAdjustedLocalPosition(new IntVec3(2,1,2),Rot4.East);
            var landed=source+ShipLayers.Offset(owner,1,rotated);
            Check(name+"-rotated-color-restored-to-upper-floor",getColor.Invoke(null,new object[]{map,landed}) is Color color&&color==Color.magenta);
            Check(name+"-cargo-and-custom-core-landed",engine.Spawned&&engine.Position==source&&cargo.Spawned&&cargo.stackCount==37&&owner.TryDecode(cargo.Position,out var at)&&at.LayerId==2);
            var second=GravshipUtility.GenerateGravship(engine);
            Current.Game.Gravship=second;
            Check(name+"-second-capture-does-not-duplicate",second.Things.Count(t=>t==cargo)==1&&ShipLayers.TryData(second,out var again)&&again.floors.Count==98);
        }
        var flight=Current.Game.Gravship;flight.Tile=map.Tile;flight.destinationTile=map.Tile;Find.WorldObjects.Add(flight);
        Current.Game.Gravship=null;
        GameDataSaveLoader.SaveGame("RimLayers_V1Vge");
        LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,80,75).ToVector3Shifted(),12);
        pendingScreenshot=Path.Combine(directory,"vge-restored-platform.png");screenshotHold=3;stage=380;ticks=0;
    }
}
