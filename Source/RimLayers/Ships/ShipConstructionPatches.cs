using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
namespace RimLayers.Ships;

[HarmonyPatch(typeof(PlaceWorker_InSubstructureFootprint),nameof(PlaceWorker_InSubstructureFootprint.AllowsPlacing))]
internal static class ElevatedBasePlacement
{
    static bool Prefix(BuildableDef checkingDef,IntVec3 loc,Map map,ref AcceptanceReport __result)
    {
        if(checkingDef!=TerrainDefOf.Substructure||LayerMapComponent.For(map) is not {} o||
            !o.TryDecode(loc,out var l)||l.LayerId==0)return true;
        if(map.roofGrid.RoofAt(loc)?.isNatural==true){__result="MessageCannotPlaceUnderRockRoof".Translate();return false;}
        __result=o.Structure.Validate(checkingDef,loc,Rot4.North,true);return false;
    }
}
[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.GetConnectedSubstructure))]
internal static class NativeShipOwnership
{
    [HarmonyAfter("vanillaexpanded.gravship")]
    static bool Prefix(Building_GravEngine engine,HashSet<IntVec3> cells,int maxCells,bool requireInsideFootprint)
    {
        if(LayerMapComponent.For(engine.Map) is not {} o||!ShipStructureService.Core(engine))return true;
        cells.Clear();
        if(o.Ships.Get(engine) is not {} ship||!o.TryDecode(engine.Position,out var core))return false;
        foreach(var c in ship.Cells.Where(c=>o.TryDecode(c,out var at)&&at.LayerId==core.LayerId)
            .Where(c=>!requireInsideFootprint||o.Ships.InField(engine,c)).OrderBy(c=>(c-engine.Position).LengthHorizontalSquared).Take(maxCells))cells.Add(c);
        return false;
    }
}
[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.GetGizmos))]
internal static class ShipConstructionTarget
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result,Building_GravEngine __instance)
    {
        foreach(var g in __result)yield return g;
        if(LayerMapComponent.For(__instance.Map) is not {} o||!ShipStructureService.Core(__instance))yield break;
        yield return new Command_Action{defaultLabel="RL_ShipMigrateLegacy".Translate(),defaultDesc="RL_ShipMigrateLegacyHelp".Translate(),
            action=()=>ShipMigration.Open(o,__instance)};
        yield return new Command_Action{defaultLabel="RL_ShipSelectCore".Translate(),defaultDesc="RL_ShipSelectCoreHelp".Translate(),
            action=()=>{o.Ships.SelectedCore=__instance.thingIDNumber;Messages.Message("RL_ShipCoreSelected".Translate(__instance.LabelCap),MessageTypeDefOf.TaskCompletion,false);}};
    }
}
[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.Notify_FoundationDestroyed))]
internal static class DestroyedShipBaseRelease
{
    static void Postfix(IntVec3 c,Map ___map)
    {
        if(LayerMapComponent.For(___map) is {} o){o.Underlays.Release(c);o.Ships.Forget(c);}
    }
}

[HarmonyPatch(typeof(GenConstruct),nameof(GenConstruct.PlaceBlueprintForBuild))]
internal static class ShipBlueprintOwner
{
    static void Prefix(BuildableDef sourceDef,IntVec3 center,Map map,out int __state)
    {
        __state=0;
        if(sourceDef is not TerrainDef {IsSubstructure:true}||LayerMapComponent.For(map) is not {} o)return;
        __state=o.Ships.BuildOwner(center)?.thingIDNumber??0;
    }
    static void Postfix(Blueprint_Build __result,Map map,int __state)
    {
        if(__state!=0&&__result.Spawned)LayerMapComponent.For(map)?.Ships.Assign(__result.Position,__state);
    }
}
