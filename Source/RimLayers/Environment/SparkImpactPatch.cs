using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(Spark),"Impact")]
internal static class SparkImpactPatch
{
    // The saved physical contact stays outside the wall. Native Spark uses Position rather
    // than hitThing for ignition, so preserve the actual obstacle as the ignition cell.
    public static bool Ignite(IntVec3 cell,Map map,float size,Thing instigator,SimpleCurve curve,Spark spark,Thing hitThing)
    {
        if(CombatService.Flight(spark) is { } flight && hitThing is Building {Spawned:true} && hitThing.Map==map &&
            LayerMapComponent.For(map) is { } owner && owner.TryDecode(hitThing.Position,out var target))
        {
            CellRect rect=hitThing.OccupiedRect();
            cell=new IntVec3(Math.Clamp((int)Math.Floor(flight.ImpactPoint.x),rect.minX,rect.maxX),0,
                Math.Clamp((int)Math.Floor(flight.ImpactPoint.z)+target.LayerId*owner.Layout!.Stride,rect.minZ,rect.maxZ));
        }
        return FireUtility.TryStartFireIn(cell,map,size,instigator,curve);
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original=AccessTools.Method(typeof(FireUtility),nameof(FireUtility.TryStartFireIn));int count=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(original))
            {
                var self=new CodeInstruction(OpCodes.Ldarg_0);self.labels.AddRange(instruction.labels);instruction.labels.Clear();
                yield return self;yield return new CodeInstruction(OpCodes.Ldarg_1);
                instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(SparkImpactPatch),nameof(Ignite));count++;
            }
            yield return instruction;
        }
        if(count!=1)throw new InvalidOperationException($"RimLayers spark ignition: expected one call, found {count}");
    }
}
