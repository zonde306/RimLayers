using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Ships;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>Optional bindings only; neither VGE assembly is a compile-time dependency.</summary>
internal static class VanillaGravshipCompatibility
{
    internal static bool Active { get; private set; }
    internal static bool Chapter2 { get; private set; }
    private static FieldInfo? capturedShip, capturedColors, ritualState;
    private static MethodInfo? executeLaunch, isJammed;
    [ThreadStatic] private static bool confirmedLaunch;
    internal static bool SelectingRitual=>Active&&ritualState?.GetValue(null)!=null;
    internal static bool EngineDef(ThingDef def)=>def==ThingDefOf.GravEngine ||
        Active && def.defName is "VGE_GravjumperEngine" or "VGE_GravhulkEngine";
    internal static IEnumerable<Building_GravEngine> Engines(Map map)=>
        Active?map.listerThings.AllThings.OfType<Building_GravEngine>().Where(e=>EngineDef(e.def)):
        map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).OfType<Building_GravEngine>();
    internal static bool Scaffold(TerrainDef? def)=>Active&&def?.defName=="VGE_GravshipSubscaffold";
    internal static int SupportCost(Map map,IEnumerable<IntVec3> cells)=>!Active?cells.Count():cells.Count(c=>!Scaffold(map.terrainGrid.FoundationAt(c)));

    internal static void Install(Harmony harmony)
    {
        Active=ModsConfig.IsActive("vanillaexpanded.gravship");
        Chapter2=Active&&ModsConfig.IsActive("vanillaexpanded.gravship2");
        if(!Active)return;
        Type Required(string name)=>AccessTools.TypeByName("VanillaGravshipExpanded."+name) ??
            throw new InvalidOperationException("[RimLayers] VGE type not found: "+name);
        MethodInfo Method(string type,string name)=>AccessTools.Method(Required(type),name) ??
            throw new InvalidOperationException("[RimLayers] VGE method not found: "+type+"."+name);
        HarmonyMethod Hook(string name)=>new(typeof(VanillaGravshipCompatibility),name);
        ritualState=AccessTools.Field(Required("Dialog_BeginRitual_ShowRitualBeginWindow_Patch"),"state");
        var capture=Required("Gravship_CopyCellContents");
        capturedShip=AccessTools.Field(capture,"currentGravship");
        capturedColors=AccessTools.Field(capture,"tempVacBarrierRoofColors");
        executeLaunch=Method("GravshipUtility_PreLaunchConfirmation_Patch","ExecuteGravshipLaunch");
        harmony.Patch(executeLaunch,prefix:Hook(nameof(BeforeRitualDeparture)));
        harmony.Patch(AccessTools.PropertyGetter(Required("ExtraGravshipDataComp"),"VacBarrierRoofColors"),
            postfix:Hook(nameof(ProjectRoofColors)));
        harmony.Patch(AccessTools.PropertyGetter(Required("CompPowerPlantGravEngine"),"DesiredPowerOutput"),
            transpiler:Hook(nameof(CountAllDecks)));
        // VGE's 2D scaffold flood must not cross ownership or charge the wrong deck.
        harmony.Patch(Method("GravshipUtility_GetConnectedSubstructure_Patch","GetConnectedSubstructure"),
            prefix:Hook(nameof(ScaffoldAllowance)));
        if(Chapter2)
        {
            var type=AccessTools.TypeByName("VanillaGravshipExpanded2.Designator_MoveGravship_IsValidCell_Patch")!;
            isJammed=AccessTools.Method(type,"IsCellJammed");
            harmony.Patch(isJammed,transpiler:Hook(nameof(JammerDistance)));
        }
        Log.Message("[RimLayers] Optional VGE compatibility enabled (Chapter 2: "+Chapter2+"). VGE single-engine and resource rules retained.");
    }
    internal static void NormalizeRoofColors(Gravship ship,ShipCapture capture)
    {
        if(!Active||capturedShip?.GetValue(null)!=ship||
            capturedColors?.GetValue(null) is not Dictionary<IntVec3,Color> colors)return;
        var entries=colors.ToArray();colors.Clear();
        foreach(var pair in entries)colors.Add(capture.Encode(pair.Key),pair.Value);
    }
    private static void ProjectRoofColors(WorldObjectComp __instance,ref IEnumerable<(IntVec3,Color)> __result)
    {
        if(__instance.parent is Gravship ship&&ShipLayers.TryData(ship,out _))
            __result=ShipLayers.Project(ship,__result);
    }
    private static bool BeforeRitualDeparture(Building_GravEngine engine,PlanetTile tile)
    {
        if(confirmedLaunch||!ShipLayers.Eligible(engine)||LayerMapComponent.For(engine.Map)==null)return true;
        ShipSeparation.Confirm(engine,()=>
        {
            if(!LayerShipDestination.TryPrepare(engine,tile))return;
            try { confirmedLaunch=true;executeLaunch!.Invoke(null,new object[]{engine,tile}); }
            catch
            {
                if(!WorldComponent_GravshipController.CutsceneInProgress&&Find.CurrentGravship?.Engine!=engine)
                    LayerExpansionQuota.Current?.Release(engine.thingIDNumber);
                throw;
            }
            finally { confirmedLaunch=false; }
        });
        return false;
    }
    private static bool ScaffoldAllowance(Building_GravEngine engine,ref int maxCells,bool requireInsideFootprint)
    {
        if(LayerMapComponent.For(engine.Map) is not {} owner||!ShipStructureService.Core(engine))return true;
        if(owner.Ships.Get(engine) is {} ship&&owner.TryDecode(engine.Position,out var core))
            maxCells+=ship.Cells.Count(c=>owner.TryDecode(c,out var p)&&p.LayerId==core.LayerId&&
                Scaffold(engine.Map.terrainGrid.FoundationAt(c))&&(!requireInsideFootprint||owner.Ships.InField(engine,c)));
        return false;
    }
    private static HashSet<IntVec3> PowerDecks(Building_GravEngine engine)=>
        LayerMapComponent.For(engine.Map)?.Ships.Get(engine) is {} ship ? ship.Cells : engine.ValidSubstructure;
    private static IEnumerable<CodeInstruction> CountAllDecks(IEnumerable<CodeInstruction> instructions)
    {
        var getter=AccessTools.PropertyGetter(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructure));
        foreach(var code in instructions)
        {
            if(code.Calls(getter)){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(VanillaGravshipCompatibility),nameof(PowerDecks));}
            yield return code;
        }
    }
    internal static AcceptanceReport LandingCell(IntVec3 cell,Map map)=>
        isJammed?.Invoke(null,new object[]{cell,map}) is true?"VGE_DestinationJammed".Translate():true;
    private static int LogicalDistanceSquared(IntVec3 a,IntVec3 b,Map map)
    {
        if(LayerMapComponent.For(map) is {} owner&&owner.TryDecode(a,out var x)&&owner.TryDecode(b,out var y))
            return (x.X-y.X)*(x.X-y.X)+(x.Z-y.Z)*(x.Z-y.Z);
        return a.DistanceToSquared(b);
    }
    private static IEnumerable<CodeInstruction> JammerDistance(IEnumerable<CodeInstruction> instructions)
    {
        // Preserve VGE power/stun/range policy; only replace flattened storage distance.
        foreach(var code in instructions)
        {
            if(code.Calls(AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceToSquared),new[]{typeof(IntVec3),typeof(IntVec3)})))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(VanillaGravshipCompatibility),nameof(LogicalDistanceSquared));
            }
            yield return code;
        }
    }
}
