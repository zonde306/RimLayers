using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Presentation;

internal static class LogicalAiming
{
    [ThreadStatic] internal static bool SuppressEquipmentAim;
    internal static Vector3 Delta(Vector3 target,Vector3 source,Pawn pawn)
    {
        var delta=target-source;var owner=LayerMapComponent.For(pawn.MapHeld);
        if(owner!=null&&owner.TryDecode(source.ToIntVec3(),out var from)&&owner.TryDecode(target.ToIntVec3(),out var to))
            delta.z-=(to.LayerId-from.LayerId)*owner.Layout!.Stride;
        return delta;
    }
}

[HarmonyPatch(typeof(PawnRenderUtility),nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
internal static class LogicalWeaponAimPatch
{
    static void Prefix(PawnRenderFlags flags,out bool __state)
    {
        __state=LogicalAiming.SuppressEquipmentAim;
        LogicalAiming.SuppressEquipmentAim|=flags.HasFlag(PawnRenderFlags.NeverAimWeapon)||flags.HasFlag(PawnRenderFlags.Portrait);
    }
    static void Finalizer(bool __state)=>LogicalAiming.SuppressEquipmentAim=__state;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var subtract=AccessTools.Method(typeof(Vector3),"op_Subtraction",new[]{typeof(Vector3),typeof(Vector3)});
        var logical=AccessTools.Method(typeof(LogicalAiming),nameof(LogicalAiming.Delta));int count=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(subtract))
            {
                var pawn=new CodeInstruction(OpCodes.Ldarg_0);pawn.labels.AddRange(instruction.labels);instruction.labels.Clear();
                yield return pawn;yield return new CodeInstruction(OpCodes.Call,logical);count++;
            }
            else yield return instruction;
        }
        if(count!=2)throw new InvalidOperationException($"Unexpected native aiming subtraction count: {count}.");
    }
}
[HarmonyPatch(typeof(Pawn_RotationTracker),nameof(Pawn_RotationTracker.Face))]
internal static class LogicalPawnFacingPatch
{
    static void Prefix(ref Vector3 p,Pawn ___pawn)=>p=___pawn.DrawPos+LogicalAiming.Delta(p,___pawn.DrawPos,___pawn);
}

// Some custom render workers bypass the native extras method and pass a fixed
// carrying angle. Correct active cross-floor aim at the shared final entry too;
// never rewrite idle/same-floor art, portrait flags, or a verb's explicit override.
[HarmonyPatch(typeof(PawnRenderUtility),nameof(PawnRenderUtility.DrawEquipmentAiming))]
internal static class LogicalCustomWeaponAimPatch
{
    [HarmonyPriority(Priority.First)]
    static void Prefix(Thing eq,ref float aimAngle)
    {
        if(LogicalAiming.SuppressEquipmentAim||eq.ParentHolder is not Pawn_EquipmentTracker tracker)return;
        var pawn=tracker.pawn;var owner=LayerMapComponent.For(pawn.MapHeld);
        if(owner==null||pawn.stances?.curStance is not Stance_Busy stance||stance.neverAimWeapon||!stance.focusTarg.IsValid||pawn.CurrentEffectiveVerb?.AimAngleOverride!=null)return;
        var target=stance.focusTarg.HasThing?stance.focusTarg.Thing.DrawPos:stance.focusTarg.Cell.ToVector3Shifted();
        if(!owner.TryDecode(pawn.Position,out var from)||!owner.TryDecode(target.ToIntVec3(),out var to)||from.LayerId==to.LayerId)return;
        var delta=LogicalAiming.Delta(target,pawn.DrawPos,pawn);
        aimAngle=delta.MagnitudeHorizontalSquared()>.001f?delta.AngleFlat():0;
    }
}
