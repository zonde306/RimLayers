using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using Verse;

namespace RimLayers.Combat;

[HarmonyPatch(typeof(DamageWorker),nameof(DamageWorker.ExplosionStart))]
internal static class ExplosionHeatPatch
{
    // Native StartExplosion owns the one-shot start. Loading an active wave does not call it again.
    public static bool PushHeat(IntVec3 center,Map map,float energy,Explosion explosion,List<IntVec3> cells)
    {
        if(LayerMapComponent.For(map)==null || CombatService.Blast(explosion)==null || cells.Count==0)
            return GenTemperature.PushHeat(center,map,energy);
        bool applied=false;float perCell=energy/cells.Count;
        foreach(IntVec3 cell in cells)applied|=GenTemperature.PushHeat(cell,map,perCell);
        return applied;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original=AccessTools.Method(typeof(GenTemperature),nameof(GenTemperature.PushHeat),new[]{typeof(IntVec3),typeof(Map),typeof(float)});
        int count=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(original))
            {
                var argument=new CodeInstruction(OpCodes.Ldarg_1);argument.labels.AddRange(instruction.labels);instruction.labels.Clear();
                yield return argument;yield return new CodeInstruction(OpCodes.Ldarg_2);
                instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(ExplosionHeatPatch),nameof(PushHeat));count++;
            }
            yield return instruction;
        }
        if(count!=1)throw new InvalidOperationException($"RimLayers explosion heat: expected one start injection, found {count}");
    }
}
