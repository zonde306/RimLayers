using HarmonyLib;
using RimLayers.Layout;
using RimLayers.Ships;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void BeginShipLandingItems()
    {
        // A real three-deck ship with a hollow middle, using the established roof-support fixture.
        var rect=new CellRect(18,90,5,6);
        foreach(var c in rect)
        {
            foreach(var t in c.GetThingList(map).ToArray())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
            if(map.terrainGrid.UnderTerrainAt(c)!=null)map.terrainGrid.RemoveTopLayer(c,false);
            map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);
        }
        foreach(int layer in new[]{0,1,2})
        {
            foreach(var c in rect)
            {
                if(layer==1&&(c.x==18||c.x==22)||layer==2)ShipDeckTerrain.Install(owner,At(layer,c.x,c.z),false);
                map.roofGrid.SetRoof(At(layer,c.x,c.z),RoofDefOf.RoofConstructed);map.fogGrid.Unfog(At(layer,c.x,c.z));
            }
            foreach(var c in new[]{new IntVec3(18,0,90),new IntVec3(22,0,90),new IntVec3(18,0,95),new IntVec3(22,0,95)})
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(layer,c.x,c.z),map).SetFaction(Faction.OfPlayer);
        }
        var core=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(20,0,92),map);core.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(ThingDefOf.PilotConsole,new IntVec3(20,0,94),map).SetFaction(Faction.OfPlayer);
        var cargo=ThingMaker.MakeThing(ThingDefOf.Silver);cargo.stackCount=37;GenSpawn.Spawn(cargo,At(2,20,93),map);
        owner.Ships.MarkDirty();owner.Ships.Tick();
        core.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
        var ship=GravshipUtility.GenerateGravship(core);
        Check("items-fixture-real-hollow-three-deck-ship",ShipLayers.TryData(ship,out var data)&&data.layerCount==3&&data.floors.Count==72&&
            !data.floors.Any(f=>f.local==new IntVec3(0,1,0))&&ship.Things.Contains(cargo));
        if(!CheckLandingItemsAt(ship,new IntVec3(20,0,92),Rot4.North,"ground")){Finish();return;}
        Check("ground-landing-keeps-original-ship-cargo",cargo.Spawned&&!cargo.Destroyed&&cargo.stackCount==37);
        core.launchInfo=new LaunchInfo{pilot=pawn,quality=1,doNegativeOutcome=false};
        ship=GravshipUtility.GenerateGravship(core);
        CoexistPlatform(new CellRect(24,21,18,18));
        RimLayersMod.Settings.UpperLimit=3; // Isolated fixture: three decks landing one floor above ground.
        ShipLanding.EnsureCapacity(ship,map,3);
        if(CheckLandingItemsAt(ship,At(1,31,30),Rot4.East,"elevated-rotated"))
            Check("upper-landing-keeps-original-ship-cargo",cargo.Spawned&&!cargo.Destroyed&&cargo.stackCount==37&&owner.TryDecode(cargo.Position,out var at)&&owner.Elevation(at.LayerId)==3);
        Finish();
    }

    private static bool CheckLandingItemsAt(Gravship ship,IntVec3 root,Rot4 rotation,string label)
    {
        ship.Rotation=rotation;owner.TryDecode(root,out var origin);
        IntVec3 Cell(int x,int y,int z)=>root+ShipLayers.Offset(owner,origin.LayerId,PrefabUtility.GetAdjustedLocalPosition(new IntVec3(x,y,z),rotation));
        var bottom=Cell(0,0,0);var top=Cell(0,2,0);var hollow=Cell(0,1,0);var beside=Cell(6,0,0);
        ShipLayers.TryData(ship,out var data);
        foreach(var floor in data.floors)map.fogGrid.Unfog(Cell(floor.local.x,floor.local.y,floor.local.z));
        var clean=ShipLanding.Check(ship,root,map);
        Check(label+"-empty-landing-valid",clean.Accepted,clean.Reason);if(!clean.Accepted)return false;
        Thing Item(ThingDef def,IntVec3 cell,int count=1)
        {var thing=ThingMaker.MakeThing(def);thing.stackCount=count;return GenSpawn.Spawn(thing,cell,map);}
        Pawn Animal(IntVec3 cell,Faction? faction)
        {var animal=PawnGenerator.GeneratePawn(PawnKindDefOf.Muffalo,faction);return (Pawn)GenSpawn.Spawn(animal,cell,map);}
        var chunkDef=DefDatabase<ThingDef>.GetNamed("ChunkGranite");
        var chunk=Item(chunkDef,bottom);var upperChunk=Item(chunkDef,top);
        var material=Item(ThingDefOf.Steel,Cell(1,0,3),23);
        var holeItem=Item(chunkDef,hollow);var sideItem=Item(ThingDefOf.Steel,beside,19);
        var tame=Animal(Cell(0,0,3),Faction.OfPlayer);var wild=Animal(Cell(1,2,1),null);
        var holeAnimal=Animal(Cell(1,1,0),null);
        var treeDef=DefDatabase<ThingDef>.GetNamed("Plant_TreeOak");
        var grassDef=DefDatabase<ThingDef>.GetNamed("Plant_Grass");
        var tree=Item(treeDef,Cell(-1,0,0));var grass=Item(grassDef,Cell(-1,2,0));
        var crop=Item(DefDatabase<ThingDef>.GetNamed("Plant_Rice"),Cell(-1,0,3));
        var holePlant=Item(grassDef,Cell(-1,1,0));var sidePlant=Item(grassDef,Cell(6,0,1));
        AcceptanceReport NativeCell(IntVec3 cell)=>(AcceptanceReport)AccessTools.Method(typeof(Designator_MoveGravship),"IsValidCell").Invoke(null,new object[]{cell,map})!;
        var allowed=ShipLanding.Check(ship,root,map);
        Check(label+"-items-and-nonhostile-animals-allow-landing",allowed.Accepted,allowed.Reason);
        Check(label+"-native-cell-allows-items-tame-and-wild-animals",NativeCell(bottom).Accepted&&NativeCell(tame.Position).Accepted);
        Check(label+"-preview-preserves-items-animals-and-hollow-space",chunk.Spawned&&upperChunk.Spawned&&material.stackCount==23&&
            holeItem.Spawned&&holeItem.Position==hollow&&tame.Spawned&&wild.Spawned&&holeAnimal.Spawned&&!owner.StructuralCells.Contains(hollow));

        var treeReport=NativeCell(tree.Position);var cropReport=NativeCell(crop.Position);
        Check(label+"-ordinary-trees-grass-crops-allow-landing",allowed.Accepted&&treeReport.Accepted&&cropReport.Accepted,$"ship={allowed.Reason},tree={treeReport.Reason},crop={cropReport.Reason}");
        Check(label+"-preview-does-not-clear-plants",tree.Spawned&&grass.Spawned&&crop.Spawned&&holePlant.Spawned&&sidePlant.Spawned);
        bool plantFlag=treeDef.preventGravshipLandingOn;
        try
        {
            treeDef.preventGravshipLandingOn=true;
            Check(label+"-explicit-plant-landing-opt-out-is-respected",!ShipLanding.Check(ship,root,map).Accepted&&!NativeCell(tree.Position).Accepted);
        }
        finally{treeDef.preventGravshipLandingOn=plantFlag;}
        bool flag=chunkDef.preventGravshipLandingOn;
        try
        {
            chunkDef.preventGravshipLandingOn=true;
            Check(label+"-explicit-item-landing-opt-out-is-respected",!ShipLanding.Check(ship,root,map).Accepted&&!NativeCell(bottom).Accepted);
        }
        finally{chunkDef.preventGravshipLandingOn=flag;}
        var oldPosition=pawn.Position;pawn.Position=bottom;
        try{Check(label+"-humanlike-pawn-still-blocks",!ShipLanding.Check(ship,root,map).Accepted&&!NativeCell(bottom).Accepted);}
        finally{pawn.Position=oldPosition;}
        var hostile=Animal(Cell(1,0,2),Faction.OfInsects);
        Check(label+"-hostile-nonhuman-pawn-still-blocks",hostile.HostileTo(Faction.OfPlayer)&&!ShipLanding.Check(ship,root,map).Accepted&&!NativeCell(hostile.Position).Accepted);
        hostile.Destroy(DestroyMode.Vanish);
        var wall=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),Cell(-1,1,1),map);
        Check(label+"-hollow-column-building-still-blocks",!ShipLanding.Check(ship,root,map).Accepted);wall.Destroy(DestroyMode.Vanish);
        var after=ShipLanding.Check(ship,root,map);
        Check(label+"-removing-real-blockers-restores-landing",after.Accepted,after.Reason);if(!after.Accepted)return false;
        GravshipPlacementUtility.PlaceGravshipInMap(ship,root,map,out _);Current.Game.Gravship=null;
        Check(label+"-native-landing-clears-only-covered-items",chunk.Destroyed&&upperChunk.Destroyed&&material.Destroyed&&
            holeItem.Spawned&&holeItem.Position==hollow&&sideItem.Spawned&&sideItem.Position==beside&&sideItem.stackCount==19);
        Check(label+"-native-landing-clears-covered-animals-not-hollow-animal",tame.Destroyed&&wild.Destroyed&&holeAnimal.Spawned&&!holeAnimal.Destroyed);
        Check(label+"-native-landing-clears-covered-plants-only",tree.Destroyed&&grass.Destroyed&&crop.Destroyed&&
            holePlant.Spawned&&holePlant.Position==Cell(-1,1,0)&&sidePlant.Spawned&&sidePlant.Position==Cell(6,0,1));
        Check(label+"-landing-does-not-fill-hollow-deck",!owner.StructuralCells.Contains(hollow)&&map.terrainGrid.FoundationAt(hollow)==null);
        // Remove the deliberate unsupported airspace probes before the next scenario ticks.
        holeItem.Destroy(DestroyMode.Vanish);holeAnimal.Destroy(DestroyMode.Vanish);sideItem.Destroy(DestroyMode.Vanish);
        holePlant.Destroy(DestroyMode.Vanish);sidePlant.Destroy(DestroyMode.Vanish);
        return true;
    }
}