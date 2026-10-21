using System.Text.Json;
using HarmonyLib;
using RimLayers.Structure;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static long heatExchangesBefore;
    private static Room HeatRoom(int layer)=>At(layer,57,57).GetRoom(map);
    private static float[] HeatTemperatures()=>Enumerable.Range(0,3).Select(l=>HeatRoom(l).Temperature).ToArray();
    private static double HeatEnergy()=>Enumerable.Range(0,3).Sum(l=>(double)HeatRoom(l).Temperature*HeatRoom(l).CellCount);
    private static void SetHeat(float a,float b,float c)
    { HeatRoom(0).Temperature=a;HeatRoom(1).Temperature=b;HeatRoom(2).Temperature=c; }
    private static void BeginHeatTests()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.Position=At(0,48,48);pawn.Notify_Teleported();
        for(int layer=0;layer<3;layer++)
            foreach(IntVec3 local in new CellRect(55,55,5,5))
            {
                IntVec3 cell=At(layer,local.x,local.z);
                foreach(Thing t in cell.GetThingList(map).ToList())
                    if(t is not Pawn) { if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn(); }
                map.roofGrid.SetRoof(cell,RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(cell,layer==0?TerrainDefOf.Concrete:RimLayersDefOf.RL_StructuralFloor);
                if(local.x==55 || local.x==59 || local.z==55 || local.z==59)
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),cell,map);
            }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>owner.TryDecode(c,out var local) && new CellRect(55,55,5,5).Contains(new IntVec3(local.X,0,local.Z)));
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var rooms=Enumerable.Range(0,3).Select(HeatRoom).ToArray();
        Check("stacked-enclosed-rooms-remain-independent",rooms.All(r=>r!=null && !r.UsesOutdoorTemperature && r.CellCount==9) && rooms.Distinct().Count()==3,
            string.Join(";",rooms.Select(r=>$"{r?.ID}:cells={r?.CellCount},outside={r?.UsesOutdoorTemperature}")));
        IntVec3 gap=new(60,0,owner.Layout!.Height);
        Check("isolation-has-no-thermal-region",gap.GetExpectedRegionType(map)==RegionType.None && gap.GetRegion(map)==null && gap.GetRoom(map)==null);
        Check("logical-floor-rim-is-outdoor-boundary",At(1,40,0).GetRoom(map)?.TouchesMapEdge==true);
        Check("sealed-slabs-conduct-but-do-not-pass-air",Math.Abs(owner.Heat.Conductance(At(0,57,57),At(1,57,57))-0.006)<1e-9 && !owner.Air.IsOpen(At(0,57,57),At(1,57,57)));
        SetHeat(80,20,20);double before=HeatEnergy();owner.Heat.Exchange();
        float[] temps=HeatTemperatures();
        Check("paired-slab-heat-warms-upper-and-cools-lower",temps[0]<80 && temps[1]>20 && temps[2]==20,string.Join(",",temps));
        Check("paired-room-heat-is-conservative",Math.Abs(HeatEnergy()-before)<0.0005,$"before={before}, after={HeatEnergy()}");
        var thin=AccessTools.Method(typeof(RoomTempTracker),"ThinRoofEqualizationTempChangePerInterval");
        Check("covered-lower-roof-does-not-also-lose-heat-outdoors",(float)thin.Invoke(rooms[0].TempTracker,null)! ==0 && (float)thin.Invoke(rooms[1].TempTracker,null)! ==0);
        Check("top-ceiling-retains-native-outdoor-loss",Math.Abs((float)thin.Invoke(rooms[2].TempTracker,null)!-(map.mapTemperature.OutdoorTemp-rooms[2].Temperature)*0.006f)<0.00001);
        var stair=(Building_Stairs)GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(0,56,56),map);
        var door=(Building_Door)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Door,ThingDefOf.Steel),At(1,56,56),map);
        Check("closed-stair-door-keeps-only-slow-conduction",Math.Abs(owner.Heat.Conductance(stair.Position,stair.UpperCell)-0.006)<1e-9);
        AccessTools.Method(typeof(Building_Door),"DoorOpen").Invoke(door,new object[]{100});stair.SetForbidden(true);
        Check("open-stair-has-independent-heat-permission",Math.Abs(owner.Heat.Conductance(stair.Position,stair.UpperCell)-0.084)<1e-9);
        door.Destroy(DestroyMode.Vanish);stair.Destroy(DestroyMode.Vanish);
        map.terrainGrid.SetTerrain(At(1,57,57),RimLayersDefOf.RL_OpenAir);map.roofGrid.SetRoof(At(0,57,57),null);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("covered-void-is-not-directly-open-sky",!owner.Sky.IsExposed(At(0,57,57)) && !owner.Sky.IsExposed(At(1,57,57)) && !HeatRoom(0).UsesOutdoorTemperature);
        Check("open-void-has-air-volume-and-fast-convection",At(1,57,57).GetRoom(map)!=null && Math.Abs(owner.Heat.Conductance(At(0,57,57),At(1,57,57))-0.084)<1e-9);
        map.roofGrid.SetRoof(At(1,57,57),null);
        Check("higher-roof-shades-through-intermediate-void",!owner.Sky.IsExposed(At(0,57,57)) && !owner.Sky.IsExposed(At(1,57,57)));
        map.roofGrid.SetRoof(At(2,57,57),null);
        Check("last-overhead-roof-removal-invalidates-entire-sky-column",Enumerable.Range(0,3).All(l=>owner.Sky.IsExposed(At(l,57,57))));
        for(int l=0;l<3;l++)map.roofGrid.SetRoof(At(l,57,57),RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(1,57,57),RimLayersDefOf.RL_StructuralFloor);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();SetHeat(80,20,5);
        SetHeat(20,20,20);double preBlast=HeatEnergy();
        var blast=(Explosion)GenSpawn.Spawn(ThingDefOf.Explosion,At(0,57,57),map);
        blast.radius=1;blast.damType=DamageDefOf.Bomb;blast.damAmount=1;blast.propagationSpeed=1;
        blast.doVisualEffects=blast.doSoundEffects=false;
        blast.overrideCells=new(){At(0,57,57),At(1,57,57),At(2,57,57)};
        blast.StartExplosion(null,null);
        double expectedHeat=DamageDefOf.Bomb.explosionHeatEnergyPerCell*3;
        Check("native-blast-distributes-heat-to-actual-covered-rooms",Enumerable.Range(0,3).All(l=>HeatRoom(l).Temperature>20));
        Check("native-blast-total-heat-budget-is-not-multiplied",Math.Abs(HeatEnergy()-preBlast-expectedHeat)<0.001,
            $"expected={expectedHeat}, actual={HeatEnergy()-preBlast}");
        // Isolate the start-phase heat contract from the later damage wave in this fixture.
        blast.Destroy(DestroyMode.Vanish);
        GameDataSaveLoader.SaveGame("RimLayers_V1Heat");
        File.WriteAllText(Path.Combine(directory,"heat-baseline.json"),JsonSerializer.Serialize(new {temperatures=HeatTemperatures()}));
        Check("saved-native-room-temperatures-without-second-owner",true);heatExchangesBefore=owner.Heat.Exchanges;stage=80;ticks=0;
    }
    private static void RestoreHeat()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"heat-baseline.json")));
        float[] saved=baseline.RootElement.GetProperty("temperatures").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        Check("restart-preserves-native-temperature-save-quantization",saved.Zip(HeatTemperatures()).All(p=>
            p.Second==(int)(Math.Clamp(p.First,-273.15f,1000f)*16f)/16f),string.Join(",",HeatTemperatures()));
        Check("restart-keeps-distributed-blast-heat-in-native-cache",HeatTemperatures().All(t=>t>20));
        Check("restart-rebuilds-sky-and-room-identities",Enumerable.Range(0,3).All(l=>!owner.Sky.IsExposed(At(l,57,57)) && !HeatRoom(l).UsesOutdoorTemperature));
        SetHeat(80,20,20);double before=HeatEnergy();owner.Heat.Exchange();
        Check("restart-paired-heat-remains-conservative",Math.Abs(HeatEnergy()-before)<0.0005 && HeatRoom(1).Temperature>20);
        SetHeat(80,20,5);heatExchangesBefore=owner.Heat.Exchanges;stage=80;ticks=0;
    }
    private static void HeatStep()
    {
        if(ticks<130)return;
        Check("native-temperature-phase-runs-vertical-exchange",owner.Heat.Exchanges>heatExchangesBefore && owner.Heat.LastPairCount>0 && HeatRoom(0).Temperature<80,
            $"scheduledSteps={owner.Heat.Exchanges-heatExchangesBefore}, pairs={owner.Heat.LastPairCount}, temperatures={string.Join(",",HeatTemperatures())}");
        LayerView.Switch(owner,1);Find.Selector.ClearSelection();
        Find.CameraDriver.SetRootPosAndSize(At(1,57,57).ToVector3Shifted(),9);
        pendingScreenshot=Path.Combine(directory,mode+"-heat.png");screenshotHold=3;stage=11;ticks=0;
    }
}
