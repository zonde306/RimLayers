using System.Text.Json;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimLayers.Ships;
using RimWorld;
using UnityEngine;
using Verse;
namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void ConfigureShipCoexistence(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(UI),nameof(UI.MouseMapPosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(HeightLimitMouse)));
        h.Patch(AccessTools.PropertyGetter(typeof(UI),nameof(UI.MousePositionOnUI)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(HeightLimitMouseOnUI)));
        foreach(var name in new[]{"TransferZones","CopyCellContents","CopyAreas","CopyStorageGroups","UpdateBoundingBoxes","DetermineLaunchDirection","CheckAffectedGoodwill"})
            h.Patch(AccessTools.Method(typeof(Gravship),name),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CoexistBefore)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(CoexistAfter)));
    }
    private static void CoexistBefore(Gravship __instance,MethodBase __originalMethod)=>Log.Message("[RimLayers.NativeTrace] begin "+__instance.Engine.ThingID+" "+__originalMethod.Name);
    private static void CoexistAfter(Gravship __instance,MethodBase __originalMethod)=>Log.Message("[RimLayers.NativeTrace] end "+__instance.Engine.ThingID+" "+__originalMethod.Name);
    private static readonly CellRect coexistDeck=new(76,72,9,7);
    private static Building_GravEngine coexistCore=null!,coexistOther=null!;
    private static Thing coexistCargo=null!,coexistHeld=null!;
    private static IntVec3 coexistLanding;
    private static float coexistLandingStarted;
    private static void CoexistPlatform(CellRect rect)
    {
        foreach(var c in rect)
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.roofGrid.SetRoof(c,null);
        }
        for(int x=rect.minX;x<=rect.maxX;x+=5)for(int z=rect.minZ;z<=rect.maxZ;z+=5)
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),new IntVec3(x,0,z),map);
        foreach(var c in rect)
        {
            map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
            var upper=At(1,c.x,c.z);map.terrainGrid.SetTerrain(upper,RimLayersDefOf.RL_StructuralFloor);map.fogGrid.Unfog(upper);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(rect.Contains);
    }
    private static void BeginShipCoexistence()
    {
        CheckShipLandingHeightLimits();
        pendingScreenshot=Path.Combine(directory,"landing-height-limit.png");screenshotHold=3;stage=382;ticks=0;
    }
    private static void ContinueShipCoexistence()
    {
        FinishShipLandingHeightLimits();
        CheckHollowShipSupport();
        CoexistPlatform(new CellRect(73,69,24,13));
        var baseCell=At(1,76,72);var deck=ShipDeckTerrain.Def;
        Find.ResearchManager.gravEngineInspected=true;
        Check("ordinary-upper-platform-allows-native-base-without-engine",
            GenConstruct.CanPlaceBlueprintAt(TerrainDefOf.Substructure,baseCell,Rot4.North,map).Accepted);
        Check("upper-ship-deck-still-requires-real-engine",!owner.Structure.Validate(deck,At(2,76,72),Rot4.North,true).Accepted);
        var frame=(Frame)ThingMaker.MakeThing(TerrainDefOf.Substructure.frameDef);frame.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(frame,baseCell,map);
        foreach(var cost in TerrainDefOf.Substructure.CostListAdjusted(null))
        {var item=ThingMaker.MakeThing(cost.thingDef);item.stackCount=cost.count;frame.resourceContainer.TryAdd(item);}
        frame.CompleteConstruction(pawn);
        Check("native-frame-builds-elevated-base-and-keeps-hidden-colony-floor",frame.Destroyed&&map.terrainGrid.FoundationAt(baseCell)==TerrainDefOf.Substructure&&owner.Underlays.Has(baseCell));
        map.terrainGrid.RemoveFoundation(baseCell,true);
        Check("manual-native-base-removal-restores-ordinary-floor",map.terrainGrid.FoundationAt(baseCell)==null&&map.terrainGrid.TopTerrainAt(baseCell)==RimLayersDefOf.RL_StructuralFloor&&owner.Structure.HasSurface(baseCell)&&!owner.Underlays.Has(baseCell));
        foreach(var t in baseCell.GetThingList(map).ToList())if(t.def.category==ThingCategory.Item)t.Destroy(DestroyMode.Vanish);
        var hostZone=new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager){label="Colony landing pad"};map.zoneManager.RegisterZone(hostZone);
        foreach(var c in coexistDeck){var at=At(1,c.x,c.z);map.terrainGrid.SetTerrain(at,DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));map.areaManager.Home[at]=true;hostZone.AddCell(at);}
        map.designationManager.AddDesignation(new Designation(At(1,83,78),DesignationDefOf.RemoveFloor));
        foreach(var c in coexistDeck)map.terrainGrid.SetFoundation(At(1,c.x,c.z),TerrainDefOf.Substructure);
        coexistCore=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,At(1,79,74),map);coexistCore.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(ThingDefOf.PilotConsole,At(1,82,74),map).SetFaction(Faction.OfPlayer);
        owner.Ships.Tick();
        Check("elevated-native-base-keeps-native-identity",coexistDeck.All(c=>map.terrainGrid.FoundationAt(At(1,c.x,c.z))==TerrainDefOf.Substructure));
        Check("base-payload-does-not-own-hidden-host-zone",hostZone.Cells.Count==0&&map.zoneManager.AllZones.Contains(hostZone));
        // Simulate a second ship docking with a pre-existing stable identity, touching the first.
        coexistOther=(Building_GravEngine)ThingMaker.MakeThing(ThingDefOf.GravEngine);
        var second=new CellRect(85,72,9,7);
        foreach(var c in second){var at=At(1,c.x,c.z);owner.Ships.Assign(at,coexistOther.thingIDNumber);map.terrainGrid.SetFoundation(at,TerrainDefOf.Substructure);}
        GenSpawn.Spawn(coexistOther,At(1,89,74),map);coexistOther.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(ThingDefOf.PilotConsole,At(1,92,74),map).SetFaction(Faction.OfPlayer);
        owner.Ships.Tick();
        Check("touching-real-native-ships-have-separate-owners",owner.Ships.Get(coexistCore) is {Ambiguous:false} first&&first.Cells.Count==63&&owner.Ships.Get(coexistOther) is {Ambiguous:false} other&&other.Cells.Count==63&&!first.Cells.Overlaps(other.Cells));
        var oldOther=coexistOther;var otherPosition=oldOther.Position;
        oldOther.Destroy(DestroyMode.Vanish);
        coexistOther=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,otherPosition,map);coexistOther.SetFaction(Faction.OfPlayer);
        owner.Ships.MarkDirty();owner.Ships.Tick();
        Check("replacement-core-inherits-only-its-orphaned-hull",owner.Ships.OwnerAt(otherPosition)==coexistOther&&
            owner.Ships.Get(coexistOther) is {Ambiguous:false} rebuilt&&rebuilt.Cells.Count==63&&owner.Ships.Get(coexistCore)?.Cells.Count==63);
        coexistCargo=ThingMaker.MakeThing(ThingDefOf.Silver);coexistCargo.stackCount=37;GenSpawn.Spawn(coexistCargo,At(1,83,78),map);
        var split=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TableButcher"),ThingDefOf.WoodLog),At(1,75,75),map);
        var casket=(Building_CryptosleepCasket)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("CryptosleepCasket"),At(1,75,77),map,Rot4.East);
        coexistHeld=ThingMaker.MakeThing(ThingDefOf.Gold);coexistHeld.stackCount=11;
        casket.GetDirectlyHeldThings().TryAdd(coexistHeld);
        var mixedFrame=(Frame)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TableButcher").frameDef,ThingDefOf.WoodLog);
        mixedFrame.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(mixedFrame,At(1,75,73),map);
        var supplied=ThingMaker.MakeThing(ThingDefOf.WoodLog);supplied.stackCount=9;mixedFrame.resourceContainer.TryAdd(supplied);
        var capture=ShipLayers.Prepare(coexistCore,out var report);
        Check("cross-boundary-buildings-and-frame-are-read-only-demolition-list",report.Accepted&&capture!=null&&capture.Straddlers.SetEquals(new Thing[]{split,casket,mixedFrame})&&split.Spawned&&casket.Spawned&&mixedFrame.Spawned,report.Reason);
        coexistCore.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
        var ship=GravshipUtility.GenerateGravship(coexistCore);
        Check("actual-capture-destroys-cross-boundary-buildings-once",split.Destroyed&&casket.Destroyed&&mixedFrame.Destroyed&&!coexistCore.Spawned);
        Check("actual-container-and-frame-materials-remain-original-objects",coexistHeld.Spawned&&!coexistHeld.Destroyed&&coexistHeld.stackCount==11&&supplied.Spawned&&!supplied.Destroyed&&supplied.stackCount==9&&!ship.Things.Contains(coexistHeld)&&!ship.Things.Contains(supplied));
        Check("takeoff-restores-original-pad-decoration-home-and-native-zone",coexistDeck.All(c=>map.terrainGrid.TopTerrainAt(At(1,c.x,c.z)).defName=="WoodPlankFloor"&&owner.Structure.HasSurface(At(1,c.x,c.z))&&map.areaManager.Home[At(1,c.x,c.z)])&&hostZone.Cells.Count==63&&map.designationManager.DesignationAt(At(1,83,78),DesignationDefOf.RemoveFloor)!=null);
        Check("other-ship-stays-intact-after-neighbor-launch",coexistOther.Spawned&&owner.Ships.Get(coexistOther) is {Ambiguous:false} kept&&kept.Cells.Count==63&&second.All(c=>map.terrainGrid.FoundationAt(At(1,c.x,c.z))==TerrainDefOf.Substructure));
        CoexistPlatform(new CellRect(24,21,18,18));
        foreach(var c in new CellRect(24,21,18,18))map.terrainGrid.SetTerrain(At(1,c.x,c.z),DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
        coexistLanding=At(1,31,30);ship.Rotation=Rot4.East;
        Check("rotated-elevated-native-base-landing-is-accepted",ShipLanding.Check(ship,coexistLanding,map).Accepted);
        Check("single-deck-ship-still-fits-third-floor-height",ShipLanding.CheckHeight(ship,2).Accepted);
        var block=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(2,31,30),map);
        Check("elevated-landing-checks-vertical-clearance",!ShipLanding.Check(ship,coexistLanding,map).Accepted);block.Destroy(DestroyMode.Vanish);
        var oldViewed=owner.ViewedLayer;var count=owner.Layout!.LayerCount;ShipLanding.Check(ship,coexistLanding,map);
        Check("landing-preview-does-not-mutate-layout-or-underlays",owner.Layout.LayerCount==count&&!owner.Underlays.Has(coexistLanding)&&owner.ViewedLayer==oldViewed);
        if(NativeShip)
        {
            ship.Tile=map.Tile;ship.destinationTile=map.Tile;Find.WorldObjects.Add(ship);GravshipUtility.ArriveExistingMap(ship);
            var marker=(GravshipLandingMarker)AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").GetValue(Find.GravshipController)!;
            marker.GravshipRotation=Rot4.East;marker.CacheCells();
            LayerView.Switch(owner,1);
            var designator=new Designator_MoveGravship(map,marker);Find.DesignatorManager.Select(designator);
            var allowed=designator.CanDesignateCell(coexistLanding);
            Check("native-designator-accepts-real-second-floor-location",allowed.Accepted,allowed.Reason);
            designator.DesignateSingleCell(coexistLanding);coexistLanding=marker.Position;
            Check("native-designator-commits-selected-floor-and-rotation",owner.TryDecode(marker.Position,out var chosen)&&chosen.LayerId==1&&marker.GravshipRotation==Rot4.East);
            marker.BeginLanding(Find.GravshipController);
            AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").SetValue(Find.GravshipController,null);
            Check("native-high-floor-confirm-starts-landing-cutscene",WorldComponent_GravshipController.CutsceneInProgress);
            coexistLandingStarted=Time.realtimeSinceStartup;stage=381;ticks=0;return;
        }
        GravshipPlacementUtility.PlaceGravshipInMap(ship,coexistLanding,map,out _);Current.Game.Gravship=null;
        CompleteShipCoexistLanding();
    }
    private static void ShipCoexistLandingStep()
    {
        ticks++;
        if(coexistCore.Spawned&&!WorldComponent_GravshipController.CutsceneInProgress){CompleteShipCoexistLanding();return;}
        if(Time.realtimeSinceStartup-coexistLandingStarted>90f)throw new InvalidOperationException("Native elevated landing did not complete.");
    }
    private static void CompleteShipCoexistLanding()
    {
        Check("native-rotated-landing-keeps-core-on-second-floor",coexistCore.Spawned&&owner.TryDecode(coexistCore.Position,out var here)&&here.LayerId==1&&coexistCore.Position==coexistLanding);
        Check("landing-keeps-native-base-and-original-cargo-identity",coexistCargo.Spawned&&map.terrainGrid.FoundationAt(coexistCargo.Position)==TerrainDefOf.Substructure&&owner.Underlays.Has(coexistCargo.Position)&&owner.Ships.OwnerAt(coexistCargo.Position)==coexistCore);
        File.WriteAllText(Path.Combine(directory,"coexistence-baseline.json"),JsonSerializer.Serialize(new{core=coexistCore.ThingID,other=coexistOther.ThingID,cargo=coexistCargo.ThingID,held=coexistHeld.ThingID,landed=new{x=coexistCargo.Position.x,z=coexistCargo.Position.z}}));
        GameDataSaveLoader.SaveGame("RimLayers_V1Coexistence");
        LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(coexistCore.Position.ToVector3Shifted(),18);
        pendingScreenshot=Path.Combine(directory,"elevated-coexistence.png");screenshotHold=3;stage=380;ticks=0;
    }
    private static void CheckHollowShipSupport()
    {
        var rect=new CellRect(8,90,5,6);
        foreach(var c in rect)
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
            if(map.terrainGrid.UnderTerrainAt(c)!=null)map.terrainGrid.RemoveTopLayer(c,false);
            map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
        }
        foreach(int layer in new[]{0,1,2})
        {
            foreach(var c in rect)
            {
                if(layer==1&&(c.x==8||c.x==12)||layer==2)ShipDeckTerrain.Install(owner,At(layer,c.x,c.z),false);
                map.roofGrid.SetRoof(At(layer,c.x,c.z),RoofDefOf.RoofConstructed);map.fogGrid.Unfog(At(layer,c.x,c.z));
            }
            foreach(var c in new[]{new IntVec3(8,0,90),new IntVec3(12,0,90),new IntVec3(8,0,95),new IntVec3(12,0,95)})
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,c.x,c.z),map).SetFaction(Faction.OfPlayer);
        }
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),new IntVec3(6,0,92),map).SetFaction(Faction.OfPlayer);
        map.roofGrid.SetRoof(new IntVec3(7,0,92),RoofDefOf.RoofConstructed);
        ShipDeckTerrain.Install(owner,At(1,7,92),false);
        var core=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(10,0,92),map);core.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(ThingDefOf.PilotConsole,new IntVec3(10,0,94),map).SetFaction(Faction.OfPlayer);
        owner.Ships.MarkDirty();owner.Ships.Tick();
        var c1=ShipLayers.Prepare(core,out var report);
        Check("native-roof-supported-hollow-ship-connects-through-empty-middle",report.Accepted&&c1!=null&&c1.Cells.Count==73&&!c1.Cells.Contains(At(1,10,92))&&c1.HollowRoofs.Contains(At(1,10,92)),"reason="+report.Reason+",cells="+c1?.Cells.Count+",hollow="+c1?.HollowRoofs.Count);
        core.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
        var ship=GravshipUtility.GenerateGravship(core);
        Check("hollow-support-roof-is-payload-not-extra-floor",ShipLayers.TryData(ship,out var data)&&data.floors.Count==73&&data.supportRoofs.Count==19&&data.supportRoofs.Any(p=>p.local==new IntVec3(0,1,0))&&map.roofGrid.RoofAt(At(1,10,92))==null);
        Check("hollow-decks-count-full-height-span-not-only-occupied-levels",!ShipLanding.CheckHeight(ship,1).Accepted&&data.layerCount==3);
        GravshipPlacementUtility.PlaceGravshipInMap(ship,new IntVec3(10,0,92),map,out _);Current.Game.Gravship=null;
        Check("hollow-roofs-restore-before-upper-deck-support",map.roofGrid.RoofAt(At(1,10,92))==RoofDefOf.RoofConstructed&&owner.Structure.HasSurface(At(2,10,92))&&!owner.StructuralCells.Contains(At(1,10,92)));
        core.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
        ship=GravshipUtility.GenerateGravship(core);
        Check("second-departure-restores-borrowed-colony-roof-and-removes-only-new-hollow-roof",
            map.roofGrid.RoofAt(new IntVec3(7,0,92))==RoofDefOf.RoofConstructed&&map.roofGrid.RoofAt(At(1,10,92))==null,
            "colonyRoof="+map.roofGrid.RoofAt(new IntVec3(7,0,92))?.defName+",hollowRoof="+map.roofGrid.RoofAt(At(1,10,92))?.defName);
        GravshipPlacementUtility.PlaceGravshipInMap(ship,new IntVec3(10,0,92),map,out _);Current.Game.Gravship=null;

    }

    private static void RestoreShipCoexistence()
    {
        using var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"coexistence-baseline.json")));var data=saved.RootElement;
        coexistCore=map.listerThings.AllThings.OfType<Building_GravEngine>().Single(t=>t.ThingID==data.GetProperty("core").GetString());
        coexistOther=map.listerThings.AllThings.OfType<Building_GravEngine>().Single(t=>t.ThingID==data.GetProperty("other").GetString());
        coexistCargo=map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("cargo").GetString());
        var cargoCell=coexistCargo.Position;
        Check("reload-keeps-independent-ship-identity-and-hidden-pad",owner.Ships.OwnerAt(coexistCore.Position)==coexistCore&&owner.Ships.OwnerAt(coexistOther.Position)==coexistOther&&owner.Underlays.Has(cargoCell));
        Check("reload-keeps-demolition-container-object-once",map.listerThings.AllThings.Count(t=>t.ThingID==data.GetProperty("held").GetString())==1);
        coexistCore.launchInfo=new LaunchInfo{pilot=map.mapPawns.FreeColonistsSpawned.First(),quality=1,doNegativeOutcome=false};
        var ship=GravshipUtility.GenerateGravship(coexistCore);
        Check("reloaded-pad-is-restored-on-next-departure",!owner.Underlays.Has(cargoCell)&&map.terrainGrid.FoundationAt(cargoCell)==null&&map.terrainGrid.TopTerrainAt(cargoCell).defName=="WoodPlankFloor"&&owner.Structure.HasSurface(cargoCell));
        Check("reloaded-neighbor-is-not-stolen",coexistOther.Spawned&&!ship.Things.Contains(coexistOther)&&owner.Ships.OwnerAt(coexistOther.Position)==coexistOther);
        Check("reloaded-cargo-is-the-one-native-payload-object",ship.Things.Count(t=>t==coexistCargo)==1&&!coexistCargo.Spawned);
        Current.Game.Gravship=null;LayerView.Switch(owner,1);
        pendingScreenshot=Path.Combine(directory,"restored-colony-pad.png");screenshotHold=3;stage=380;ticks=0;
    }
}

