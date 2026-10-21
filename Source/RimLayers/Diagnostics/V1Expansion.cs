using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Map expansionMap=null!;
    private static IntVec3 expansionOldSize;
    private static Thing expansionStack=null!,expansionTable=null!,expansionBlueprint=null!;
    private static Frame expansionFrame=null!;
    private static Job expansionJob=null!;
    private static int expansionPhase,expansionLight;
    private static readonly IntVec3 expansionSample=new(32,0,32),expansionGoal=new(82,0,82);
    private static readonly JsonSerializerOptions expansionJson=new(){WriteIndented=true};
    private sealed record GridPrefix(object Owner,FieldInfo Field,object?[] Values);

    private static bool SpaceFixture=>GenCommandLine.CommandLineArgPassed("rimlayersspace");
    private static Map MakeUnmanagedMap(PlanetTile tile,int width,int height)
    {
        MapParent parent;
        if(tile.Layer.Def.isSpace)
        {
            parent=(MapParent)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("Space"));
            parent.Tile=tile;Find.WorldObjects.Add(parent);
        }
        else parent=SettleUtility.AddNewHome(tile,Find.WorldObjects.Settlements.First(s=>s.Faction!=null&&!s.Faction.IsPlayer&&!s.Faction.def.hidden).Faction);
        return MapGenerator.GenerateMap(new IntVec3(width,1,height),parent,tile.Layer.Def.isSpace?DefDatabase<MapGeneratorDef>.GetNamed("Space"):MapGeneratorDefOf.Encounter);
    }
    private static void WearVacuumGear(Pawn p)
    {
        foreach(var apparel in p.apparel.WornApparel.ToList())p.apparel.Remove(apparel);
        foreach(var name in new[]{"Apparel_Vacsuit","Apparel_VacsuitHelmet"})p.apparel.Wear((Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(name)));
    }
    private static void BeginExpansion()
    {
        var tile=SpaceFixture?Find.WorldGrid.Orbit.Tiles.First(t=>Find.WorldObjects.MapParentAt(t.tile)==null).tile:Find.WorldGrid.Tiles.First(t=>t.PrimaryBiome==BiomeDefOf.TemperateForest&&
            (!GenCommandLine.CommandLineArgPassed("rimlayersriver")||!t.Rivers.NullOrEmpty())&&TileFinder.IsValidTileForNewSettlement(t.tile)).tile;
        int width=120;if(GenCommandLine.TryGetCommandLineArg("rimlayersmapsize",out var size))int.TryParse(size,out width);
        expansionMap=MakeUnmanagedMap(tile,width,width+17);
        map=expansionMap;Current.Game.CurrentMap=map;expansionOldSize=map.Size;
        Check("fixture-is-an-existing-unmanaged-foreign-map",LayerMapComponent.For(map)==null&&map.Size.z==width+17&&map.Parent.Faction!=Faction.OfPlayer);
        if(GenCommandLine.CommandLineArgPassed("rimlayersriver"))Check("fixture-has-native-river-flow",map.waterInfo.riverFlowMap?.Count==map.Size.x*map.Size.z*2);
        foreach(var c in new CellRect(20,20,75,75))
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);if(SpaceFixture)map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>new CellRect(20,20,75,75).Contains(c));
        pawn=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);GenSpawn.Spawn(pawn,new IntVec3(25,0,25),map);
        if(SpaceFixture)WearVacuumGear(pawn);
        pawn.drafter.Drafted=true;
        var held=ThingMaker.MakeThing(ThingDefOf.Steel);held.stackCount=11;pawn.inventory.innerContainer.TryAdd(held);
        expansionStack=ThingMaker.MakeThing(ThingDefOf.Silver);expansionStack.stackCount=37;GenSpawn.Spawn(expansionStack,new IntVec3(33,0,30),map);
        expansionTable=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TableButcher"),ThingDefOf.WoodLog),new IntVec3(35,0,30),map);
        expansionTable.SetFaction(Faction.OfPlayer);((Building_WorkTable)expansionTable).BillStack.AddBill(new Bill_Production(DefDatabase<RecipeDef>.GetNamed("ButcherCorpseFlesh"),null){repeatCount=7});
        expansionBlueprint=GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Stool,new IntVec3(39,0,30),map,Rot4.North,Faction.OfPlayer,ThingDefOf.WoodLog);
        expansionFrame=(Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef,ThingDefOf.Steel);expansionFrame.SetFaction(Faction.OfPlayer);
        var material=ThingMaker.MakeThing(ThingDefOf.Steel);material.stackCount=4;expansionFrame.resourceContainer.TryAdd(material);GenSpawn.Spawn(expansionFrame,new IntVec3(41,0,30),map);
        var store=new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager){label="Expansion stores"};map.zoneManager.RegisterZone(store);store.AddCell(expansionStack.Position);store.settings.Priority=StoragePriority.Critical;
        map.areaManager.TryMakeNewAllowed(out var area);area.SetLabel("Expansion crew");foreach(var c in new CellRect(20,20,75,75))area[c]=true;
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap=area;map.areaManager.Home[expansionSample]=true;
        map.designationManager.AddDesignation(new Designation(new IntVec3(31,0,31),DesignationDefOf.RemoveFloor));
        foreach(var c in new CellRect(45,45,8,8).EdgeCells)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),c,map);
        foreach(var c in new CellRect(45,45,8,8))map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
        var torch=(ThingWithComps)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("TorchLamp"),new IntVec3(48,0,48),map);torch.GetComp<CompRefuelable>().Refuel(30);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();new IntVec3(48,0,48).GetRoom(map).Temperature=27.5f;
        expansionLight=map.glowGrid.RegisterCustomGlower(new IntVec3(65,0,25),5f,new ColorInt(30,120,230),0f,false);
        map.snowGrid.SetDepth(expansionSample,.3f);if(map.sandGrid!=null)map.sandGrid.SetDepth(expansionSample,.4f);
        map.gasGrid.AddGas(expansionSample,GasType.BlindSmoke,110);if(ModsConfig.BiotechActive)map.pollutionGrid.SetPolluted(expansionSample,true);
        map.deepResourceGrid.SetAt(expansionSample,ThingDefOf.Steel,912);
        map.fogGrid.Refog(new CellRect(4,4,3,3));
        expansionJob=JobMaker.MakeJob(JobDefOf.Goto,expansionGoal);pawn.jobs.TryTakeOrderedJob(expansionJob);
        Check("fixture-has-a-real-active-job-and-reservation",pawn.CurJob==expansionJob&&pawn.Reserve(expansionStack,expansionJob));
        expansionPhase=0;stage=340;ticks=0;
    }

    private static List<GridPrefix> ExpansionPrefixes()
    {
        var fields=new List<GridPrefix>();
        foreach(var (obj,names) in new (object,string[])[]{(map.terrainGrid,new[]{"topGrid","underGrid","tempGrid","foundationGrid","colorGrid"}),
            (map.roofGrid,new[]{"roofGrid"}),(map.gasGrid,new[]{"gasDensity"}),(map.deepResourceGrid,new[]{"countGrid","defGrid"}),
            (map.haulDestinationManager,new[]{"groupGrid"}),(map.zoneManager,new[]{"zoneGrid"}),(map.planManager,new[]{"planGrid"}),(map.thingGrid,new[]{"thingGrid"})})
            foreach(var name in names){var field=AccessTools.Field(obj.GetType(),name);fields.Add(new(obj,field,((Array)field.GetValue(obj)!).Cast<object?>().ToArray()));}
        return fields;
    }
    private static bool ExpansionPrefixesMatch(List<GridPrefix> before)=>before.All(p=>
    {
        var a=(Array)p.Field.GetValue(p.Owner)!;
        if(a.Rank==1)return a.Cast<object?>().Take(p.Values.Length).SequenceEqual(p.Values);
        int height=p.Values.Length/a.GetLength(0); // Native groupGrid is x/y/z; height expansion changes its stride.
        return Enumerable.Range(0,a.GetLength(0)).All(x=>Enumerable.Range(0,height).All(z=>Equals(a.GetValue(x,0,z),p.Values[x*height+z])));
    });
    private static void ExpansionStep()
    {
        if(expansionPhase==0)
        {
            if(ticks<10)return;
            var originalMap=map;var oldThings=map.listerThings.AllThings.ToArray();var counts=oldThings.ToDictionary(t=>t,t=>t.stackCount);
            var prefix=ExpansionPrefixes();int count=map.cellIndices.NumGridCells;
            var fog=Enumerable.Range(0,count).Select(map.fogGrid.IsFogged).ToArray();
            var snow=Enumerable.Range(0,count).Select(i=>map.snowGrid.GetDepth(map.cellIndices.IndexToCell(i))).ToArray();
            var sand=Enumerable.Range(0,count).Select(i=>map.sandGrid?.GetDepth(map.cellIndices.IndexToCell(i))??0).ToArray();
            var originalDriver=pawn.jobs.curDriver;int toil=originalDriver.CurToilIndex;var target=pawn.pather.Destination;
            var climate=new IntVec3(48,0,48).GetRoom(map).Temperature;
            var oldFlow=map.waterInfo.riverFlowMap?.ToArray();
            bool injected=false;double referenceThreeLayerMs=0;var referenceTimer=System.Diagnostics.Stopwatch.StartNew();
            try{LiveMapExpansion.Expand(map,()=>{referenceThreeLayerMs=referenceTimer.Elapsed.TotalMilliseconds;throw new InvalidOperationException("isolated expansion rollback fault");});}
            catch(InvalidOperationException e)when(e.Message=="isolated expansion rollback fault"){injected=true;}
            Check("injected-failure-restores-original-map-layout-and-data",injected&&map==originalMap&&map.Size==expansionOldSize&&LayerMapComponent.For(map)==null&&ExpansionPrefixesMatch(prefix));
            Check("rollback-preserves-active-job-driver-path-and-reservation",pawn.CurJob==expansionJob&&pawn.jobs.curDriver==originalDriver&&pawn.pather.Moving&&pawn.pather.Destination==target&&map.reservationManager.ReservedBy(expansionStack,pawn,expansionJob));
            var result=LiveMapExpansion.EnsureLayerCount(map,2);owner=LayerMapComponent.For(map)??throw new InvalidOperationException("Expanded map has no layer layout.");
            Check("first-append-allocates-only-second-floor",owner.Layout!.LayerCount==2);
            var originalLayout=owner.Layout;var twoSize=map.Size;
            var sample=At(1,26,26);map.roofGrid.SetRoof(new IntVec3(26,0,26),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(sample,RimLayersDefOf.RL_StructuralFloor);
            var upperCargo=GenSpawn.Spawn(ThingDefOf.Silver,sample,map);upperCargo.stackCount=9;
            var upperPrefix=ExpansionPrefixes();
            bool secondFault=false;
            try{LiveMapExpansion.EnsureLayerCount(map,3,()=>throw new InvalidOperationException("second append fault"));}
            catch(InvalidOperationException e)when(e.Message=="second append fault"){secondFault=true;}
            Check("second-append-rollback-retains-existing-layout-upper-grid-and-structure",secondFault&&ReferenceEquals(owner.Layout,originalLayout)&&map.Size==twoSize&&owner.Structure.HasSurface(sample)&&upperCargo.Position==sample&&ExpansionPrefixesMatch(upperPrefix));
            LiveMapExpansion.EnsureLayerCount(map,3);
            Check("second-append-retains-existing-floor-and-entity",owner.Layout!.LayerCount==3&&owner.Structure.HasSurface(sample)&&upperCargo.Spawned&&upperCargo.Position==sample&&ExpansionPrefixesMatch(upperPrefix));
            upperCargo.Destroy(DestroyMode.Vanish);owner.StructuralCells.Remove(sample);map.terrainGrid.SetTerrain(sample,RimLayersDefOf.RL_OpenAir);
            map.roofGrid.SetRoof(new IntVec3(26,0,26),null);
            File.WriteAllText(Path.Combine(directory,"expansion-buffers.json"),JsonSerializer.Serialize(new{before=new{x=result.Before.x,z=result.Before.z},after=new{x=result.After.x,z=result.After.z},referenceThreeLayerMs,result.Milliseconds,result.Phases,result.Buffers},expansionJson));
            Check("successful-expansion-keeps-same-map-and-appends-three-layers",map==originalMap&&owner.Layout!.Height==expansionOldSize.z&&owner.Layout.Width==expansionOldSize.x);
            Check("every-original-spawned-thing-keeps-identity-map-and-stack",map.listerThings.AllThings.Count==oldThings.Length&&oldThings.All(t=>map.listerThings.AllThings.Contains(t)&&t.Spawned&&t.Map==map&&t.stackCount==counts[t]));
            Check("all-original-terrain-roof-gas-resource-zone-and-thing-grids-keep-prefix",ExpansionPrefixesMatch(prefix));
            Check("native-fog-snow-sand-contents-keep-exact-original-prefix",Enumerable.Range(0,count).All(i=>map.fogGrid.IsFogged(i)==fog[i]&&map.snowGrid.GetDepth(map.cellIndices.IndexToCell(i))==snow[i]&&(map.sandGrid?.GetDepth(map.cellIndices.IndexToCell(i))??0)==sand[i]));
            Check("expansion-keeps-active-job-driver-toil-and-native-reservation",pawn.CurJob==expansionJob&&pawn.jobs.curDriver==originalDriver&&originalDriver.CurToilIndex==toil&&pawn.pather.Destination==target&&pawn.pather.Moving&&map.reservationManager.ReservedBy(expansionStack,pawn,expansionJob));
            Check("room-climate-and-native-workbench-frame-blueprint-survive",Math.Abs(new IntVec3(48,0,48).GetRoom(map).Temperature-climate)<.001&&((Building_WorkTable)expansionTable).BillStack.Bills.OfType<Bill_Production>().Single().repeatCount==7&&expansionFrame.resourceContainer.TotalStackCount==4&&expansionBlueprint.Spawned);
            Check("areas-zone-settings-designation-and-pollution-survive",pawn.playerSettings.AreaRestrictionInPawnCurrentMap?.Label=="Expansion crew"&&map.areaManager.Home[expansionSample]&&map.zoneManager.ZoneAt(expansionStack.Position) is Zone_Stockpile z&&z.settings.Priority==StoragePriority.Critical&&map.designationManager.DesignationAt(new IntVec3(31,0,31),DesignationDefOf.RemoveFloor)!=null&&(!ModsConfig.BiotechActive||map.pollutionGrid.IsPolluted(expansionSample)));
            if(oldFlow is {Length:>0})Check("river-vector-field-remaps-every-original-x-major-cell",Enumerable.Range(0,expansionOldSize.x).All(x=>Enumerable.Range(0,expansionOldSize.z).All(z=>Enumerable.Range(0,2).All(a=>oldFlow[(x*expansionOldSize.z+z)*2+a]==map.waterInfo.riverFlowMap![(x*owner.Layout!.StorageHeight+z)*2+a]))));
            var lit=map.glowGrid.VisualGlowAt(new IntVec3(65,0,25));map.glowGrid.DeregisterCustomGlower(expansionLight);map.glowGrid.GlowGridUpdate_First();
            var unlit=map.glowGrid.VisualGlowAt(new IntVec3(65,0,25));Check("custom-native-light-handle-remains-valid-after-resize",lit.b>unlit.b,$"before={lit},after={unlit}");
            Check("appended-space-and-isolation-are-unwalkable-with-valid-grid-capacity",!owner!.Structure.HasSurface(At(1,10,10))&&map.terrainGrid.TerrainAt(new IntVec3(10,0,expansionOldSize.z))==RimLayersDefOf.RL_Isolation);
            expansionPhase=1;ticks=0;return;
        }
        if(expansionPhase==1)
        {
            if(pawn.Position!=expansionGoal){if(ticks>4000)throw new Exception("Original Goto did not resume after expansion.");return;}
            Check("original-job-reaches-original-target-after-expansion",pawn.Position==expansionGoal);
            for(int l=0;l<2;l++)
            {
                if(l>0)foreach(var c in new CellRect(20,70,15,15))map.terrainGrid.SetTerrain(At(l,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);
                foreach(var c in new[]{new IntVec3(21,0,71),new IntVec3(27,0,71),new IntVec3(33,0,71),new IntVec3(21,0,81),new IntVec3(27,0,81),new IntVec3(33,0,81)})GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(l,c.x,c.z),map);
                foreach(var c in new CellRect(20,70,15,15))map.roofGrid.SetRoof(At(l,c.x,c.z),RoofDefOf.RoofConstructed);
            }
            foreach(var c in new CellRect(20,70,15,15))map.terrainGrid.SetTerrain(At(2,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);
            GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(0,22,72),map);GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(1,30,80),map);
            destination=At(2,31,81);pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination));expansionPhase=2;ticks=0;return;
        }
        if(expansionPhase==2)
        {
            if(pawn.Position!=destination){if(ticks>5000)throw new Exception("New upper floors are not navigable.");return;}
            Check("original-pawn-can-use-newly-allocated-two-stair-route",owner.TryDecode(pawn.Position,out var pos)&&pos.LayerId==2&&owner.Structure.HasSurface(pawn.Position));
            File.WriteAllText(Path.Combine(directory,"expansion-baseline.json"),JsonSerializer.Serialize(new{map=map.uniqueID,pawn=pawn.ThingID,stack=expansionStack.ThingID,table=expansionTable.ThingID,frame=expansionFrame.ThingID,blueprint=expansionBlueprint.ThingID,
                inventory=pawn.inventory.innerContainer.Select(t=>t.ThingID).ToArray(),width=owner.Layout!.Width,height=owner.Layout.Height,stride=owner.Layout.Stride},expansionJson));
            GameDataSaveLoader.SaveGame("RimLayers_V1Expansion");
            CameraJumper.TryJump(pawn,CameraJumper.MovementMode.Cut);LayerView.Switch(owner,2);pendingScreenshot=Path.Combine(directory,"expanded-map-upper-floor.png");screenshotHold=3;stage=11;ticks=0;
        }
    }
    private static void RestoreExpansion()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"expansion-baseline.json")));var b=doc.RootElement;
        map=Find.Maps.Single(m=>m.uniqueID==b.GetProperty("map").GetInt32());Current.Game.CurrentMap=map;owner=LayerMapComponent.For(map)??throw new InvalidOperationException("Expanded map has no layer layout.");
        pawn=map.mapPawns.AllPawnsSpawned.Single(t=>t.ThingID==b.GetProperty("pawn").GetString());
        Check("expanded-foreign-map-reloads-with-original-id-layout-and-upper-pawn",owner.Layout!.Width==b.GetProperty("width").GetInt32()&&owner.Layout.Height==b.GetProperty("height").GetInt32()&&owner.Layout.Stride==b.GetProperty("stride").GetInt32()&&owner.TryDecode(pawn.Position,out var p)&&p.LayerId==2&&owner.Structure.HasSurface(pawn.Position));
        Thing Original(string name)=>map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty(name).GetString());
        Check("reload-keeps-fixture-entities-bills-and-materials-once",Original("stack").stackCount==37&&((Building_WorkTable)Original("table")).BillStack.Bills.OfType<Bill_Production>().Single().repeatCount==7&&((Frame)Original("frame")).resourceContainer.TotalStackCount==4&&Original("blueprint") is Blueprint);
        Check("reload-keeps-real-held-inventory-and-area-assignment",pawn.inventory.innerContainer.Select(t=>t.ThingID).SequenceEqual(b.GetProperty("inventory").EnumerateArray().Select(t=>t.GetString()))&&pawn.playerSettings.AreaRestrictionInPawnCurrentMap?.Label=="Expansion crew");
        Check("reload-keeps-original-resource-and-pollution-data",map.deepResourceGrid.CountAt(expansionSample)==912&&map.deepResourceGrid.ThingDefAt(expansionSample)==ThingDefOf.Steel&&(!ModsConfig.BiotechActive||map.pollutionGrid.IsPolluted(expansionSample)));
        Check("repeated-expansion-is-idempotent",LiveMapExpansion.Expand(map).Before==map.Size);
        Check("reloaded-expanded-map-remains-cross-layer-reachable",pawn.CanReach(new IntVec3(25,0,25),PathEndMode.OnCell,Danger.Deadly));
        CameraJumper.TryJump(pawn,CameraJumper.MovementMode.Cut);LayerView.Switch(owner,2);pendingScreenshot=Path.Combine(directory,"expanded-map-reloaded.png");screenshotHold=3;stage=11;ticks=0;
    }
}
