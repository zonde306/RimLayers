using HarmonyLib;
using RimLayers.Core;
using Verse;

namespace RimLayers.Environment;

internal static class GasBoundary
{
    internal static bool Logical(Map map,int index,out LayerMapComponent? owner,out IntVec3 cell)
    {
        owner=LayerMapComponent.For(map);cell=CellIndicesUtility.IndexToCell(index,map.Size.x);
        return owner==null || owner.TryDecode(cell,out _);
    }
    internal static void Changed(GasGrid grid,Map map,int index)
    {
        if(Logical(map,index,out var owner,out var cell) && owner!=null && (grid.GetDirect(index)&0xFFFFFF00u)!=0)
            owner.Air.Mark(cell);
    }
}
[HarmonyPatch(typeof(GasGrid),nameof(GasGrid.SetDirect),new[]{typeof(int),typeof(uint)})]
internal static class LayerPackedGasWritePatch
{
    static void Prefix(Map ___map,int index,ref uint packedDensities)
    { if(!GasBoundary.Logical(___map,index,out _,out _)) packedDensities=0; }
    static void Postfix(GasGrid __instance,Map ___map,int index)=>GasBoundary.Changed(__instance,___map,index);
}
[HarmonyPatch(typeof(GasGrid),nameof(GasGrid.SetDirect),new[]{typeof(int),typeof(byte),typeof(byte),typeof(byte),typeof(byte)})]
internal static class LayerGasChannelsWritePatch
{
    static void Prefix(Map ___map,int index,ref byte smoke,ref byte toxic,ref byte rotStink,ref byte deadlife)
    { if(!GasBoundary.Logical(___map,index,out _,out _)) smoke=toxic=rotStink=deadlife=0; }
    static void Postfix(GasGrid __instance,Map ___map,int index)=>GasBoundary.Changed(__instance,___map,index);
}
