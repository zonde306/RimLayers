using System.IO;
using System.Text.Json;
using RimLayers.Cargo;
using RimLayers.Ships;
using RimWorld;
using Verse;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void AddShipCargoFixture()
    {
        if(scenario!="gravship")return;
        var main=(Building_CargoHoist)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoManualShaft")),At(0,77,73),map);main.SetFaction(Faction.OfPlayer);
        foreach(int l in new[]{1,2})GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoShaftStation")),At(l,77,73),map).SetFaction(Faction.OfPlayer);
        var cargo=ThingMaker.MakeThing(ThingDefOf.Gold);cargo.stackCount=17;main.Contents.TryAdd(cargo,false);
        main.Motion.Position=1.25f;main.Motion.Destination=2;main.Motion.Moving=true;
        File.WriteAllText(Path.Combine(directory,"ship-hoist-baseline.json"),JsonSerializer.Serialize(new{main=main.ThingID,item=cargo.ThingID}));
    }
    private static void CheckCargoShipBoundary()
    {
        if(scenario!="gravship")return;
        foreach(var c in new CellRect(28,90,5,6))
        {
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);map.roofGrid.SetRoof(At(1,c.x,c.z),RoofDefOf.RoofConstructed);
            ShipDeckTerrain.Install(owner,At(2,c.x,c.z),false);
        }
        var core=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,At(2,30,92),map);core.SetFaction(Faction.OfPlayer);
        var main=(Building_CargoHoist)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoManualShaft")),At(1,32,94),map);main.SetFaction(Faction.OfPlayer);
        var port=(Building_CargoPort)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoShaftStation")),At(2,32,94),map);port.SetFaction(Faction.OfPlayer);
        var goods=ThingMaker.MakeThing(ThingDefOf.Gold);goods.stackCount=11;main.Contents.TryAdd(goods,false);
        owner.Ships.MarkDirty();owner.Ships.Tick();
        var capture=ShipLayers.Prepare(core,out var report);
        Check("cargo-cross-boundary-preview-classifies-entire-shaft",report.Accepted&&capture!=null&&capture.Straddlers.Contains(main)&&capture.Straddlers.Contains(port)&&!capture.Cells.Contains(main.Position)&&capture.Cells.Contains(port.Position),report.Reason);
        Check("cargo-cross-boundary-preview-keeps-real-goods",main.Spawned&&port.Spawned&&main.Contents.Contains(goods)&&goods.stackCount==11);
        if(capture!=null&&report.Accepted)
        {
            ShipSeparation.Commit(capture);
            Check("cargo-cross-boundary-commit-preserves-items-once",main.Destroyed&&port.Destroyed&&goods.stackCount==11&&
                map.listerThings.AllThings.Concat(owner.Recovery.GetDirectlyHeldThings()).Count(t=>t==goods)==1);
        }
        core.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
    }
    private static void VerifyShipCargo(IEnumerable<Thing> payload,string phase)
    {
        if(scenario!="gravship")return;
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"ship-hoist-baseline.json")));var d=json.RootElement;
        var all=payload.ToArray();
        var main=all.OfType<Building_CargoHoist>().Single(h=>h.ThingID==d.GetProperty("main").GetString());
        Check("cargo-ship-"+phase+"-single-physical-holder",main.Contents.Count==1&&main.Contents[0].ThingID==d.GetProperty("item").GetString()&&main.Contents[0].stackCount==17&&main.Contents[0].ParentHolder==main);
        Check("cargo-ship-"+phase+"-keeps-paused-motion",Math.Abs(main.Motion.Position-1.25f)<.001f&&main.Motion.Destination==2&&main.Motion.Moving);
        Check("cargo-ship-"+phase+"-keeps-three-port-references",all.OfType<Building_CargoPort>().Count(p=>p.Master==main)==3);
        if(main.Spawned)Check("cargo-ship-landed-shaft-is-connected",CargoNetwork.For(main.Map).Ports.Where(p=>p.Master==main).All(p=>CargoNetwork.For(main.Map).Connected(p)));
    }
}
