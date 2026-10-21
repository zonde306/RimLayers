using HarmonyLib;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimLayers.Ships;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Gravship heightLimitShip=null!;
    private static GravshipLandingMarker heightLimitMarker=null!;
    private static readonly IntVec3 heightLimitSource=new(50,0,92);

    private static bool HeightLimitMouse(ref Vector3 __result)
    {if(stage!=382)return true;__result=heightLimitMarker.Position.ToVector3Shifted();return false;}
    private static bool HeightLimitMouseOnUI(ref Vector2 __result)
    {if(stage!=382)return true;__result=new Vector2(UI.screenWidth*.5f,UI.screenHeight*.5f);return false;}

    private static void CheckShipLandingHeightLimits()
    {
        var rect=new CellRect(47,90,9,7);
        foreach(var c in rect)
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
            if(map.terrainGrid.UnderTerrainAt(c)!=null)map.terrainGrid.RemoveTopLayer(c,false);
            map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);
        }
        foreach(var c in new[]{new IntVec3(47,0,90),new IntVec3(55,0,90),new IntVec3(47,0,96),new IntVec3(55,0,96)})
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),c,map).SetFaction(Faction.OfPlayer);
        foreach(var c in rect)
        {
            map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
            ShipDeckTerrain.Install(owner,At(1,c.x,c.z),false);map.fogGrid.Unfog(At(1,c.x,c.z));
        }
        var core=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,heightLimitSource,map);core.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(ThingDefOf.PilotConsole,new IntVec3(53,0,92),map).SetFaction(Faction.OfPlayer);
        var cargo=ThingMaker.MakeThing(ThingDefOf.Silver);cargo.stackCount=51;GenSpawn.Spawn(cargo,At(1,51,94),map);
        owner.Ships.MarkDirty();owner.Ships.Tick();
        core.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
        heightLimitShip=GravshipUtility.GenerateGravship(core);
        Check("height-limit-fixture-is-real-two-deck-native-payload",ShipLayers.TryData(heightLimitShip,out var data)&&data.layerCount==2&&data.floors.Count==126&&heightLimitShip.Things.Contains(cargo));
        CoexistPlatform(new CellRect(24,21,18,18));
        CoexistPlatform(new CellRect(24,44,18,18));
        foreach(var c in new CellRect(24,44,18,18))
        {
            map.roofGrid.SetRoof(At(1,c.x,c.z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(2,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);map.fogGrid.Unfog(At(2,c.x,c.z));
        }
        for(int x=24;x<=41;x+=5)for(int z=44;z<=61;z+=5)
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(1,x,z),map);
        var lower=At(1,31,30);var upper=At(2,31,51);
        Check("two-deck-ship-fits-second-floor-through-third",ShipLanding.Check(heightLimitShip,lower,map).Accepted);
        var expected="RL_ShipLandingLayerOverflow".Translate(3,4,3).ToString();
        var overflow=ShipLanding.Check(heightLimitShip,upper,map);
        Check("two-deck-third-floor-reports-3-through-4-with-limit-3",!overflow.Accepted&&overflow.Reason==expected,overflow.Reason);
        // Use the real native arrival/designator and a stale marker to exercise all three entry points.
        heightLimitShip.Tile=map.Tile;heightLimitShip.destinationTile=map.Tile;Find.WorldObjects.Add(heightLimitShip);
        GravshipUtility.ArriveExistingMap(heightLimitShip);
        heightLimitMarker=(GravshipLandingMarker)AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").GetValue(Find.GravshipController)!;
        GenSpawn.Spawn(heightLimitMarker,upper,map);heightLimitMarker.CacheCells();
        var designator=new Designator_MoveGravship(map,heightLimitMarker);Find.DesignatorManager.Select(designator);
        var size=map.Size;var layout=owner.Layout;int viewed=owner.ViewedLayer;
        var cells=map.AllCells.ToDictionary(c=>c,c=>(map.terrainGrid.TopTerrainAt(c),map.terrainGrid.FoundationAt(c),map.roofGrid.RoofAt(c)));
        var things=map.listerThings.AllThings.ToHashSet();var floors=data.floors.ToArray();var payload=heightLimitShip.Things.ToHashSet();
        var preview=designator.CanDesignateCell(upper);
        Check("native-preview-rejects-height-with-specific-range",!preview.Accepted&&preview.Reason==expected,preview.Reason);
        Check("native-preview-keeps-legal-second-floor-available",designator.CanDesignateCell(lower).Accepted);
        var valid=new List<IntVec3>();var invalid=new List<IntVec3>();
        var validate=AccessTools.Method(typeof(Designator_MoveGravship),"ValidGravshipLocation");
        var colored=(AcceptanceReport)validate.Invoke(designator,new object[]{upper,valid,invalid})!;
        Check("overflow-preview-colors-whole-bottom-footprint-invalid",!colored.Accepted&&valid.Count==0&&invalid.Count==heightLimitMarker.GravshipCells.Count(),$"valid={valid.Count},invalid={invalid.Count}");
        valid.Clear();invalid.Clear();colored=(AcceptanceReport)validate.Invoke(designator,new object[]{lower,valid,invalid})!;
        Check("legal-height-keeps-native-valid-preview-cells",colored.Accepted&&valid.Count==heightLimitMarker.GravshipCells.Count()&&invalid.Count==0);
        var previous=ShipConfirmationState.Active;var confirmation=new ShipConfirmationState();
        try
        {
            ShipConfirmationState.Active=confirmation;heightLimitMarker.BeginLanding(Find.GravshipController);
        }
        finally{ShipConfirmationState.Active=previous;}
        Check("overflow-confirm-keeps-marker-and-does-not-start-cutscene",heightLimitMarker.Spawned&&!heightLimitMarker.Destroyed&&
            confirmation.Rejected==heightLimitMarker&&!WorldComponent_GravshipController.CutsceneInProgress&&
            AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").GetValue(Find.GravshipController)==heightLimitMarker);
        bool rejected=false;
        try{GravshipPlacementUtility.PlaceGravshipInMap(heightLimitShip,upper,map,out _);}
        catch(InvalidOperationException e){rejected=e.Message.Contains(expected);}
        Check("final-placement-rejects-height-before-any-spawn",rejected&&!core.Spawned&&!cargo.Spawned);
        Check("all-height-rejections-leave-map-layout-surfaces-and-things-unchanged",map.Size==size&&owner.Layout==layout&&owner.ViewedLayer==viewed&&
            things.SetEquals(map.listerThings.AllThings)&&cells.All(p=>p.Value==(map.terrainGrid.TopTerrainAt(p.Key),map.terrainGrid.FoundationAt(p.Key),map.roofGrid.RoofAt(p.Key)))&&
            !owner.Underlays.Has(upper));
        Check("overflow-neither-truncates-nor-moves-payload",data.layerCount==2&&data.floors.SequenceEqual(floors)&&payload.SetEquals(heightLimitShip.Things)&&
            !cargo.Destroyed&&cargo.stackCount==51&&heightLimitMarker.Position==upper);
        File.WriteAllText(Path.Combine(directory,"landing-height-message.txt"),expected);
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(upper.ToVector3Shifted(),18);
        // Floor switching ends active designators; select again to capture the real red hover preview.
        Find.DesignatorManager.Select(designator);
    }

    private static void FinishShipLandingHeightLimits()
    {
        Find.DesignatorManager.Deselect();heightLimitMarker.Destroy(DestroyMode.Vanish);
        AccessTools.Field(typeof(WorldComponent_GravshipController),"landingMarker").SetValue(Find.GravshipController,null);
        GravshipPlacementUtility.PlaceGravshipInMap(heightLimitShip,heightLimitSource,map,out _);Current.Game.Gravship=null;
        Check("rejected-ship-remains-intact-and-can-be-placed-at-legal-height",heightLimitShip.Engine.Spawned&&ShipLayers.TryData(heightLimitShip,out var data)&&
            data.layerCount==2&&data.floors.All(f=>owner.Structure.HasSurface(heightLimitSource+ShipLayers.Offset(owner,0,f.local))));
    }
}
