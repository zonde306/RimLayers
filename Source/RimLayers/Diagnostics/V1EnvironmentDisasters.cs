using HarmonyLib;
using RimLayers.Core;
using RimLayers.Environment;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void EnvironmentPowerIsolation()
    {
        foreach(var wire in EnvAt(-1,86,52).GetThingList(map).Where(t=>t.def.defName=="PowerConduit").ToArray())wire.Destroy(DestroyMode.Vanish);
        var basementPort=(Building)GenSpawn.Spawn(RimLayersDefOf.RL_PowerRiser,EnvAt(-1,86,52),map);basementPort.SetFaction(Faction.OfPlayer);
        var surface=EnvAt(0,86,52);map.roofGrid.SetRoof(surface,RoofDefOf.RoofConstructed);
        var surfacePort=(Building)GenSpawn.Spawn(RimLayersDefOf.RL_PowerRiser,surface,map);surfacePort.SetFaction(Faction.OfPlayer);
        map.powerNetManager.UpdatePowerNetsAndConnections_First();
        Check("exposed-node-connects-to-same-native-network",surfacePort.PowerComp.PowerNet==environmentLoad.PowerComp.PowerNet);
        Check("one-unshielded-node-interferes-with-whole-grid",!SolarShield.Protected(environmentLoad.PowerComp.PowerNet));
        for(int i=0;i<25;i++)Find.TickManager.DoSingleTick();
        Check("native-flare-actually-disables-connected-lamp",!environmentLoad.GetComp<CompPowerTrader>().PowerOn);
        surfacePort.GetComp<CompFlickable>().SwitchIsOn=false;map.powerNetManager.UpdatePowerNetsAndConnections_First();
        Check("completed-native-net-rebuild-restores-shielding",SolarShield.Protected(environmentLoad.PowerComp.PowerNet));
        for(int i=0;i<210;i++)Find.TickManager.DoSingleTick();
        Check("native-isolated-grid-powers-lamp-during-flare",environmentLoad.GetComp<CompPowerTrader>().PowerOn&&map.gameConditionManager.ElectricityDisabled(map));
        var def=new GameConditionDef{defName="RL_DiagnosticOtherBlackout",label="diagnostic blackout",conditionClass=typeof(GameCondition_DisableElectricity)};
        var blackout=GameConditionMaker.MakeCondition(def,60000);map.gameConditionManager.RegisterCondition(blackout);
        Check("other-blackout-does-not-get-solar-exemption",SolarShield.Disabled(map.gameConditionManager,map,environmentLoad.PowerComp.PowerNet));
        blackout.End();
    }
    private static void EnvironmentHydrology()
    {
        if(!ModsConfig.OdysseyActive)return;
        var source=EnvAt(0,73,73);var target=EnvAt(-1,73,73);
        environmentDoor.Destroy(DestroyMode.Vanish);
        var floodBase=DefDatabase<TerrainDef>.AllDefsListForReading.First(t=>t.floodTerrain!=null);
        map.terrainGrid.SetTerrain(source,floodBase);
        foreach(var offset in GenAdj.CardinalDirections)
            if((source+offset).GetEdifice(map) is not Building)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),source+offset,map).SetFaction(Faction.OfPlayer);
        var flood=(Flood)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("TorrentialRainFlood"),source,map);
        var weights=(HashSet<IntVec3>[])AccessTools.Field(typeof(Flood),"openCellWeights").GetValue(flood)!;
        void Frontier()
        {
            foreach(var set in weights)set.Clear();weights[^1].Add(source);
            AccessTools.Field(typeof(Flood),"noPossibleCell").SetValue(flood,false);
            AccessTools.Field(typeof(Flood),"floodedTileCount").SetValue(flood,0);
            AccessTools.Field(typeof(Flood),"estimatedFloodedTiles").SetValue(flood,100);
        }
        var spread=AccessTools.Method(typeof(Flood),"SpreadFlood",Type.EmptyTypes);
        RimLayersMod.Settings.Environment.FloodDown=false;Frontier();spread.Invoke(flood,null);
        Check("disabled-downward-flood-leaves-basement-dry",map.terrainGrid.TempTerrainAt(target)==null);
        RimLayersMod.Settings.Environment.FloodDown=true;Frontier();spread.Invoke(flood,null);
        Check("one-downward-flood-cell-consumes-one-native-budget",map.terrainGrid.TempTerrainAt(target)!=null&&(int)AccessTools.Field(typeof(Flood),"floodedTileCount").GetValue(flood)! ==1);
        flood.Destroy(DestroyMode.Vanish);
        Check("flood-retreat-keeps-native-delayed-terrain-cleanup",map.terrainGrid.TempTerrainAt(target)!=null);
        // Use an independent open shaft so water and lava never overwrite each other's fixture state.
        var lavaSource=EnvAt(0,74,74);var lavaTarget=EnvAt(-1,74,74);
        EnvClear(lavaSource,TerrainDefOf.VolcanicRock,null);EnvClear(lavaTarget,DefDatabase<ThingDef>.GetNamed("Granite").building.naturalTerrain,RoofDefOf.RoofRockThick);
        GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("RL_StairsDown"),lavaSource,map);
        foreach(var offset in GenAdj.CardinalDirections)
        {
            var at=lavaSource+offset;if(at==source)continue;
            if(at.GetEdifice(map)==null)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),at,map).SetFaction(Faction.OfPlayer);
        }
        var condition=(GameCondition_LavaFlow)GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("LavaFlow"),60000);
        map.gameConditionManager.RegisterCondition(condition);
        var open=(HashSet<IntVec3>)AccessTools.Field(typeof(GameCondition_LavaFlow),"openCells").GetValue(condition)!;
        open.Clear();open.Add(lavaSource);
        var advance=AccessTools.Method(typeof(GameCondition_LavaFlow),"SpreadLava");
        RimLayersMod.Settings.Environment.LavaDown=false;advance.Invoke(condition,null);
        Check("disabled-downward-lava-leaves-basement-unchanged",map.terrainGrid.TempTerrainAt(lavaTarget)==null);
        RimLayersMod.Settings.Environment.LavaDown=true;open.Clear();open.Add(lavaSource);advance.Invoke(condition,null);
        Check("lava-downward-step-retains-one-native-frontier",open.Count==2&&map.terrainGrid.TempTerrainAt(lavaTarget)==TerrainDefOf.LavaShallow);
        condition.End();map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
    }
}
