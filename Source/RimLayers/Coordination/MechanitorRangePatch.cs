using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Coordination;

[HarmonyPatch(typeof(Pawn_MechanitorTracker),nameof(Pawn_MechanitorTracker.CanCommandTo))]
internal static class MechanitorRangePatch
{
    private static readonly AccessTools.FieldRef<Pawn_MechanitorTracker,Pawn> PawnOf=AccessTools.FieldRefAccess<Pawn_MechanitorTracker,Pawn>("pawn");
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var distance=AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceToSquared));int count=0;
        foreach(var code in source)
        {
            if(code.Calls(distance))
            {
                var self=new CodeInstruction(OpCodes.Ldarg_0);self.labels.AddRange(code.labels);code.labels.Clear();
                yield return self;code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(MechanitorRangePatch),nameof(Distance));count++;
            }
            yield return code;
        }
        if(count!=1)throw new InvalidOperationException($"RimLayers mechanitor distance anchor changed: {count}");
    }
    private static int Distance(IntVec3 a,IntVec3 b,Pawn_MechanitorTracker tracker)
    {
        var owner=LayerMapComponent.For(PawnOf(tracker).MapHeld);
        if(owner==null)return a.DistanceToSquared(b);
        if(!owner.TryDecode(a,out var left)||!owner.TryDecode(b,out var right))return int.MaxValue;
        int x=left.X-right.X,z=left.Z-right.Z;return x*x+z*z;
    }
}
