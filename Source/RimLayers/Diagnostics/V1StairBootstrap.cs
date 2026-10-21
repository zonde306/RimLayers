using System.Text.Json;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static ThingDef BootstrapDef=>GenCommandLine.CommandLineArgPassed("rimlayersbootlift")?DefDatabase<ThingDef>.GetNamed("RL_LiftSingle"):RimLayersDefOf.RL_Stairs;
    private static Frame bootstrapFrame=null!;
    private static Map bootstrapDirectMap=null!;
    private static Building_Stairs bootstrapStair=null!,bootstrapDirectStair=null!;
    private static Thing bootstrapCargo=null!;
    private static int bootstrapPhase;
    private static readonly IntVec3 bootstrapSite=new(35,0,35);
    private static PlanetTile BootstrapTile()=>SpaceFixture?Find.WorldGrid.Orbit.Tiles.First(t=>Find.WorldObjects.MapParentAt(t.tile)==null).tile:
        Find.WorldGrid.Tiles.First(t=>t.PrimaryBiome==BiomeDefOf.TemperateForest&&TileFinder.IsValidTileForNewSettlement(t.tile)).tile;
    private static void BootstrapSite(Map target)
    {
        foreach(var c in new CellRect(20,20,45,45))
        {
            foreach(var t in c.GetThingList(target).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            target.roofGrid.SetRoof(c,null);
            if(SpaceFixture)target.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);
            target.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);target.fogGrid.Unfog(c);
        }
        target.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(20,20,45,45).Contains(c));
        foreach(var c in new[]{new IntVec3(31,0,31),new IntVec3(40,0,31),new IntVec3(31,0,40),new IntVec3(40,0,40)})
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),c,target);
        foreach(var c in new CellRect(30,30,12,12))target.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
    }
    private static void BeginStairBootstrap()
    {
        Check("new-player-starting-map-is-not-preallocated",LayerMapComponent.For(map)==null&&map.Size==new IntVec3(120,1,120));
        var construction=DefDatabase<WorkTypeDef>.GetNamed("Construction");
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(construction));
        pawn.jobs.StopAll();pawn.DeSpawn();
        map=MakeUnmanagedMap(BootstrapTile(),120,137);Current.Game.CurrentMap=map;BootstrapSite(map);
        if(SpaceFixture)
        {
            var engine=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(53,0,53),map);engine.SetFaction(Faction.OfPlayer);engine.ForceSubstructureDirty();
            Check("fixture-is-legal-core-ship-on-unexpanded-orbit-map",RimLayers.Ships.ShipLayers.Eligible(engine)&&LayerMapComponent.For(map)==null);
            WearVacuumGear(pawn);
        }
        GenSpawn.Spawn(pawn,new IntVec3(33,0,33),map);pawn.drafter.Drafted=false;pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;
        foreach(var work in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(work,0);pawn.workSettings.SetPriority(construction,1);
        bootstrapCargo=ThingMaker.MakeThing(ThingDefOf.Silver);bootstrapCargo.stackCount=17;GenSpawn.Spawn(bootstrapCargo,new IntVec3(28,0,28),map);
        var worker=new PlaceWorker_LayerSupport();
        map.roofGrid.SetRoof(bootstrapSite,null);
        Check("unexpanded-stair-still-requires-artificial-roof",!worker.AllowsPlacing(BootstrapDef,bootstrapSite,Rot4.North,map).Accepted);
        map.roofGrid.SetRoof(bootstrapSite,RoofDefOf.RoofConstructed);
        Check("unexpanded-map-accepts-native-stair-blueprint",GenConstruct.CanPlaceBlueprintAt(BootstrapDef,bootstrapSite,Rot4.North,map).Accepted);
        Check("unexpanded-map-still-rejects-structural-slab",!worker.AllowsPlacing(RimLayersDefOf.RL_StructuralFloor,bootstrapSite,Rot4.North,map).Accepted);
        var blueprint=GenConstruct.PlaceBlueprintForBuild(BootstrapDef,bootstrapSite,map,Rot4.North,Faction.OfPlayer,null);
        Check("planning-does-not-create-layers",LayerMapComponent.For(map)==null&&map.Size==new IntVec3(120,1,137));
        blueprint.Destroy(DestroyMode.Cancel);
        Check("cancelling-blueprint-does-not-create-layers",LayerMapComponent.For(map)==null);
        bootstrapFrame=(Frame)ThingMaker.MakeThing(BootstrapDef.frameDef);bootstrapFrame.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(bootstrapFrame,bootstrapSite,map);
        foreach(var cost in BootstrapDef.CostListAdjusted(null))
        {var material=ThingMaker.MakeThing(cost.thingDef);material.stackCount=cost.count;bootstrapFrame.resourceContainer.TryAdd(material);}
        var materials=bootstrapFrame.resourceContainer.ToArray();
        map.roofGrid.SetRoof(bootstrapSite,null);bootstrapFrame.CompleteConstruction(pawn);
        Check("unexpanded-final-roof-check-keeps-frame-and-original-materials",LayerMapComponent.For(map)==null&&bootstrapFrame.Spawned&&!bootstrapFrame.Destroyed&&
            bootstrapFrame.resourceContainer.SequenceEqual(materials)&&materials.All(t=>!t.Destroyed)&&bootstrapFrame.resourceContainer.TotalStackCount==BootstrapDef.CostListAdjusted(null).Sum(c=>c.count));
        map.roofGrid.SetRoof(bootstrapSite,RoofDefOf.RoofConstructed);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var giver=(WorkGiver_ConstructFinishFrames)DefDatabase<WorkGiverDef>.GetNamed("ConstructFinishFrames").Worker;
        var job=giver.JobOnThing(pawn,bootstrapFrame,true);
        Check("native-builder-can-take-unexpanded-stair-frame",job!=null,JobFailReason.Reason);
        if(job==null)throw new InvalidOperationException("Fixture builder cannot construct stairs.");
        bootstrapFrame.workDone=bootstrapFrame.WorkToBuild-1;
        pawn.jobs.TryTakeOrderedJob(job);
        Check("starting-construction-does-not-create-layers-early",LayerMapComponent.For(map)==null);
        bootstrapPhase=0;stage=350;ticks=0;
    }
    private static void StairBootstrapStep()
    {
        if(bootstrapPhase==0)
        {
            if(!bootstrapFrame.Destroyed){if(ticks>3000)throw new Exception("Native stair construction did not finish: "+DescribePawn());return;}
            owner=LayerMapComponent.For(map)??throw new Exception("Completed stairs did not create floors.");
            bootstrapStair=map.listerThings.ThingsOfDef(BootstrapDef).OfType<Building_Stairs>().Single();
            Check("native-completion-creates-one-stair-and-only-two-floors",owner.Layout is {Width:120,Height:137,LayerCount:2}&&bootstrapStair.Position==bootstrapSite&&bootstrapFrame.resourceContainer.Count==0);
            Check("completion-creates-supported-registered-upper-landing",owner.Structure.HasSurface(bootstrapStair.UpperCell)&&owner.Connectors.Usable(bootstrapStair,TraverseParms.For(pawn)));
            Check("original-pawn-and-cargo-survive-stair-expansion",pawn.Spawned&&pawn.Map==map&&bootstrapCargo.Spawned&&bootstrapCargo.Map==map&&bootstrapCargo.stackCount==17);
            var layout=owner.Layout;var size=map.Size;
            GenSpawn.Spawn(BootstrapDef,new IntVec3(37,0,35),map);
            Check("second-stair-does-not-expand-existing-layout-again",ReferenceEquals(layout,owner.Layout)&&map.Size==size&&map.listerThings.ThingsOfDef(BootstrapDef).Count==2);
            pawn.drafter.Drafted=true;destination=bootstrapStair.UpperCell;pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination));
            bootstrapPhase=1;ticks=0;return;
        }
        if(bootstrapPhase==1)
        {
            if(pawn.Position!=destination){if(ticks>3000)throw new Exception("Constructed stairs do not route the pawn.");return;}
            Check("native-pawn-walks-up-newly-created-stairs",pawn.Position==bootstrapStair.UpperCell);
            bootstrapDirectMap=MakeUnmanagedMap(BootstrapTile(),120,137);BootstrapSite(bootstrapDirectMap);
            bootstrapDirectStair=(Building_Stairs)GenSpawn.Spawn(BootstrapDef,bootstrapSite,bootstrapDirectMap);bootstrapDirectStair.SetFaction(Faction.OfPlayer);
            var resident=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
            if(SpaceFixture)WearVacuumGear(resident);
            GenSpawn.Spawn(resident,new IntVec3(33,0,33),bootstrapDirectMap);resident.drafter.Drafted=true;
            Check("direct-spawn-fixture-is-an-active-map-not-eligible-for-native-removal",!bootstrapDirectMap.Parent.ShouldRemoveMapNow(out _));
            Current.Game.CurrentMap=map;
            Check("direct-spawn-queues-bootstrap-without-resizing-inside-spawn",LayerMapComponent.For(bootstrapDirectMap)==null&&bootstrapDirectMap.GetComponent<LayerMapComponent>().StairBootstrapPending);
            File.WriteAllText(Path.Combine(directory,"stair-bootstrap-baseline.json"),JsonSerializer.Serialize(new{map=map.uniqueID,pawn=pawn.ThingID,stair=bootstrapStair.ThingID,cargo=bootstrapCargo.ThingID,directMap=bootstrapDirectMap.uniqueID,directStair=bootstrapDirectStair.ThingID}));
            GameDataSaveLoader.SaveGame("RimLayers_V1StairBootstrap");
            Check("saved-finished-stair-before-pending-bootstrap-ran",LayerMapComponent.For(bootstrapDirectMap)==null);
            bootstrapPhase=2;ticks=0;return;
        }
        if(bootstrapPhase==2)
        {
            var direct=LayerMapComponent.For(bootstrapDirectMap);
            if(direct==null){if(ticks>20)throw new Exception($"Direct-spawn bootstrap did not run: disposed={bootstrapDirectMap.Disposed},present={Find.Maps.Contains(bootstrapDirectMap)},pending={bootstrapDirectMap.GetComponent<LayerMapComponent>().StairBootstrapPending}");return;}
            Check("direct-finished-stair-creates-layers-at-safe-map-tick",bootstrapDirectStair.Spawned&&direct.Layout is {Height:137,LayerCount:2}&&direct.Structure.HasSurface(bootstrapDirectStair.UpperCell)&&direct.Connectors.HasAny&&!direct.StairBootstrapPending);

            CameraJumper.TryJump(pawn,CameraJumper.MovementMode.Cut);LayerView.Switch(owner,1);pendingScreenshot=Path.Combine(directory,"stair-created-upper-floor.png");screenshotHold=3;stage=11;ticks=0;
        }
    }
    private static void RestoreStairBootstrap()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"stair-bootstrap-baseline.json")));var b=doc.RootElement;
        map=Find.Maps.Single(m=>m.uniqueID==b.GetProperty("map").GetInt32());Current.Game.CurrentMap=map;owner=LayerMapComponent.For(map)!;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        bootstrapStair=map.listerThings.AllThings.OfType<Building_Stairs>().Single(s=>s.ThingID==b.GetProperty("stair").GetString());
        Check("stair-created-map-reloads-with-original-layout-and-landing",owner.Layout is {Width:120,Height:137,LayerCount:2}&&owner.Structure.HasSurface(bootstrapStair.UpperCell)&&pawn.Position==bootstrapStair.UpperCell&&map.listerThings.ThingsOfDef(BootstrapDef).Count==2);
        Check("original-cargo-survives-native-save-and-load",map.listerThings.AllThings.Any(t=>t.ThingID==b.GetProperty("cargo").GetString()&&t.stackCount==17));
        Check("reloaded-stair-retains-real-cross-layer-route",pawn.CanReach(new IntVec3(33,0,33),PathEndMode.OnCell,Danger.Deadly));
        bootstrapDirectMap=Find.Maps.Single(m=>m.uniqueID==b.GetProperty("directMap").GetInt32());
        bootstrapDirectStair=bootstrapDirectMap.listerThings.AllThings.OfType<Building_Stairs>().Single(s=>s.ThingID==b.GetProperty("directStair").GetString());
        Check("loaded-finished-stair-requeues-unpersisted-bootstrap-request",LayerMapComponent.For(bootstrapDirectMap)==null&&bootstrapDirectMap.GetComponent<LayerMapComponent>().StairBootstrapPending);
        stage=351;ticks=0;
    }
    private static void StairBootstrapLoadStep()
    {
        var direct=LayerMapComponent.For(bootstrapDirectMap);
        if(direct==null){if(ticks>20)throw new Exception("Loaded finished stair did not create layers.");return;}
        Check("loaded-finished-stair-creates-supported-upper-floor-once",direct.Layout is {Height:137,LayerCount:2}&&direct.Structure.HasSurface(bootstrapDirectStair.UpperCell)&&direct.Connectors.HasAny&&!direct.StairBootstrapPending);
        CameraJumper.TryJump(pawn,CameraJumper.MovementMode.Cut);LayerView.Switch(owner,1);pendingScreenshot=Path.Combine(directory,"stair-created-upper-reloaded.png");screenshotHold=3;stage=11;ticks=0;
    }
}
