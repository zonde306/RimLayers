using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using Verse;
using Verse.AI;
using RimWorld;
namespace RimLayers.Ships;

// These native consumers bypass ValidSubstructureAt and read only the engine's
// 2D deck. Keep their original role, timing, visitor and completion logic.
[HarmonyPatch]
internal static class ShipBoardingMembership
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StageEndTrigger_RoleArrivedAndSpectatorsOnSubstructure),"ArrivedCheck");
        yield return AccessTools.Method(typeof(JobDriver_BoardOrLeaveGravship),"CheckFailedToBoardOrLeave");
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source,MethodBase __originalMethod)
    {
        var getter=AccessTools.PropertyGetter(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructure));
        var contains=AccessTools.Method(typeof(HashSet<IntVec3>),nameof(HashSet<IntVec3>.Contains));
        var at=AccessTools.Method(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructureAt));
        int gets=0,calls=0;
        foreach(var code in source)
        {
            if(code.Calls(getter)){code.opcode=OpCodes.Nop;code.operand=null;gets++;}
            else if(code.Calls(contains)){code.opcode=OpCodes.Callvirt;code.operand=at;calls++;}
            yield return code;
        }
        int expected=__originalMethod.DeclaringType==typeof(StageEndTrigger_RoleArrivedAndSpectatorsOnSubstructure)?3:1;
        if(gets!=expected||calls!=expected)throw new InvalidOperationException($"RimLayers boarding anchors changed in {__originalMethod}: {gets}/{calls}, expected {expected}.");
    }
}

internal static class ShipBoardingSpots
{
    internal static bool TryFind(Pawn pawn,Building_GravEngine engine,bool onboard,out IntVec3 spot)
    {
        spot=IntVec3.Invalid;
        if(!ModsConfig.OdysseyActive||pawn.MapHeld!=engine.Map||!ShipLayers.Eligible(engine)||LayerMapComponent.For(engine.Map) is not {} owner||owner.Ships.Get(engine) is not {Ambiguous:false} ship)return false;
        IEnumerable<IntVec3> cells=onboard?ship.Cells:owner.map.AllCells.Where(c=>owner.TryDecode(c,out _)&&owner.Structure.HasSurface(c));
        owner.TryDecode(pawn.PositionHeld,out var start);
        var ordered=cells.Where(c=>c.Standable(owner.map)&&engine.ValidSubstructureAt(c)==onboard)
            .OrderBy(c=>{owner.TryDecode(c,out var at);return Math.Abs(at.X-start.X)+Math.Abs(at.Z-start.Z)+owner.Layout!.Distance(at.LayerId,start.LayerId)*4;}).ToArray();
        // Native boarding prefers an indoor space, then permits an outdoor deck.
        for(int pass=0;pass<(onboard?2:1);pass++)
            foreach(var cell in ordered)
            {
                if(onboard&&pass==0&&cell.GetRoom(owner.map)?.PsychologicallyOutdoors!=false)continue;
                if(pawn.CanReach(cell,PathEndMode.OnCell,Danger.Deadly)){spot=cell;return true;}
            }
        return false;
    }
}
[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.TryFindSpotOnGravship))]
internal static class ShipBoardingSpotFallback
{
    static void Postfix(Pawn pawn,Building_GravEngine engine,ref IntVec3 spot,ref bool __result)
    {if(!__result&&ShipBoardingSpots.TryFind(pawn,engine,true,out var found)){spot=found;__result=true;}}
}
[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.TryFindSpotOffGravship))]
internal static class ShipLeavingSpotFallback
{
    static void Postfix(Pawn pawn,Building_GravEngine engine,ref IntVec3 spot,ref bool __result)
    {if(!__result&&ShipBoardingSpots.TryFind(pawn,engine,false,out var found)){spot=found;__result=true;}}
}
