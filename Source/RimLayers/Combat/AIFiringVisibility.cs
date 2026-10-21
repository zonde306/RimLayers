using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Combat;

/// <summary>Wait_Combat requires visible auto-targets as well as a clear grenade arc.</summary>
[HarmonyPatch(typeof(JobGiver_AIFightEnemy),"TryGiveJob")]
internal static class LayerAIFiringVisibilityPatch
{
    [ThreadStatic] internal static bool Active;
    static void Prefix(Pawn pawn,out bool __state)
    {__state=Active;Active=LayerMapComponent.For(pawn.Map)!=null;}
    static Exception? Finalizer(bool __state,Exception? __exception)
    {Active=__state;return __exception;}

    internal static bool AutomaticLine(Thing caster,IntVec3 source,LocalTargetInfo target,Verb verb)
    {
        var owner=CombatCoordinates.Cross(caster.Map,source,target.Cell);
        if(owner==null||!target.HasThing||!ShotProfiles.Curved(verb))return true;
        // The native wait driver reacquires with NeedLOSToAll. A physically possible arc
        // alone cannot make this cell a usable automatic firing position. Keep explicit
        // player casts outside this scope, including intentional area throws.
        return owner.Combat.Solve(caster,source,target,float.PositiveInfinity).GeometricallyPossible;
    }
    public static bool CanHit(Verb verb,LocalTargetInfo target)=>verb.CanHitTarget(target)&&
        (!Active||AutomaticLine(verb.Caster,verb.Caster.Position,target,verb));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native=AccessTools.Method(typeof(Verb),nameof(Verb.CanHitTarget),new[]{typeof(LocalTargetInfo)});
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(native))
            {instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(LayerAIFiringVisibilityPatch),nameof(CanHit));}
            yield return instruction;
        }
    }
}
