using System.Text.Json;
using HarmonyLib;
using RimLayers.Layout;
using RimLayers.Ships;
using RimLayers.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void BeginConfirmedShipTakeoff()
    {
        var target=Find.WorldObjects.MapParentAt(shipTargetTile).Map;float fuel=shipTank.Fuel;
        var quota=LayerExpansionQuota.Current!;var settings=RimLayersMod.Settings;
        void ConfirmProbe(Action action)
        {
            var seen=Find.WindowStack.Windows.ToHashSet();
            SettlementProximityGoodwillUtility.CheckConfirmSettle(shipTargetTile,action,null,shipEngine);
            for(int i=0;i<4;i++)
            {
                var dialog=Find.WindowStack.Windows.OfType<Dialog_MessageBox>().FirstOrDefault(w=>!seen.Contains(w));
                if(dialog==null)break;seen.Add(dialog);
                try{dialog.buttonAAction();}finally{dialog.Close(false);}
            }
        }
        int oldGlobal=settings.GlobalLimit;bool oldSpace=settings.SpaceExpansion,deniedAction=false;
        try
        {
            settings.GlobalLimit=quota.Generated;
            ConfirmProbe(()=>{deniedAction=true;shipTank.ConsumeFuel(1);});
            Check("departure-quota-denial-precedes-native-fuel-and-detachment",!deniedAction&&shipTank.Fuel==fuel&&shipEngine.Spawned&&LayerMapComponent.For(target)==null&&quota.Reserved==0);
            settings.GlobalLimit=oldGlobal;
            if(shipTargetTile.Layer.Def.isSpace)
            {
                settings.SpaceExpansion=false;
                ConfirmProbe(()=>{deniedAction=true;shipTank.ConsumeFuel(1);});
                Check("departure-space-denial-precedes-native-fuel-and-detachment",!deniedAction&&shipTank.Fuel==fuel&&shipEngine.Spawned&&quota.Reserved==0);
            }
        }
        finally{settings.GlobalLimit=oldGlobal;settings.SpaceExpansion=oldSpace;}
        bool fault=false;
        try{ConfirmProbe(()=>throw new InvalidOperationException("settings-departure-test"));}
        catch(InvalidOperationException ex)when(ex.Message=="settings-departure-test"){fault=true;}
        Check("failed-confirmed-native-action-releases-unused-reservation",fault&&quota.Reserved==0&&shipEngine.Spawned&&shipTank.Fuel==fuel&&LayerMapComponent.For(target)==null);
        Check("destination-validation-is-read-only-before-confirmation",LayerShipDestination.Report(shipEngine,shipTargetTile).Accepted&&LayerMapComponent.For(target)==null&&shipTank.Fuel==fuel);
        var windows=Find.WindowStack.Windows.ToHashSet();bool ran=false,afterStartFault=false;
        void CaptureAfterStartFault(Action action)
        {try{action();}catch(InvalidOperationException ex)when(ex.Message=="settings-after-takeoff-test"){afterStartFault=true;}}
        CaptureAfterStartFault(()=>SettlementProximityGoodwillUtility.CheckConfirmSettle(shipTargetTile,()=>
        {
            ran=true;
            Check("confirmed-departure-reserves-before-native-fuel-without-allocation",LayerMapComponent.For(target)==null&&shipEngine.Spawned&&shipTank.Fuel==fuel&&LayerExpansionQuota.Current!.Reservations.Any(r=>r.EngineId==shipEngine.thingIDNumber&&r.Destination==shipTargetTile&&r.Top==ShipDecks-1)&&LayerExpansionQuota.Current.Reserved==ShipDecks-1);
            Find.GravshipController.InitiateTakeoff(shipEngine,shipTargetTile);
            throw new InvalidOperationException("settings-after-takeoff-test");
        },null,shipEngine));
        var confirmation=Find.WindowStack.Windows.OfType<Dialog_MessageBox>().FirstOrDefault(w=>!windows.Contains(w));
        if(confirmation!=null)
        {
            Check("pending-confirmation-does-not-expand-or-consume-fuel",!ran&&LayerMapComponent.For(target)==null&&shipTank.Fuel==fuel&&quota.Reserved==0);
            CaptureAfterStartFault(confirmation.buttonAAction);confirmation.Close(false);
        }
        Check("confirmed-native-action-runs-once-after-preparation",ran);
        Check("exception-after-takeoff-keeps-inflight-commitment",afterStartFault&&WorldComponent_GravshipController.CutsceneInProgress&&quota.Reservations.Any(r=>r.EngineId==shipEngine.thingIDNumber));
    }
    private static void LandExistingShip()
    {
        if(Find.CurrentMap!=shipDestination||Find.DesignatorManager.SelectedDesignator is not Designator_MoveGravship designator)return;
        var marker=(GravshipLandingMarker)AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").GetValue(Find.GravshipController)!;
        map=shipDestination;owner=LayerMapComponent.For(map)!;shipLandingRoot=new IntVec3(65,0,70);
        foreach(var c in new CellRect(45,45,45,45))
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn and not GravshipLandingMarker){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,SpaceFixture?DefDatabase<TerrainDef>.GetNamed("Space"):TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(45,45,45,45).Contains(c));
        if(SpaceFixture)
        {
            // Native existing-map landings must touch land/platform; empty orbital space is not a legal location.
            foreach(var c in new CellRect(64,69,3,3))
            {map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);}
        }
        // Ground Pawns are native landing obstacles too; move fixture wildlife out of the prepared pad.
        foreach(var visitor in map.mapPawns.AllPawnsSpawned.Where(p=>new CellRect(45,45,45,45).Contains(p.Position)).ToList())
        {var safe=GenRadial.RadialCellsAround(new IntVec3(23,0,23),6,true).First(c=>c.Standable(map)&&c.GetFirstPawn(map)==null);visitor.Position=safe;visitor.Notify_Teleported();}
        marker.GravshipRotation=Rot4.East;
        var zero=(IntVec3)AccessTools.Method(typeof(Designator_MoveGravship),"GetSizeRotAdjustedCell").Invoke(designator,new object[]{IntVec3.Zero})!;
        var choose=shipLandingRoot-PrefabUtility.GetRoot(zero,layerShip.Bounds.Size,marker.GravshipRotation);
        var report=designator.CanDesignateCell(choose);
        Check("existing-map-native-designator-accepts-first-landing",report.Accepted,report.Reason);
        if(!report.Accepted)throw new InvalidOperationException("Fixture must obey native landing rules: "+report.Reason);
        designator.DesignateSingleCell(choose);
        Check("existing-map-native-designator-commits-marker-and-rotation",marker.Spawned&&marker.Map==map&&marker.Position==shipLandingRoot&&marker.GravshipRotation==Rot4.East);
        ShipLayers.TryData(layerShip,out var payload);var local=PrefabUtility.GetAdjustedLocalPosition(payload.floors.First().local,Rot4.East);
        var blocked=owner.ToStorage(new LayerCell(local.y,shipLandingRoot.x+local.x,shipLandingRoot.z+local.z));
        var blocker=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel),blocked,map);
        Check("existing-map-upper-collision-rejected-before-cargo-spawn",!ShipLanding.Check(layerShip,shipLandingRoot,map).Accepted&&!shipPawn.Spawned);
        marker.BeginLanding(Find.GravshipController);
        Check("existing-map-blocked-confirm-preserves-marker",marker.Spawned&&!WorldComponent_GravshipController.CutsceneInProgress);
        blocker.Destroy(DestroyMode.Vanish);
        var geology=owner.Mountains.Snapshot();var blockedGeology=geology.Copy();blockedGeology.Floors.Add(blocked);owner.Mountains.Restore(blockedGeology);
        Check("natural-mountain-floor-rejects-landing-even-with-no-rock-or-roof",!ShipLanding.Check(layerShip,shipLandingRoot,map).Accepted&&!shipPawn.Spawned);
        owner.Mountains.Restore(geology);
        marker.BeginLanding(Find.GravshipController);
        AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").SetValue(Find.GravshipController,null);
        shipPhase=2;
    }
    private static void ValidateShipCabin()
    {
        var offset=PrefabUtility.GetAdjustedLocalPosition(new IntVec3(6,1,-2),Rot4.East);
        var cabin=At(1,shipLandingRoot.x+offset.x,shipLandingRoot.z+offset.z).GetRoom(map);
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"ship-cabin-baseline.json")));var b=doc.RootElement;
        Check("sealed-upper-cabin-keeps-native-temperature-and-vacuum",cabin!=null&&!cabin.UsesOutdoorTemperature&&!cabin.ExposedToSpace&&
            Math.Abs(cabin.Temperature-b.GetProperty("temperature").GetSingle())<.01f&&Math.Abs(cabin.Vacuum-b.GetProperty("vacuum").GetSingle())<.001f,
            $"temperature={cabin?.Temperature},vacuum={cabin?.Vacuum},expected={b}");
    }
    private static void PrepareExistingShipDestination()
    {
        if(SpaceFixture)shipTargetTile=Find.WorldGrid.Orbit.Tiles.First(t=>Find.WorldObjects.MapParentAt(t.tile)==null).tile;
        var target=MakeUnmanagedMap(shipTargetTile,145,157);
        Check("ship-target-starts-as-existing-unmanaged-map",LayerMapComponent.For(target)==null&&target.Size==new IntVec3(145,1,157));
        // Prepare an actual ground landing pad BEFORE geology is captured by expansion.
        // Production must reject mountains above a cleared ground footprint, not erase them.
        foreach(var c in new CellRect(45,45,45,45))
        {
            foreach(var t in c.GetThingList(target).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            target.roofGrid.SetRoof(c,null);
            target.terrainGrid.SetTerrain(c,SpaceFixture?DefDatabase<TerrainDef>.GetNamed("Space"):TerrainDefOf.Concrete);
        }
        target.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(45,45,45,45).Contains(c));
        var cell=new IntVec3(20,0,20);
        foreach(var c in new CellRect(18,18,12,12))
        {
            foreach(var t in c.GetThingList(target).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            target.roofGrid.SetRoof(c,null);
            if(SpaceFixture)target.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);
            target.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);target.fogGrid.Unfog(c);
        }
        var item=ThingMaker.MakeThing(ThingDefOf.Silver);item.stackCount=43;GenSpawn.Spawn(item,cell,target);
        // An existing owned destination has a real resident. An empty enemy settlement can be
        // defeated by the native world tick while the ship is still in flight.
        target.Parent.SetFaction(Faction.OfPlayer);
        var resident=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        if(SpaceFixture)WearVacuumGear(resident);
        GenSpawn.Spawn(resident,new IntVec3(23,0,23),target);resident.drafter.Drafted=true;
        target.areaManager.Home[cell]=true;target.deepResourceGrid.SetAt(cell,ThingDefOf.Steel,321);
        File.WriteAllText(Path.Combine(directory,"ship-existing-destination.json"),JsonSerializer.Serialize(new{map=target.uniqueID,item=item.ThingID,space=SpaceFixture}));
        Current.Game.CurrentMap=shipSource;
    }
    private static void ValidateExistingShipDestination()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"ship-existing-destination.json")));var b=doc.RootElement;
        var target=Find.Maps.Single(m=>m.uniqueID==b.GetProperty("map").GetInt32());
        Check("existing-destination-keeps-map-original-item-area-resource-and-layer-layout",target==shipDestination&&
            target.listerThings.AllThings.Any(t=>t.ThingID==b.GetProperty("item").GetString()&&t.stackCount==43&&t.Position==new IntVec3(20,0,20))&&
            target.areaManager.Home[new IntVec3(20,0,20)]&&target.deepResourceGrid.CountAt(new IntVec3(20,0,20))==321&&
            LayerMapComponent.For(target)?.Layout is {Width:145,Height:157,Stride:170} layout&&layout.LayerCount==ShipDecks&&target.Tile.Layer.Def.isSpace==b.GetProperty("space").GetBoolean());
    }
}
