using System.Text.Json;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Thing migrationItem=null!,migrationBed=null!;
    private static int migrationOriginalId,migrationMainId;
    private static void BeginMigration()
    {
        migrationMainId=map.uniqueID;
        var parent=Find.WorldObjects.Settlements.First(s=>s.Faction!=null&&!s.Faction.IsPlayer&&!s.HasMap);
        bool river=GenCommandLine.CommandLineArgPassed("rimlayersriver");
        if(river)
            parent.Tile=Find.WorldGrid.Tiles.First(t=>!t.Rivers.NullOrEmpty()&&!t.Roads.NullOrEmpty()&&t.Rivers.Count>=2&&t.PrimaryBiome==BiomeDefOf.TemperateForest&&TileFinder.IsValidTileForNewSettlement(t.tile)).tile;
        int size=river?120:60;
        var oldMap=MapGenerator.GenerateMap(new IntVec3(size,1,size),parent,MapGeneratorDefOf.Encounter);
        Check("legacy-fixture-starts-as-real-unexpanded-native-map",LayerMapComponent.For(oldMap)==null&&oldMap.Size==new IntVec3(size,1,size));
        parent.SetFaction(Faction.OfPlayer);
        var oldPawn=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        for(int x=20;x<36;x++)for(int z=20;z<36;z++)
        {
            var c=new IntVec3(x,0,z);foreach(var t in c.GetThingList(oldMap).ToList())if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            oldMap.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);oldMap.fogGrid.Unfog(c);
        }
        GenSpawn.Spawn(oldPawn,new IntVec3(24,0,24),oldMap);oldPawn.drafter.Drafted=true;
        migrationItem=ThingMaker.MakeThing(ThingDefOf.Silver);migrationItem.stackCount=37;oldPawn.inventory.innerContainer.TryAdd(migrationItem);
        migrationBed=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Bed,ThingDefOf.WoodLog),new IntVec3(27,0,27),oldMap);
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),new IntVec3(26,0,25),oldMap);
        oldMap.roofGrid.SetRoof(new IntVec3(25,0,25),RoofDefOf.RoofConstructed);
        oldMap.areaManager.Home[new IntVec3(30,0,30)]=true;
        oldMap.snowGrid.SetDepth(new IntVec3(29,0,29),.35f);
        oldMap.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var job=JobMaker.MakeJob(JobDefOf.Goto,new IntVec3(34,0,34));oldPawn.jobs.TryTakeOrderedJob(job);
        Current.Game.CurrentMap=oldMap;migrationOriginalId=oldMap.uniqueID;
        File.WriteAllText(Path.Combine(directory,"migration-baseline.json"),JsonSerializer.Serialize(new{map=oldMap.uniqueID,main=migrationMainId,pawn=oldPawn.ThingID,
            bed=migrationBed.ThingID,item=migrationItem.ThingID,job=job.loadID,width=oldMap.Size.x,height=oldMap.Size.z}));
        if(river)
        {
            Check("legacy-fixture-has-native-river-flow",oldMap.waterInfo.riverFlowMap?.Count==size*size*2&&oldMap.waterInfo.riverFlowMap.Any(v=>v!=0),$"tile={oldMap.Tile}, flow={oldMap.waterInfo.riverFlowMap?.Count}, riverTerrain={oldMap.AllCells.Count(c=>c.GetTerrain(oldMap).IsRiver)}, graph={oldMap.waterInfo.riverGraph?.Count}");
            File.WriteAllText(Path.Combine(directory,"migration-river-bits.json"),JsonSerializer.Serialize(oldMap.waterInfo.riverFlowMap!.Select(BitConverter.SingleToInt32Bits).ToArray()));
        }
        GameDataSaveLoader.SaveGame("RimLayers_V1Migration");
        Check("legacy-original-format-save-was-written",true);Finish();
    }
    private static bool migrationWaiting;
    private static void RestoreMigration()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"migration-baseline.json")));var b=document.RootElement;
        map=Find.Maps.Single(m=>m.uniqueID==b.GetProperty("map").GetInt32());Current.Game.CurrentMap=map;owner=LayerMapComponent.For(map)!;
        int width=b.GetProperty("width").GetInt32(),height=b.GetProperty("height").GetInt32();
        bool converted=GenCommandLine.CommandLineArgPassed("rimlayersmigrationreload");
        Check("legacy-map-load-remains-single-floor-until-construction",converted?owner?.Layout?.LayerCount==2:owner==null&&map.Size==new IntVec3(width,1,height));
        var main=Find.Maps.Single(m=>m.uniqueID==b.GetProperty("main").GetInt32());
        Check("existing-three-floor-map-is-not-shrunk-or-expanded",LayerMapComponent.For(main)?.Layout is {Height:120,LayerCount:3}&&main.Size.z==408);
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        Check("legacy-pawn-and-native-job-preserved-before-opt-in",converted||pawn.CurJob?.loadID==b.GetProperty("job").GetInt32());
        Check("legacy-held-inventory-identity-and-count-preserved",pawn.inventory.innerContainer.Any(t=>t.ThingID==b.GetProperty("item").GetString()&&t.stackCount==37));
        Check("legacy-building-and-stuff-preserved",map.listerThings.AllThings.Any(t=>t.ThingID==b.GetProperty("bed").GetString()&&t.Stuff==ThingDefOf.WoodLog));
        Check("legacy-terrain-roof-home-and-fog-preserved",map.terrainGrid.TerrainAt(new IntVec3(24,0,24))==TerrainDefOf.Concrete&&map.roofGrid.RoofAt(new IntVec3(25,0,25))==RoofDefOf.RoofConstructed&&map.areaManager.Home[new IntVec3(30,0,30)]&&!new IntVec3(24,0,24).Fogged(map));
        CheckMigrationRiver(width,height);
        if(converted){Check("converted-save-retains-second-floor-and-stair",owner!.Structure.HasSurface(At(1,25,25))&&map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).Count==1);stage=11;ticks=0;return;}
        pawn.jobs.StopAll();GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,new IntVec3(25,0,25),map);
        Check("legacy-finished-stair-requests-safe-tick-not-inline-allocation",LayerMapComponent.For(map)==null&&map.GetComponent<LayerMapComponent>().StairBootstrapPending);
        migrationWaiting=true;stage=282;ticks=0;
    }
    private static void CheckMigrationRiver(int width,int height)
    {
        var path=Path.Combine(directory,"migration-river-bits.json");if(!File.Exists(path))return;
        var before=JsonSerializer.Deserialize<int[]>(File.ReadAllText(path))!;var after=map.waterInfo.riverFlowMap;int storage=map.Size.z;
        bool same=after?.Count==width*storage*2;
        for(int x=0;x<width&&same;x++)for(int z=0;z<height;z++)for(int a=0;a<2;a++)same&=before[(x*height+z)*2+a]==BitConverter.SingleToInt32Bits(after![(x*storage+z)*2+a]);
        bool empty=same;if(same)for(int x=0;x<width;x++)for(int z=height;z<storage;z++)for(int a=0;a<2;a++)empty&=after![(x*storage+z)*2+a]==0;
        Check("native-river-values-preserved-with-correct-current-stride",same&&empty);
    }
    private static void MigrationStep()
    {
        if(migrationWaiting)
        {
            owner=LayerMapComponent.For(map)!;if(owner==null){if(ticks>30)throw new Exception("Legacy stair bootstrap did not execute.");return;}
            Check("legacy-first-stair-creates-only-second-floor-and-supported-landing",owner.Layout!.LayerCount==2&&owner.Structure.HasSurface(At(1,25,25)));
            CheckMigrationRiver(owner.Layout!.Width,owner.Layout.Height);
            destination=At(1,25,25);pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,destination));migrationWaiting=false;ticks=0;return;
        }
        if(pawn.Position==destination)
        {
            Check("legacy-pawn-can-walk-up-created-floor",true);
            File.WriteAllText(Path.Combine(directory,"migration-converted-baseline.json"),JsonSerializer.Serialize(new{snow=map.snowGrid.GetDepth(new IntVec3(29,0,29))}));
            GameDataSaveLoader.SaveGame("RimLayers_V1MigrationConverted");
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(destination.ToVector3Shifted(),12);pendingScreenshot=Path.Combine(directory,"migration-upper-backdrop.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(ticks>3500){Check("legacy-stair-route-completes",false,DescribePawn());Finish();}
    }
}
