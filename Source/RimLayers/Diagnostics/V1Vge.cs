using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
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
    /// <summary>
    /// Deck removal, finish removal and the cross-deck facility/pipe paths, all asserted on the two
    /// decks the scenario just built. Runs per engine def so a renamed/cloned engine is covered too.
    /// </summary>
    private static void CheckVgeDecks(string name,Building_GravEngine engine,ThingWithComps console,CellRect rect)
    {
        var lower=At(1,80,75);
        // Keep the probe off the pilot bridge footprint: VGE_PilotBridge is 5x3 at x78..82 z74..76,
        // and spawning the beam there would displace the bridge before capture.
        var probe=At(2,77,74);
        // 1. ValidSubstructure now describes every deck, not just the engine's own band.
        var decks=owner.Ships.Get(engine)!.Cells;
        Check(name+"-valid-substructure-covers-upper-deck",
            engine.ValidSubstructure.Contains(probe)&&engine.ValidSubstructureNoRegen.Contains(probe)&&
            engine.ValidSubstructure.Count==decks.Count,$"count={engine.ValidSubstructure.Count}; owned={decks.Count}");
        // 2. The inspect text no longer claims the upper console is out of engine range.
        var text=console.GetComp<CompPilotConsole>().CompInspectStringExtra();
        Check(name+"-upper-facility-inspect-has-no-range-warning",
            !text.Contains("MessageMustBePlacedInRangeOfGravEngine".Translate().ToString()),text.Replace('\n','|'));
        // 2b. The exact live pairing: the player builds the VANILLA PilotConsole on a VGE hull. VGE only
        // adds its own facilities to the vanilla GravEngine list; nothing adds PilotConsole to a VGE
        // engine. Native membership is engine-list only, so that console is never even a candidate and
        // reports NotConnectedToGravEngine forever (Player-prev.log: allowed=False canLink=True
        // belongs=True distance=3.6/200 engineFacilities=0). This asserts the fail-open rule end to end.
        //
        // Only for the VGE engines: when the engine under test IS the vanilla GravEngine the scenario
        // console is already a PilotConsole holding the single PilotConsole slot (maxSimultaneous 1), so
        // a second one is rejected by native capacity rather than by membership. That pairing is
        // already covered by -upper-console-linked above.
        if(DefDatabase<ThingDef>.GetNamedSilentFail("PilotConsole") is {} vanillaConsole&&console.def!=vanillaConsole)
        {
            var vanilla=(ThingWithComps)GenSpawn.Spawn(vanillaConsole,At(2,80,72),map,Rot4.North);
            vanilla.SetFaction(Faction.OfPlayer);
            owner.Ships.MarkDirty();owner.Ships.Tick();
            var facility=vanilla.GetComp<CompFacility>();
            var engineList=engine.def.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities;
            Check(name+"-vanilla-console-links-to-this-engine-across-decks",
                facility!=null&&facility.LinkedBuildings.Contains(engine)&&vanilla.GetComp<CompPilotConsole>().engine==engine,
                $"links={string.Join(",",facility?.LinkedBuildings.Select(t=>t.def.defName)??Enumerable.Empty<string>())}; "+
                $"declares={string.Join(",",facility?.Props.linkableBuildings?.Select(d=>d.defName)??Enumerable.Empty<string>())}; "+
                $"engineList={string.Join(",",(engineList??new List<ThingDef>()).Select(d=>d.defName))}; "+
                $"inEngineList={engineList?.Contains(vanillaConsole)==true}; canLink={facility?.CanLink()}");
            // Destroy before the capture: Gravship.AddThing keeps the first CompPilotConsole it sees.
            vanilla.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
        }
        // 2c. The same pairing, reached the way a LOADED SAVE reaches it.
        //
        // 2b spawns the console while the engine is already linked, so only the engine-side
        // enumeration runs. A save load instead runs PostSpawnSetup -> CompFacility.
        // LinkToNearbyBuildings, which enumerates the FACILITY-side linkableBuildings - a different
        // list that never named a VGE engine. That asymmetry is why this stayed green on the fixture
        // and red on the player's save for two rounds. A despawn/spawn cycle reproduces it exactly.
        if(DefDatabase<ThingDef>.GetNamedSilentFail("PilotConsole") is {} reloadDef&&console.def!=reloadDef)
        {
            var reload=(ThingWithComps)GenSpawn.Spawn(reloadDef,At(2,80,71),map,Rot4.North);
            reload.SetFaction(Faction.OfPlayer);
            owner.Ships.MarkDirty();owner.Ships.Tick();
            var comp=reload.GetComp<CompFacility>();
            var before=comp?.LinkedBuildings.Contains(engine)==true;
            var host=reload.Map;var at=reload.Position;
            reload.DeSpawn();owner.Ships.MarkDirty();owner.Ships.Tick();
            GenSpawn.Spawn(reload,at,host,Rot4.North);reload.SetFaction(Faction.OfPlayer);
            owner.Ships.MarkDirty();owner.Ships.Tick();
            var after=comp?.LinkedBuildings.Contains(engine)==true;
            Check(name+"-vanilla-console-survives-a-respawn-like-a-save-load",
                before&&after&&reload.GetComp<CompPilotConsole>()?.engine==engine,
                $"before={before}; after={after}; links={string.Join(",",comp?.LinkedBuildings.Select(t=>t.def.defName)??Enumerable.Empty<string>())}; "+
                $"engineFacilities={engine.AffectedByFacilities.LinkedFacilitiesListForReading.Count}");
            reload.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
        }
        // 3. A plain-facility engine link (VGE_GravshipStructuralBeam) is offered and accepted across decks.
        if(DefDatabase<ThingDef>.GetNamedSilentFail("VGE_GravshipStructuralBeam") is {} beamDef)
        {
            var beam=(ThingWithComps)GenSpawn.Spawn(beamDef,probe,map);beam.SetFaction(Faction.OfPlayer);
            owner.Ships.MarkDirty();owner.Ships.Tick();
            // The beam is a Wall-derived structure, so its own cells must be owned by this ship before
            // an ownership-checked link can be accepted; place it on an owned deck cell instead of
            // relying on the freshly installed cell.
            var candidates=CompAffectedByFacilities.PotentialThingsToLinkTo(engine.def,engine.Position,engine.Rotation,map).ToArray();
            Check(name+"-plain-facility-candidate-spans-decks",candidates.Contains(beam),
                $"candidates={string.Join(",",candidates.Select(t=>t.def.defName))}; owned={owner.Ships.Belongs(engine,beam)}");
            // Native CanLinkTo returns false for a facility that is already linked (its final loop),
            // so a successful cross-deck link is exactly "present in LinkedFacilities" + "not relinkable".
            var linked=engine.AffectedByFacilities.LinkedFacilitiesListForReading.Contains(beam);
            Check(name+"-plain-facility-links-across-decks",linked&&!engine.AffectedByFacilities.CanLinkTo(beam),
                $"linked={linked}; canLink={engine.AffectedByFacilities.CanLinkTo(beam)}; owned={owner.Ships.Belongs(engine,beam)}");
            Check(name+"-plain-facility-active-across-decks",engine.AffectedByFacilities.IsFacilityActive(beam));
            beam.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
        }
        // 4. Both deck kinds can be removed now: Removable is `layerable`, so this used to be false.
        Check(name+"-bare-upper-deck-can-be-dismantled",map.terrainGrid.CanRemoveFoundationAt(probe)&&
            map.terrainGrid.CanRemoveTopLayerAt(probe)==false,$"foundation={map.terrainGrid.CanRemoveFoundationAt(probe)}");
        // A finish layer over the deck can be removed without destroying the deck itself.
        map.terrainGrid.SetTerrain(probe,DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
        Check(name+"-deck-finish-is-removable",map.terrainGrid.CanRemoveTopLayerAt(probe),
            $"top={map.terrainGrid.TopTerrainAt(probe)}; foundation={map.terrainGrid.FoundationAt(probe)}");
        map.terrainGrid.RemoveTopLayer(probe,false);
        Check(name+"-deck-finish-removal-keeps-foundation",
            map.terrainGrid.FoundationAt(probe)==ShipDeckTerrain.Def&&owner.Structure.HasSurface(probe),
            $"foundation={map.terrainGrid.FoundationAt(probe)}; surface={owner.Structure.HasSurface(probe)}");
        // Native safety still wins: an item on the deck blocks dismantling.
        var blocker=ThingMaker.MakeThing(ThingDefOf.Silver);GenSpawn.Spawn(blocker,probe,map);
        Check(name+"-occupied-deck-still-blocks-removal",!map.terrainGrid.CanRemoveFoundationAt(probe));
        blocker.Destroy(DestroyMode.Vanish);
        // 5. Astrofuel and oxygen pipes join across decks of the same ship. This must run before the
        //    capture below: capture forgets the ship's claims, and pipe vertical adjacency is keyed on
        //    ship ownership. CheckVgePipes removes everything it spawns before returning.
        CheckVgePipes(name,engine);
        // Restore the fixture cell for the capture that follows.
        map.terrainGrid.SetTerrain(probe,RimLayersDefOf.RL_OpenAir);
        owner.Ships.MarkDirty();owner.Ships.Tick();
    }
    private static void CheckVgePipes(string name,Building_GravEngine engine)
    {
        if(!RimLayers.Environment.PipeLinks.Active){Check(name+"-pipe-hook-installed",false,"PipeSystem missing");return;}
        var riserDef=DefDatabase<ThingDef>.GetNamedSilentFail("RL_AstrofuelPipeRiser");
        if(riserDef==null){Check(name+"-pipe-riser-def-present",false,"RL_AstrofuelPipeRiser not injected");return;}
        Check(name+"-pipe-riser-def-present",typeof(Building).IsAssignableFrom(riserDef.thingClass)&&
            riserDef.thingClass.FullName=="PipeSystem.Building_Pipe",riserDef.thingClass.FullName??"<null>");
        // Ordinary pipes on two decks must stay separate: only an explicit riser may cross a boundary.
        // The whole pipe fixture sits on plain ground south of the hull, because VGE's engines cap the
        // substructure at 100 cells: the hull cannot grow past 7x7, and a 5x5 VGE_GravhulkEngine
        // leaves no room for a 6x3 tank inside it. Same-layer pipe links are pure PipeSystem behaviour
        // and need no ship ownership; only the vertical hop involves RimLayers, and that hop is what
        // these assertions measure.
        var lower=At(1,80,79);
        var upper=At(2,80,79);
        var pipeDef=DefDatabase<ThingDef>.GetNamed("VGE_AstrofuelPipe");
        var lowerPipe=GenSpawn.Spawn(pipeDef,lower,map);
        var upperPipe=GenSpawn.Spawn(pipeDef,upper,map);
        lowerPipe.SetFaction(Faction.OfPlayer);upperPipe.SetFaction(Faction.OfPlayer);
        Check(name+"-plain-pipe-does-not-cross-decks",!ReferenceEquals(PipeNetOf(lowerPipe),PipeNetOf(upperPipe)),
            $"lower={PipeNetOf(lowerPipe)}; upper={PipeNetOf(upperPipe)}");
        // A riser on each deck at the same (x,z) column joins them. Native RegisterConnector does the
        // merge; RimLayers only supplies the vertical adjacency. The risers must NOT share a cell with
        // a pipe: CompResource.RemovePipes destroys the pipes already sitting on a connector's own
        // footprint, so a same-cell riser would delete the pipes this assertion is about.
        var riserLower=GenSpawn.Spawn(riserDef,At(1,79,79),map);
        var riserUpper=GenSpawn.Spawn(riserDef,At(2,79,79),map);
        riserLower.SetFaction(Faction.OfPlayer);riserUpper.SetFaction(Faction.OfPlayer);
        Check(name+"-pipe-riser-joins-decks",ReferenceEquals(PipeNetOf(lowerPipe),PipeNetOf(upperPipe)),
            $"lower={PipeNetOf(lowerPipe)}; upper={PipeNetOf(upperPipe)}; riserLower={PipeNetOf(riserLower)}; riserUpper={PipeNetOf(riserUpper)}");
        // A tank touching the riser's own deck must be visible from the other deck's network.
        var tankDef=DefDatabase<ThingDef>.GetNamed("VGE_GiantAstrofuelTank");
        const int tankX=80,tankZ=82;
        var tankAt=At(1,tankX,tankZ);
        var tankRect=GenAdj.OccupiedRect(tankAt,tankDef.defaultPlacingRot,tankDef.size);
        int logicalMinZ=tankZ-tankDef.size.z/2,logicalMaxZ=tankZ+tankDef.size.z/2;
        int logicalMaxX=tankX+tankDef.size.x/2;
        foreach(var c in tankRect.ExpandedBy(2))
            foreach(var t in c.GetThingList(map).ToList())
                if(t is not Building_GravEngine&&t.def.destroyable&&t!=lowerPipe&&t!=upperPipe&&
                   t!=riserLower&&t!=riserUpper)t.Destroy(DestroyMode.Vanish);
        var tank=(ThingWithComps)GenSpawn.Spawn(tankDef,tankAt,map,tankDef.defaultPlacingRot);
        tank.SetFaction(Faction.OfPlayer);
        // Bridge the tank to the riser column: a strip along the tank's top ring (z = minZ-1) up to
        // x=80, where the pipes and risers already sit, so the tank, the strip, both risers and both
        // decks all end up in one network.
        for(int x=79;x<=logicalMaxX;x++)
        {
            var c=new IntVec3(x,0,logicalMinZ-1);
            if(c.x<0||c.x>=owner.Layout!.Width||c.z<0||c.z>=owner.Layout.Height)continue;
            var cell=At(1,c.x,c.z);
            if(!cell.InBounds(map)||cell.GetFirstThing(map,pipeDef)!=null)continue;
            GenSpawn.Spawn(pipeDef,cell,map).SetFaction(Faction.OfPlayer);
        }
        var storage=tank.AllComps.FirstOrDefault(c=>c.GetType().Name=="CompResourceStorage");
        var sharedNet=PipeNetOf(upperPipe);
        if(storage==null)
            Check(name+"-fuel-storage-shared-across-decks",false,"tank has no CompResourceStorage");
        else
        {
            AccessTools.Method(storage.GetType(),"AddResource").Invoke(storage,new object[]{250f});
            if(sharedNet!=null)AccessTools.Method(sharedNet.GetType(),"PipeSystemTick").Invoke(sharedNet,null);
            var shared=sharedNet is null?0f:
                (float)AccessTools.Method(sharedNet.GetType(),"CurrentStored").Invoke(sharedNet,null)!;
            var own=(float)AccessTools.PropertyGetter(storage.GetType(),"AmountStored").Invoke(storage,null)!;
            var storageNet=PipeNetOf(tank);
            Check(name+"-fuel-storage-shared-across-decks",shared>=250f,
                $"shared={shared}; tankStored={own}; sameNet={ReferenceEquals(storageNet,sharedNet)}; storageNet={storageNet}; net={sharedNet}");
        }
        tank.Destroy(DestroyMode.Vanish);
        // Removing the upper riser must split the networks again, natively. The upper pipe itself is
        // expected to survive on its own net, so "no connector above this deck" is the wrong test: the
        // invariant is that the riser that bridged the decks is gone from the lower net and the two
        // pipes no longer share a network.
        var beforeSplit=PipeNetOf(lowerPipe);
        riserUpper.Destroy(DestroyMode.Vanish);
        var afterLower=PipeNetOf(lowerPipe);
        var afterUpper=PipeNetOf(upperPipe);
        var connectors=ConnectorsOn(afterLower);
        // PipeSystem never clears a destroyed comp's PipeNet, and PipeNet.Destroy only unregisters the
        // entries it still holds, so a destroyed riser can linger in a connectors list. The observable
        // split is therefore "the two decks are no longer one network", which sameNet already reports.
        var riserStillLinked=connectors.Any(c=>c is ThingComp tc&&tc.parent==riserUpper);
        var liveRiserLinked=connectors.Any(c=>c is ThingComp tc&&tc.parent==riserUpper&&!tc.parent.Destroyed);
        Check(name+"-pipe-riser-removal-splits-decks",
            afterLower!=null&&!liveRiserLinked&&!ReferenceEquals(afterLower,afterUpper),
            $"connectors={connectors.Count}; riserStillLinked={riserStillLinked} (destroyed={riserUpper.Destroyed}); "+
            $"liveRiserLinked={liveRiserLinked}; sameNet={ReferenceEquals(afterLower,afterUpper)}; before={beforeSplit!=null}");
        if(!lowerPipe.Destroyed)lowerPipe.Destroy(DestroyMode.Vanish);
        if(!upperPipe.Destroyed)upperPipe.Destroy(DestroyMode.Vanish);
        if(!riserLower.Destroyed)riserLower.Destroy(DestroyMode.Vanish);
    }
    /// <summary>Connectors listed on a PipeNet, reached through its public connectors list.</summary>
    private static List<object> ConnectorsOn(object? pipeNet)
    {
        var result=new List<object>();
        if(pipeNet==null)return result;
        if(AccessTools.Field(pipeNet.GetType(),"connectors")?.GetValue(pipeNet) is System.Collections.IEnumerable items)
            foreach(var item in items)if(item!=null)result.Add(item);
        return result;
    }
    private static object? PipeNetOf(Thing thing)    {
        var resourceType=AccessTools.TypeByName("PipeSystem.CompResource");
        if(resourceType==null)return null;
        // PipeNet is declared on CompResource, so read it through the declaring type rather than the
        // concrete comp class (which only inherits it).
        var getter=AccessTools.PropertyGetter(resourceType,"PipeNet");
        foreach(var comp in thing is ThingWithComps withComps?withComps.AllComps:Enumerable.Empty<ThingComp>())
            if(resourceType.IsInstanceOfType(comp))return getter.Invoke(comp,null);
        return null;
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
            if(engine.Destroyed||!engine.Spawned)
            {
                Check(name+"-engine-spawned",false,
                    $"spawn at {source} failed: destroyed={engine.Destroyed} spawned={engine.Spawned} "+
                    $"at={engine.Position} things={string.Join(";",source.GetThingList(map).Select(t=>t.def.defName+":"+t.ThingID))}");
                continue;
            }
            foreach(var c in rect)
            {
                map.roofGrid.SetRoof(At(1,c.x,c.z),RoofDefOf.RoofConstructed);
                ShipDeckTerrain.Install(owner,At(2,c.x,c.z),false);map.fogGrid.Unfog(At(2,c.x,c.z));
            }
            var consoleDef=DefDatabase<ThingDef>.GetNamed(name=="VGE_GravjumperEngine"?"VGE_PilotCockpit":name=="VGE_GravhulkEngine"?"VGE_PilotBridge":"PilotConsole");
            var console=(ThingWithComps)GenSpawn.Spawn(consoleDef,At(2,80,75),map);
            console.SetFaction(Faction.OfPlayer);
            // Cargo must not sit inside the pilot bridge footprint. VGE_PilotBridge is 5x3 centred at
            // (80,75), i.e. x78..82 z74..76, so (81,76) spawned the silver ON the bridge and displaced
            // it — which is why AddThing never saw the bridge and pilotConsole stayed null. (81,77) is
            // clear of the bridge, of the probe cell and of the four corner columns.
            var cargo=ThingMaker.MakeThing(ThingDefOf.Silver);cargo.stackCount=37;GenSpawn.Spawn(cargo,At(2,81,77),map);
            foreach(var c in new[]{new IntVec3(77,0,72),new IntVec3(83,0,78),new IntVec3(83,0,72),new IntVec3(77,0,78)})
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(1,c.x,c.z),map).SetFaction(Faction.OfPlayer);
            var roofCell=At(2,82,77);
            map.roofGrid.SetRoof(roofCell,DefDatabase<RoofDef>.GetNamed("VGE_VacBarrierRoof"));
            setColor.Invoke(null,new object[]{map,roofCell,Color.magenta});
            var scaffold=At(2,77,75);
            map.terrainGrid.SetFoundation(scaffold,DefDatabase<TerrainDef>.GetNamed("VGE_GravshipSubscaffold"));
            owner.Ships.MarkDirty();owner.Ships.Tick();
            Check(name+"-both-decks-owned",owner.Ships.Get(engine)?.Cells.Count==98,
                $"cells={owner.Ships.Get(engine)?.Cells.Count}; missing="+string.Join(";",rect
                    .Where(c=>map.terrainGrid.FoundationAt(At(2,c.x,c.z))?.IsSubstructure!=true)
                    .Select(c=>c.x+","+c.z)));
            Check(name+"-upper-console-linked",console.GetComp<CompPilotConsole>().engine==engine&&console.GetComp<CompPilotConsole>().CanBeActive,$"engine={console.GetComp<CompPilotConsole>().engine}; links={engine.AffectedByFacilities.LinkedFacilitiesListForReading.Count}");
            // Guard the guard: this fixture only proves anything if it actually sits in the regime that
            // failed live. Native CompGravshipFacility.CanBeActive measures storage coordinates
            // (z = layer*Stride + z) with IntVec3.InHorDistOf, so one deck of separation is exactly
            // Stride cells. On a 250 map Stride is 255 while VGE's pilot console allows only 200, so
            // native alone must report "not connected" and only RimLayers' logical-distance widening
            // can rescue it. A smaller map silently makes both assertions below pass for free.
            var consoleMaxDistance=console.GetComp<CompPilotConsole>().Props.maxDistance;
            Check(name+"-fixture-reproduces-live-deck-gap",!console.Position.InHorDistOf(engine.Position,consoleMaxDistance),
                $"storage dz={engine.Position.z-console.Position.z} maxDistance={consoleMaxDistance} stride={owner.Layout!.Stride} "+
                $"mapHeight={map.Size.z} (run ship-vge with -MapSize 250 to reproduce live play)");
            if(owner.Ships.Get(engine) is not {} shipNow)
            {
                var footprint=engine.OccupiedRect().Select(c=>
                {
                    var foundation=map.terrainGrid.FoundationAt(c);
                    var hasSurface=owner.TryDecode(c,out var l)&&owner.Structure.HasSurface(c);
                    return $"{c.x},{c.z}:sub={(foundation?.IsSubstructure==true)}:top={map.terrainGrid.TopTerrainAt(c)?.defName}:surface={hasSurface}:layer={l.LayerId}";
                });
                Check(name+"-ship-structure-present",false,
                    "engine footprint: "+string.Join("; ",footprint)+"; claimed="+string.Join(";",engine.OccupiedRect().Select(c=>owner.Ships.ClaimAt(c))));
                return;
            }
            Check(name+"-scaffold-not-charged",VanillaGravshipCompatibility.SupportCost(map,shipNow.Cells)==97);
            Check(name+"-launch-rules-accept-structure",owner.Ships.LaunchReport(engine).Accepted,owner.Ships.LaunchReport(engine).Reason);
            if(name=="GravEngine")CheckVgeFuel(engine);
            CheckVgeDecks(name,engine,console,rect);
            Check(name+"-engine-alive-after-decks",!engine.Destroyed&&engine.Spawned,
                $"destroyed={engine.Destroyed} spawned={engine.Spawned} at={engine.Position}");
            engine.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
            // Prepare() returning null silently disables the whole layered payload, so surface its reason
            // as its own assertion instead of letting a later null reference be the only symptom.
            if(engine.Destroyed||!engine.Spawned)
                Check(name+"-engine-alive-before-capture",false,
                    $"destroyed={engine.Destroyed} spawned={engine.Spawned} at={engine.Position} "+
                    $"map={(engine.Map==null?"<null>":engine.Map.uniqueID.ToString())} things="+
                    string.Join(";",source.GetThingList(map).Select(t=>t.def.defName+":"+t.ThingID)));
            else Check(name+"-engine-alive-before-capture",true,"");
            var prepared=ShipLayers.Prepare(engine,out var prepareReport);
            Check(name+"-layered-capture-prepared",prepared!=null,
                $"capture={(prepared==null?"<null>":"ok")}; accepted={prepareReport.Accepted}; reason={prepareReport.Reason}");
            var ship=GravshipUtility.GenerateGravship(engine);
            Current.Game.Gravship=ship;
            Check(name+"-capture-has-two-decks-and-upper-cargo",ShipLayers.TryData(ship,out var data)&&data.layerCount==2&&data.floors.Count==98&&ship.Things.Contains(cargo)&&!cargo.Spawned,
                $"hasData={ShipLayers.TryData(ship,out var d)} layers={d?.layerCount} floors={d?.floors.Count} "+
                $"cargoInShip={ship.Things.Contains(cargo)} cargoSpawned={cargo.Spawned} cargoDestroyed={cargo.Destroyed} cargoAt={cargo.Position}");
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
