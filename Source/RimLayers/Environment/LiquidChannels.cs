using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

internal static class LiquidChannels
{
    internal static IntVec3 Down(LayerMapComponent owner,IntVec3 source,bool enabled)
    {
        var lower=owner.Adjacent(source,-1);
        return enabled&&lower.IsValid&&VolumeLinks.Open(owner,lower,source,LayerChangeKind.Liquid)?lower:IntVec3.Invalid;
    }
    internal static bool DryDestination(LayerMapComponent owner,IntVec3 cell)=>owner.TryDecode(cell,out _)&&
        owner.Structure.HasSurface(cell)&&!cell.GetTerrain(owner.map).IsWater&&owner.map.terrainGrid.FoundationAt(cell)==null&&
        (cell.GetEdifice(owner.map)==null||cell.GetEdifice(owner.map) is Building_Stairs)&&
        cell.GetTerrain(owner.map).defName is not ("RL_LavaDeep" or "RL_LavaShallow" or "LavaDeep" or "LavaShallow");
}
[HarmonyPatch(typeof(Flood),"SpreadFlood",new Type[0])]
internal static class LayerFloodFrontier
{
    private static readonly Func<Flood,(IntVec3,int)> Next=AccessTools.Method(typeof(Flood),"GetNextCell").CreateDelegate<Func<Flood,(IntVec3,int)>>();
    private static readonly Func<Flood,IntVec3,bool> Potential=AccessTools.Method(typeof(Flood),"CanFloodPotentiallySpreadInto").CreateDelegate<Func<Flood,IntVec3,bool>>();
    private static readonly Func<Flood,IntVec3,bool> CanSpread=AccessTools.Method(typeof(Flood),"CanFloodSpreadInto").CreateDelegate<Func<Flood,IntVec3,bool>>();
    static bool Prefix(Flood __instance,HashSet<IntVec3>[] ___openCellWeights,ref int ___floodedTileCount,ref bool ___noPossibleCell)
    {
        var owner=LayerMapComponent.For(__instance.Map);if(owner?.Environment.Enabled!=true||!LayerEventPolicy.KnownLiquid(__instance))return true;
        var native=AccessTools.Method(__instance.GetType(),"SpreadFlood",new[]{typeof(IntVec3),typeof(TerrainDef)});
        while(true)
        {
            var (source,width)=Next(__instance);
            if(!source.IsValid){___noPossibleCell=true;break;}
            if(!owner.TryDecode(source,out var local)){___openCellWeights[width-1].Remove(source);continue;}
            var terrain=owner.map.terrainGrid.TempTerrainAt(source);
            if(terrain?.floodTerrain==null)terrain=owner.map.terrainGrid.BaseTerrainAt(source);
            var neighbors=new List<IntVec3>();
            var down=LiquidChannels.Down(owner,source,LayerEnvironment.Settings.FloodDown);
            if(down.IsValid)neighbors.Add(down);
            neighbors.AddRange(GenAdj.CardinalDirections.Select(d=>source+d).Where(c=>owner.TryDecode(c,out var q)&&q.LayerId==local.LayerId));
            bool hasPotential=false,spread=false;
            foreach(var cell in neighbors)
            {
                bool vertical=cell==down;
                bool potential=Potential(__instance,cell);
                hasPotential|=potential;
                bool legal=vertical?potential&&LiquidChannels.DryDestination(owner,cell):CanSpread(__instance,cell);
                if(!legal||terrain.floodTerrain==null)continue;
                int remaining=width-1;
                if(remaining>0)___openCellWeights[remaining-1].Add(cell);
                native.Invoke(__instance,new object[]{cell,terrain});
                ___floodedTileCount++;spread=true;break;
            }
            if(!hasPotential)___openCellWeights[width-1].Remove(source);
            if(spread)break;
            // Native code otherwise loops forever when all potential cells are blocked by buildings.
            if(hasPotential){___openCellWeights[width-1].Remove(source);}
        }
        return false;
    }
}
[HarmonyPatch(typeof(GameCondition_LavaFlow),"SpreadLava")]
internal static class LayerLavaFrontier
{
    private static readonly Func<GameCondition_LavaFlow,IntVec3,bool> Potential=AccessTools.Method(typeof(GameCondition_LavaFlow),"CanLavaPotentiallySpreadInto").CreateDelegate<Func<GameCondition_LavaFlow,IntVec3,bool>>();
    private static readonly Func<GameCondition_LavaFlow,IntVec3,bool> CanSpread=AccessTools.Method(typeof(GameCondition_LavaFlow),"CanLavaSpreadInto").CreateDelegate<Func<GameCondition_LavaFlow,IntVec3,bool>>();
    private static readonly Func<GameCondition_LavaFlow,IntVec3,int> Adjacent=AccessTools.Method(typeof(GameCondition_LavaFlow),"AdjacentLavaCells").CreateDelegate<Func<GameCondition_LavaFlow,IntVec3,int>>();
    private static readonly Func<GameCondition,Map> SingleMap=AccessTools.PropertyGetter(typeof(GameCondition),"SingleMap").CreateDelegate<Func<GameCondition,Map>>();
    private static readonly Func<GameCondition_LavaFlow,int> Duration=AccessTools.PropertyGetter(typeof(GameCondition_LavaFlow),"FlowDurationTicks").CreateDelegate<Func<GameCondition_LavaFlow,int>>();
    static bool Prefix(GameCondition_LavaFlow __instance,HashSet<IntVec3> ___openCells)
    {
        var map=SingleMap(__instance);var owner=LayerMapComponent.For(map);
        if(owner?.Environment.Enabled!=true||!LayerEventPolicy.KnownLiquid(__instance))return true;
        if(___openCells.Count==0)return false;
        var source=___openCells.RandomElementByWeight(c=>Math.Max(Adjacent(__instance,c),1)*4);
        if(!owner.TryDecode(source,out var local)){___openCells.Remove(source);return false;}
        var down=LiquidChannels.Down(owner,source,LayerEnvironment.Settings.LavaDown);
        var neighbors=new List<IntVec3>();if(down.IsValid)neighbors.Add(down);
        var cardinal=GenAdj.CardinalDirections.ToList();cardinal.Shuffle();
        neighbors.AddRange(cardinal.Select(d=>source+d).Where(c=>owner.TryDecode(c,out var q)&&q.LayerId==local.LayerId));
        bool hasPotential=false;
        foreach(var cell in neighbors)
        {
            bool vertical=cell==down;
            bool downstream=vertical||owner.Elevation(local.LayerId)<0;
            bool potential=downstream?!___openCells.Contains(cell)&&LiquidChannels.DryDestination(owner,cell):Potential(__instance,cell);
            hasPotential|=potential;
            if(!potential||!downstream&&!CanSpread(__instance,cell))continue;
            map.terrainGrid.SetTempTerrain(cell,TerrainDefOf.LavaShallow);
            map.tempTerrain.QueueRemoveTerrain(cell,Find.TickManager.TicksGame+Duration(__instance)+60000);
            ___openCells.Add(cell);break;
        }
        if(!hasPotential)___openCells.Remove(source);
        return false;
    }
}

[HarmonyPatch]
internal static class FloodDownwardCandidate
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Flood),"CellCanSpreadFlood");
        yield return AccessTools.Method(typeof(Flood),"CellCanPotentiallySpreadFlood");
    }
    private static readonly Func<Flood,IntVec3,bool> Potential=AccessTools.Method(typeof(Flood),"CanFloodPotentiallySpreadInto").CreateDelegate<Func<Flood,IntVec3,bool>>();
    static void Postfix(Flood __instance,IntVec3 cell,ref bool __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(__result||owner?.Environment.Enabled!=true||!LayerEventPolicy.KnownLiquid(__instance))return;
        var down=LiquidChannels.Down(owner,cell,LayerEnvironment.Settings.FloodDown);
        if(down.IsValid&&Potential(__instance,down)&&LiquidChannels.DryDestination(owner,down))__result=true;
    }
}


[HarmonyPatch]
internal static class LocalFloodSources
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SeasonalFlood),"GetInitialCells");
        yield return AccessTools.Method(typeof(TorrentialRainFlood),"GetInitialCells");
    }
    static void Postfix(Flood __instance,Map map,ref IEnumerable<(IntVec3,int)> __result)
    {
        var owner=LayerMapComponent.For(map);if(owner?.Environment.Enabled!=true||!LayerEventPolicy.KnownLiquid(__instance))return;
        bool rain=__instance is TorrentialRainFlood;
        __result=__result.Where(p=>owner.TryDecode(p.Item1,out _)&&(!rain||owner.Environment.Sky(p.Item1)));
    }
}
[HarmonyPatch(typeof(GameCondition_LavaFlow),"GetInitialCells")]
internal static class LogicalLavaFlowSources
{
    static bool Prefix(GameCondition_LavaFlow __instance,Map map,ref IEnumerable<IntVec3> __result)
    {
        var owner=LayerMapComponent.For(map);if(owner?.Environment.Enabled!=true||!LayerEventPolicy.KnownLiquid(__instance))return true;
        __result=Cells(owner);return false;
    }
    private static IEnumerable<IntVec3> Cells(LayerMapComponent owner)
    {
        int edge=Rot4.Random.AsInt;
        int count=((IntRange)AccessTools.Field(typeof(GameCondition_LavaFlow),"StartPositionCountRange").GetValue(null)!).RandomInRange;
        var layout=owner.Layout!;
        var candidates=new List<IntVec3>();
        for(int n=0;n<(edge%2==0?layout.Width:layout.Height);n++)
        {
            int x=edge==1?layout.Width-1:edge==3?0:n;
            int z=edge==0?layout.Height-1:edge==2?0:n;
            foreach(int layer in layout.OrderedLayers.Reverse())
            {
                if(owner.Elevation(layer)<0)continue;
                var c=owner.ToStorage(new(layer,x,z));
                var terrain=c.GetTerrain(owner.map);
                if((terrain==TerrainDefOf.VolcanicRock||terrain==TerrainDefOf.CooledLava)&&!c.Fogged(owner.map)&&
                    c.GetEdifice(owner.map)==null&&owner.Environment.Sky(c)){candidates.Add(c);break;}
            }
        }
        for(int i=0;i<count&&candidates.Count>0;i++)yield return candidates.RandomElement();
    }
}
