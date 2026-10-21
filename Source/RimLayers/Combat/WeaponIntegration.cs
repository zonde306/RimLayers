using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Combat;

internal static class LayerExposureLaunch
{
    public static bool Managed
    {
        get
        {
            var verb=CombatCoordinates.CastingVerb;
            return verb?.caster?.Map is {} map && LayerMapComponent.For(map)!=null &&
                (ShotProfiles.Curved(verb)||CombatCoordinates.Cross(map,verb.caster.Position,verb.CurrentTarget.Cell)!=null);
        }
    }
    public static float PassCover(ref ShotReport report)=>Managed?1:report.PassCoverChance;
    public static Thing? RandomCover(ref ShotReport report)=>Managed?null:report.GetRandomCoverToMissInto();
    public static float SearchRange(Verb verb)=>LayerMapComponent.For(verb.caster?.Map)?.Combat.MaximumRange(verb)??verb.EffectiveRange;
}

[HarmonyPatch(typeof(AttackTargetFinder),nameof(AttackTargetFinder.BestShootTargetFromCurrentPosition))]
internal static class LayerStationaryScanRangePatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var range=AccessTools.PropertyGetter(typeof(Verb),nameof(Verb.EffectiveRange));
        foreach(var code in instructions)
        {
            if(code.Calls(range)){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(LayerExposureLaunch),nameof(LayerExposureLaunch.SearchRange));}
            yield return code;
        }
    }
}

[HarmonyPatch(typeof(Building_TurretGun),nameof(Building_TurretGun.OrderAttack))]
internal static class LayerTurretOrderPatch
{
    [ThreadStatic] private static LocalTargetInfo target;
    static void Prefix(Building_TurretGun __instance,LocalTargetInfo targ,out (Map?,LocalTargetInfo) __state)
    {__state=(CombatCoordinates.Context,target);CombatCoordinates.Context=__instance.Map;target=targ;}
    static Exception? Finalizer((Map?,LocalTargetInfo) __state,Exception? __exception)
    {CombatCoordinates.Context=__state.Item1;target=__state.Item2;return __exception;}
    public static float Range(Verb verb)=>LayerMapComponent.For(verb.caster?.Map)?.Combat.EffectiveRange(verb,verb.caster!.Position,target.Cell)??verb.EffectiveRange;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var subtract=AccessTools.Method(typeof(IntVec3),"op_Subtraction",new[]{typeof(IntVec3),typeof(IntVec3)});
        var range=AccessTools.PropertyGetter(typeof(Verb),nameof(Verb.EffectiveRange));
        foreach(var code in instructions)
        {
            if(code.Calls(subtract))code.operand=AccessTools.Method(typeof(CombatCoordinates),nameof(CombatCoordinates.Difference));
            if(code.Calls(range)){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(LayerTurretOrderPatch),nameof(Range));}
            yield return code;
        }
    }
}

[HarmonyPatch(typeof(Building_TurretGun),nameof(Building_TurretGun.TryFindNewTarget))]
internal static class LayerTurretAcquisitionPatch
{
    [ThreadStatic] internal static Building_TurretGun? Current;
    static void Prefix(Building_TurretGun __instance,out (Map?,Building_TurretGun?) __state)
    {__state=(CombatCoordinates.Context,Current);CombatCoordinates.Context=__instance.Map;Current=__instance;}
    static void Postfix(Building_TurretGun __instance,ref LocalTargetInfo __result)
    {
        if(LayerMapComponent.For(__instance.Map) is {} owner && __result.IsValid &&
            (CombatCoordinates.Cross(__instance.Map,__instance.Position,__result.Cell)!=null||ShotProfiles.Curved(__instance.AttackVerb)) &&
            !owner.Combat.SolveWeapon(__instance,__instance.Position,__result,__instance.AttackVerb).GeometricallyPossible)
            __result=LocalTargetInfo.Invalid;
    }
    static Exception? Finalizer((Map?,Building_TurretGun?) __state,Exception? __exception)
    {CombatCoordinates.Context=__state.Item1;Current=__state.Item2;return __exception;}
}

[HarmonyPatch(typeof(Building_TurretGun),nameof(Building_TurretGun.TryStartShootSomething))]
internal static class LayerMortarClearancePatch
{
    static bool Prefix(Building_TurretGun __instance)
    {
        if(LayerMapComponent.For(__instance.Map) is not {} owner || !ShotProfiles.Overhead(__instance.AttackVerb) ||
            owner.Combat.OpenLaunchColumn(__instance.Position))return true;
        AccessTools.Method(typeof(Building_TurretGun),"ResetCurrentTarget").Invoke(__instance,null);
        return false;
    }
}

// Search from the gun, not its operator's adjacent interaction cell. Native target validators remain in place.
[HarmonyPatch(typeof(Building_TurretGun),"TargSearcher")]
internal static class LayerMannedSearchOriginPatch
{
    static void Postfix(Building_TurretGun __instance,ref IAttackTargetSearcher __result)
    {if(LayerMapComponent.For(__instance.Map)!=null)__result=__instance;}
}

// The native hostile-mortar branch has a separate building predicate before random selection.
[HarmonyPatch]
internal static class LayerMortarBuildingCandidatePatch
{
    static IEnumerable<MethodBase> TargetMethods()=>typeof(Building_TurretGun)
        .GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)
        .SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
        .Where(m=>m.Name.Contains("<TryFindNewTarget>")&&m.ReturnType==typeof(bool));
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var squared=AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceToSquared));
        foreach(var code in instructions)
        {if(code.Calls(squared))code.operand=AccessTools.Method(typeof(CombatCoordinates),nameof(CombatCoordinates.DistanceSquared));yield return code;}
    }
    static void Postfix(Building x,ref bool __result)
    {if(__result&&LayerTurretAcquisitionPatch.Current is {} turret)__result=LayerTurretCandidatePatch.Allows(turret,x);}
}

[HarmonyPatch(typeof(Building_TurretGun),"IsValidTarget")]
internal static class LayerTurretCandidatePatch
{
    internal static bool Allows(Building_TurretGun turret,Thing target)
    {
        var owner=LayerMapComponent.For(turret.Map);
        bool cross=CombatCoordinates.Cross(turret.Map,turret.Position,target.Position)!=null;
        if(owner==null||!cross&&!ShotProfiles.Curved(turret.AttackVerb))return true;
        if(!owner.Combat.SolveWeapon(turret,turret.Position,target,turret.AttackVerb).GeometricallyPossible)return false;
        // Indirect area orders are separate. Automatic acquisition cannot reveal a hidden entity.
        return !cross||owner.Combat.Solve(turret,turret.Position,target,float.PositiveInfinity).Clear;
    }
    static void Postfix(Building_TurretGun __instance,Thing t,ref bool __result)
    {if(__result)__result=Allows(__instance,t);}
}
