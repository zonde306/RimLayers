using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
namespace RimLayers.Ships;

/// <summary>
/// TerrainDef.Removable is literally `layerable`, and both RimLayers ship decks are
/// isFoundation=true + layerable=false. Native CanRemoveFoundationAt therefore returns false and the
/// old postfix (guarded by `__result &&`) could never run: the deck was permanently unremovable.
/// This prefix owns the verdict for our own deck cells only; every native guard (no roof above, no
/// pawn/building/item inside, a supported neighbour) still lives in CanDismantleSlab.
/// </summary>
[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.CanRemoveFoundationAt))]
internal static class ShipFoundationRemovalAvailability
{
    static bool Prefix(IntVec3 c,Map ___map,ref bool __result)
    {
        if(LayerMapComponent.For(___map) is not {} o)return true;
        if(___map.terrainGrid.FoundationAt(c) is not {IsSubstructure:true} foundation)return true;
        if(!o.TryDecode(c,out var layer)||layer.LayerId<=0)return true;
        if(foundation!=ShipDeckTerrain.Def&&!o.StructuralCells.Contains(c))return true;
        __result=o.Structure.CanDismantleSlab(c);
        return false;
    }
}

[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.RemoveFoundation))]
internal static class ShipFoundationRemovalCommit
{
    static bool Prefix(TerrainGrid __instance,IntVec3 c,Map ___map,bool doLeavings)
    {
        if(!doLeavings||__instance.FoundationAt(c)?.IsSubstructure!=true||LayerMapComponent.For(___map) is not {} o)return true;
        if(!o.TryDecode(c,out var logical)||logical.LayerId==0)return true;
        if(!o.Structure.CanDismantleSlab(c))return false;
        var slab=__instance.FoundationAt(c);
        var refund=GenAdj.AdjacentCells.Select(d=>c+d).First(p=>o.Structure.HasSurface(p)&&p.Standable(___map));
        __instance.RemoveFoundation(c,false);
        foreach(var cost in slab.CostListAdjusted(null))
        {int count=GenMath.RoundRandom(cost.count*slab.resourcesFractionWhenDeconstructed);if(count==0)continue;var item=ThingMaker.MakeThing(cost.thingDef);item.stackCount=count;o.Returns.Place(item,refund);}
        return false;
    }
    static void Postfix(IntVec3 c,Map ___map)
    {
        var o=LayerMapComponent.For(___map);if(o==null)return;
        if(___map.terrainGrid.FoundationAt(c)==null&&___map.terrainGrid.TopTerrainAt(c)!=RimLayersDefOf.RL_StructuralFloor&&___map.terrainGrid.UnderTerrainAt(c)!=RimLayersDefOf.RL_StructuralFloor)o.StructuralCells.Remove(c);
        o.map.pathing.RecalculatePerceivedPathCostAt(c);
        o.Structure.RoofChanged(c);o.Invalidate(c,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Light);
    }
}
