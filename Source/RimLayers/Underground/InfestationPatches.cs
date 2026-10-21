using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Underground;

internal static class InfestationGeometry
{
    [ThreadStatic] internal static Map? Calculating;
    [ThreadStatic] internal static (Map Map,CellRect Bounds)? Scope;
    internal static bool InBounds(IntVec3 cell,Map map)=>cell.InBounds(map)&&
        (Scope is not {} s||s.Map!=map||s.Bounds.Contains(cell));
}

// Keep native scores and incident scheduling; only their planar geometry is layer-local.
[HarmonyPatch(typeof(InfestationCellFinder),"CalculateLocationCandidates")]
internal static class InfestationCandidatesScope
{
    static void Prefix(Map map,out Map? __state)
    {__state=InfestationGeometry.Calculating;InfestationGeometry.Calculating=LayerMapComponent.For(map)!=null?map:null;}
    static Exception? Finalizer(Map? __state,Exception? __exception)
    {InfestationGeometry.Calculating=__state;return __exception;}
}
[HarmonyPatch(typeof(CellFinderUtility),nameof(CellFinderUtility.CalculateDistanceToColonyBuildingGrid))]
internal static class InfestationColonyDistance
{
    static bool Prefix(Map map)
    {
        if(InfestationGeometry.Calculating!=map||LayerMapComponent.For(map) is not {} owner)return true;
        var field=AccessTools.Field(typeof(CellFinderUtility),"distToColonyBuilding");
        var grid=(ByteGrid?)field.GetValue(null);
        if(grid==null){grid=new ByteGrid(map);field.SetValue(null,grid);}
        else if(!grid.MapSizeMatches(map))grid.ClearAndResizeTo(map);
        grid.Clear(byte.MaxValue);
        var roots=map.listerBuildings.allBuildingsColonist.Where(b=>!b.IsClearableFreeBuilding&&owner.TryDecode(b.Position,out _)).Select(b=>b.Position);
        IEnumerable<IntVec3> Neighbors(IntVec3 cell)
        {
            if(!owner.TryDecode(cell,out var here))yield break;
            foreach(var d in GenAdj.AdjacentCells)
                if(owner.TryDecode(cell+d,out var there)&&there.LayerId==here.LayerId)yield return cell+d;
        }
        var distances=new Dictionary<IntVec3,float>();
        Dijkstra<IntVec3>.Run(roots,Neighbors,(a,b)=>a.x==b.x||a.z==b.z?1f:1.4142135f,distances);
        foreach(var pair in distances)grid[pair.Key]=(byte)Math.Min(pair.Value,254.999f);
        return false;
    }
}
[HarmonyPatch]
internal static class InfestationLocalNeighborhood
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(string name in new[]{"GetScoreAt","GetMountainousnessScoreAt","StraightLineDistToUnroofed","DistToBlocker","NoRoofAroundAndWalkable"})
            yield return AccessTools.Method(typeof(InfestationCellFinder),name);
    }
    static void Prefix(IntVec3 cell,Map map,out (Map Map,CellRect Bounds)? __state)
    {
        __state=InfestationGeometry.Scope;
        if(LayerMapComponent.For(map) is {} owner&&owner.TryDecode(cell,out var local))
            InfestationGeometry.Scope=(map,owner.LayerRect(local.LayerId));
    }
    static Exception? Finalizer((Map Map,CellRect Bounds)? __state,Exception? __exception)
    {InfestationGeometry.Scope=__state;return __exception;}
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native=AccessTools.Method(typeof(GenGrid),nameof(GenGrid.InBounds),new[]{typeof(IntVec3),typeof(Map)});
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(native)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(InfestationGeometry),nameof(InfestationGeometry.InBounds));}
            yield return instruction;
        }
    }
}
[HarmonyPatch(typeof(InfestationCellFinder),"GetScoreAt")]
internal static class InfestationValidLayer
{
    static bool Prefix(IntVec3 cell,Map map,ref float __result)
    {
        if(LayerMapComponent.For(map) is not {} owner||owner.TryDecode(cell,out _))return true;
        __result=0;return false;
    }
}
