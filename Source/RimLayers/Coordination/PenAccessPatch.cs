using System.Reflection;
using System.Reflection.Emit;
using Verse.AI;
using HarmonyLib;
using RimLayers.Core;
using Verse;

namespace RimLayers.Coordination;

// Native HasOutsideAccess is an inspection hint based only on impassable doors.
// A usable staircase crossing the pen boundary is the same kind of led access;
// it does not merge regions or make a penned roamer free to leave.
[HarmonyPatch(typeof(PenMarkerState),nameof(PenMarkerState.HasOutsideAccess),MethodType.Getter)]
internal static class PenAccessPatch
{
    private static readonly AccessTools.FieldRef<PenMarkerState,RimWorld.CompAnimalPenMarker> Marker=
        AccessTools.FieldRefAccess<PenMarkerState,RimWorld.CompAnimalPenMarker>("marker");
    static void Postfix(PenMarkerState __instance,ref bool __result)
    {
        if(__result)return;
        var marker=Marker(__instance);
        var owner=LayerMapComponent.For(marker?.parent?.Map);
        if(owner==null)return;
        foreach(var edge in owner.Connectors.MovementEdges(TraverseParms.For(TraverseMode.PassDoors)))
            if(__instance.ContainsConnectedRegion(edge.From.GetRegion(owner.map))!=
               __instance.ContainsConnectedRegion(edge.To.GetRegion(owner.map)))
            {__result=true;return;}
    }
}

// Keep native acceptance, enclosure and balanced-nutrition policy. Only "closest"
// compares prospective led paths instead of the artificial storage-band distance.
[HarmonyPatch]
internal static class PenDistancePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(AnimalPenUtility),nameof(AnimalPenUtility.ClosestSuitablePen));
        yield return AccessTools.Method(typeof(AnimalPenUtility),"PenIsCloser");
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source,MethodBase __originalMethod)
    {
        bool comparison=__originalMethod.Name=="PenIsCloser";int count=0;
        var distance=AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceToSquared));
        foreach(var code in source)
        {
            if(code.Calls(distance))
            {
                var animal=new CodeInstruction(comparison?OpCodes.Ldarg_2:OpCodes.Ldarg_0);
                animal.labels.AddRange(code.labels);code.labels.Clear();yield return animal;
                code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(PenDistancePatch),nameof(Distance));count++;
            }
            yield return code;
        }
        if(count!=(comparison?2:1))throw new InvalidOperationException($"RimLayers pen distance anchor changed: {__originalMethod.Name}/{count}");
    }
    internal static int Distance(IntVec3 from,IntVec3 to,Pawn animal)
    {
        if(LayerMapComponent.For(animal.Map)==null)return from.DistanceToSquared(to);
        using var path=animal.Map.pathFinder.FindPathNow(from,to,
            TraverseParms.For(TraverseMode.PassDoors).WithFenceblockedOf(animal),null,PathEndMode.Touch);
        return path.Found?(int)Math.Clamp(Math.Ceiling(path.TotalCost),0,int.MaxValue-1d):int.MaxValue;
    }
}
