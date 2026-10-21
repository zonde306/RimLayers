using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Combat;

internal static class CombatCoordinates
{
    internal static Map? Context { get=>LogicalQueryContext.Map;set=>LogicalQueryContext.Map=value; }
    [ThreadStatic] internal static Verb_LaunchProjectile? CastingVerb;
    [ThreadStatic] internal static ThingDef? CastingProjectile;
    internal static Thing? CastingThing=>CastingVerb?.caster;
    internal static LayerMapComponent? Cross(Map? map,IntVec3 a,IntVec3 b)
    {
        var owner=LayerMapComponent.For(map);
        return owner!=null && owner.TryDecode(a,out var left) && owner.TryDecode(b,out var right) &&
            left.LayerId!=right.LayerId ? owner : null;
    }
    internal static IntVec3 OnLayer(LayerMapComponent owner,IntVec3 cell,int layer)
    {
        if(!owner.TryDecode(cell,out var position)) return cell;
        return owner.ToStorage(new LayerCell(layer,position.X,position.Z));
    }
    public static IntVec3 Difference(IntVec3 a,IntVec3 b)
    {
        var owner=LayerMapComponent.For(Context);
        if(owner!=null && owner.TryDecode(a,out var left) && owner.TryDecode(b,out var right))
            return new IntVec3(left.X-right.X,0,left.Z-right.Z);
        return a-b;
    }
    public static int DistanceSquared(IntVec3 a,IntVec3 b)=>Difference(a,b).LengthHorizontalSquared;
}

[HarmonyPatch(typeof(Verb),nameof(Verb.TryFindShootLineFromTo))]
internal static class LayerShootLinePatch
{
    static bool Prefix(Verb __instance,IntVec3 root,LocalTargetInfo targ,bool ignoreRange,
        ref ShootLine resultingLine,ref bool __result)
    {
        var owner=LayerMapComponent.For(__instance.caster?.Map);
        bool cross=CombatCoordinates.Cross(__instance.caster?.Map,root,targ.Cell)!=null;
        if(owner==null || !cross&&!ShotProfiles.Curved(__instance))return true;
        resultingLine=new ShootLine(root,targ.Cell);
        if(cross && __instance is Verb_Jump jump)
        {
            __result=Navigation.JumpService.Solve(jump.CasterPawn,root,targ.Cell,
                ignoreRange?float.PositiveInfinity:jump.EffectiveRange,out _,out _);
            return false;
        }
        if(__instance is not Verb_LaunchProjectile || __instance.verbProps.IsMeleeAttack || __instance.EffectiveRange<=1.42f)
        {__result=false;return false;}
        if(__instance.verbProps.mustCastOnOpenGround && (!targ.Cell.Standable(owner.map) ||
            owner.map.thingGrid.CellContains(targ.Cell,ThingCategory.Pawn)))
        {__result=false;return false;}
        __result=owner.Combat.SolveWeapon(__instance.caster!,root,targ,__instance,ignoreRange).GeometricallyPossible;
        return false;
    }
}

[HarmonyPatch(typeof(Verb),nameof(Verb.OutOfRange))]
internal static class LayerWeaponRangePatch
{
    static bool Prefix(Verb __instance,IntVec3 root,LocalTargetInfo targ,CellRect occupiedRect,ref bool __result)
    {
        var owner=CombatCoordinates.Cross(__instance.caster?.Map,root,targ.Cell);
        if(owner==null || __instance is not Verb_LaunchProjectile) return true;
        owner.TryDecode(targ.Cell,out var target);
        IntVec3 projected=CombatCoordinates.OnLayer(owner,root,target.LayerId);
        float distance=occupiedRect.ClosestDistSquaredTo(projected);
        float minimum=__instance.verbProps.EffectiveMinRange(targ,__instance.caster);
        float range=owner.Combat.EffectiveRange(__instance,root,targ.Cell);
        __result=distance>range*range || distance<minimum*minimum;
        return false;
    }
}

[HarmonyPatch(typeof(Verb_MeleeAttack),"TryCastShot")]
internal static class LayerMeleeCommitPatch
{
    static bool Prefix(Verb_MeleeAttack __instance,ref bool __result)
    {
        var owner=LayerMapComponent.For(__instance.caster?.Map);
        if(owner==null) return true;
        var target=__instance.CurrentTarget;
        if(!target.IsValid || !owner.TryDecode(__instance.caster!.Position,out var source) ||
            !owner.TryDecode(target.Cell,out var destination) || source.LayerId!=destination.LayerId)
        { __result=false;return false; }
        return true;
    }
}

[HarmonyPatch(typeof(ShotReport),nameof(ShotReport.HitReportFor))]
internal static class LayerShotReportPatch
{
    private static readonly AccessTools.StructFieldRef<ShotReport,float> Distance=AccessTools.StructFieldRefAccess<ShotReport,float>("distance");
    private static readonly AccessTools.StructFieldRef<ShotReport,float> Shooter=AccessTools.StructFieldRefAccess<ShotReport,float>("factorFromShooterAndDist");
    private static readonly AccessTools.StructFieldRef<ShotReport,float> Equipment=AccessTools.StructFieldRefAccess<ShotReport,float>("factorFromEquipment");
    private static readonly AccessTools.StructFieldRef<ShotReport,float> Gas=AccessTools.StructFieldRefAccess<ShotReport,float>("factorFromCoveringGas");
    private static readonly AccessTools.StructFieldRef<ShotReport,List<CoverInfo>> Covers=AccessTools.StructFieldRefAccess<ShotReport,List<CoverInfo>>("covers");
    private static readonly AccessTools.StructFieldRef<ShotReport,float> CoverChance=AccessTools.StructFieldRefAccess<ShotReport,float>("coversOverallBlockChance");
    static void Postfix(Thing caster,Verb verb,LocalTargetInfo target,ref ShotReport __result)
    {
        var owner=CombatCoordinates.Cross(caster?.Map,caster?.Position??IntVec3.Invalid,target.Cell);
        if(owner==null || verb is not Verb_LaunchProjectile) return;
        owner.TryDecode(caster!.Position,out var source);owner.TryDecode(target.Cell,out var destination);
        float distance=new Vector2(destination.X-source.X,destination.Z-source.Z).magnitude;
        Distance(ref __result)=distance;
        Shooter(ref __result)=verb.verbProps.canGoWild ? ShotReport.HitFactorFromShooter(caster,distance) : 1;
        Equipment(ref __result)=verb.verbProps.GetHitChanceFactor(verb.EquipmentSource,distance);
        var scene=new LayerShotScene(owner,caster);
        ShotPoint a=scene.Point(caster.Position,CombatService.SourceHeight(caster)),b=scene.Point(target.Cell,CombatService.AimHeight(target.Thing));
        var solution=owner.Combat.SolveWeapon(caster,caster.Position,target,verb,true);
        Covers(ref __result)=new List<CoverInfo>();
        CoverChance(ref __result)=solution.Curved?0:1-solution.Exposure;
        Gas(ref __result)=1;
        foreach(var cell in ShotGeometry.Cells(a,b,3))
            if(owner.Layout!.Contains(new LayerCell(cell.Layer,cell.X,cell.Z)) &&
                owner.ToStorage(new LayerCell(cell.Layer,cell.X,cell.Z)).AnyGas(owner.map,GasType.BlindSmoke))
            { Gas(ref __result)=0.7f;break; }
    }
}

[HarmonyPatch(typeof(ShootLine),nameof(ShootLine.ChangeDestToMissWild))]
internal static class LayerWildMissPatch
{
    private static readonly AccessTools.StructFieldRef<ShootLine,IntVec3> Source=AccessTools.StructFieldRefAccess<ShootLine,IntVec3>("source");
    static void Prefix(ref ShootLine __instance,Map map,out IntVec3 __state)
    {
        __state=IntVec3.Invalid;
        var owner=CombatCoordinates.Cross(map,__instance.Source,__instance.Dest);
        if(owner==null) return;
        owner.TryDecode(__instance.Dest,out var destination);
        __state=__instance.Source;
        Source(ref __instance)=CombatCoordinates.OnLayer(owner,__instance.Source,destination.LayerId);
    }
    static Exception? Finalizer(ref ShootLine __instance,IntVec3 __state,Exception? __exception)
    {
        if(__state.IsValid) Source(ref __instance)=__state;
        return __exception;
    }
}

[HarmonyPatch]
internal static class LayerCastArithmeticPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
        foreach(Type type in new[]{typeof(Verb_LaunchProjectile)}.Concat(typeof(Verb_LaunchProjectile).GetNestedTypes(flags)))
            foreach(MethodInfo method in type.GetMethods(flags))
                if(method.Name=="TryCastShot" || method.Name.Contains("GetForcedMissTarget")) yield return method;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var difference=AccessTools.Method(typeof(IntVec3),"op_Subtraction",new[]{typeof(IntVec3),typeof(IntVec3)});
        var distance=AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceToSquared));
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(AccessTools.PropertyGetter(typeof(ShotReport),nameof(ShotReport.PassCoverChance))))
                instruction.operand=AccessTools.Method(typeof(LayerExposureLaunch),nameof(LayerExposureLaunch.PassCover));
            if(instruction.Calls(AccessTools.Method(typeof(ShotReport),nameof(ShotReport.GetRandomCoverToMissInto))))
                instruction.operand=AccessTools.Method(typeof(LayerExposureLaunch),nameof(LayerExposureLaunch.RandomCover));
            if(instruction.Calls(difference)) instruction.operand=AccessTools.Method(typeof(CombatCoordinates),nameof(CombatCoordinates.Difference));
            if(instruction.Calls(distance)) instruction.operand=AccessTools.Method(typeof(CombatCoordinates),nameof(CombatCoordinates.DistanceSquared));
            yield return instruction;
        }
    }
}


[HarmonyPatch(typeof(Verb_LaunchProjectile),"TryCastShot")]
internal static class LayerCastContextPatch
{
    static bool Prefix(Verb_LaunchProjectile? __instance,ref bool __result,out (Map? Map,Verb_LaunchProjectile? Verb,ThingDef? Projectile) __state)
    {
        __state=(CombatCoordinates.Context,CombatCoordinates.CastingVerb,CombatCoordinates.CastingProjectile);
        CombatCoordinates.Context=__instance?.caster?.Map;
        // Freeze ammo type before native Notify_ProjectileLaunched empties the magazine.
        CombatCoordinates.CastingProjectile=__instance?.Projectile;
        CombatCoordinates.CastingVerb=__instance;
        if(LayerExposureLaunch.Managed && LayerMapComponent.For(__instance?.caster?.Map) is {} owner &&
            !owner.Combat.SolveWeapon(__instance!.caster,__instance.caster.Position,__instance.CurrentTarget,__instance).GeometricallyPossible)
        {__result=false;return false;}
        return true;
    }
    static Exception? Finalizer((Map? Map,Verb_LaunchProjectile? Verb,ThingDef? Projectile) __state,Exception? __exception)
    { CombatCoordinates.Context=__state.Map;CombatCoordinates.CastingVerb=__state.Verb;CombatCoordinates.CastingProjectile=__state.Projectile;return __exception; }
 }
