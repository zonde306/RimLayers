using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Combat;

internal static class CombatCover
{
    internal static List<CoverInfo> Covers(LocalTargetInfo target,IntVec3 source,Map map,out float blocked)
    {
        var owner=CombatCoordinates.Cross(map,source,target.Cell);
        blocked=0;
        if(owner==null)
        {
            var native=CoverUtility.CalculateCoverGiverSet(target,source,map);
            blocked=CoverUtility.CalculateOverallBlockChance(target,source,map);
            return native;
        }
        // Preference-only query: native probability must not be multiplied into exposure.
        var seer=source.GetFirstPawn(map) as Thing ?? source.GetEdifice(map);
        if(seer!=null)blocked=1-owner.Combat.Solve(seer,source,target,float.PositiveInfinity,
            silhouette:target.HasThing?null:LayerFiringPositionPatch.Current?.Caster).Exposure;
        return new List<CoverInfo>();
    }
    public static float BlockChance(LocalTargetInfo target,IntVec3 source,Map map)
    {
        if(CombatCoordinates.Cross(map,source,target.Cell)==null) return CoverUtility.CalculateOverallBlockChance(target,source,map);
        Covers(target,source,map,out float chance);return chance;
    }
    public static bool InRange(ref IntVec3 a,IntVec3 b,float range)=>
        CombatCoordinates.DistanceSquared(a,b)<=range*range;
    public static float Distance(IntVec3 a,IntVec3 b)=>CombatCoordinates.Difference(a,b).LengthHorizontal;
}

[HarmonyPatch(typeof(AttackTargetFinder),nameof(AttackTargetFinder.BestAttackTarget))]
internal static class LayerTargetQueryScopePatch
{
    static void Prefix(IAttackTargetSearcher searcher,out Map? __state)
    { __state=CombatCoordinates.Context;CombatCoordinates.Context=searcher?.Thing?.Map; }
    static Exception? Finalizer(Map? __state,Exception? __exception)
    { CombatCoordinates.Context=__state;return __exception; }
}

[HarmonyPatch]
internal static class LayerTargetArithmeticPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
        foreach(Type root in new[]{typeof(AttackTargetFinder),typeof(CastPositionFinder)})
            foreach(Type type in new[]{root}.Concat(root.GetNestedTypes(flags)))
                foreach(MethodInfo method in type.GetMethods(flags))
                    if(method.GetMethodBody()!=null) yield return method;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var subtract=AccessTools.Method(typeof(IntVec3),"op_Subtraction",new[]{typeof(IntVec3),typeof(IntVec3)});
        var squared=AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceToSquared));
        var distance=AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceTo));
        var inRange=AccessTools.Method(typeof(IntVec3),nameof(IntVec3.InHorDistOf));
        var reachable=AccessTools.Method(typeof(Reachability),nameof(Reachability.CanReach),new[]{typeof(IntVec3),typeof(LocalTargetInfo),typeof(PathEndMode),typeof(TraverseParms)});
        var cover=AccessTools.Method(typeof(CoverUtility),nameof(CoverUtility.CalculateOverallBlockChance),
            new[]{typeof(LocalTargetInfo),typeof(IntVec3),typeof(Map)});
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(reachable)) { instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(LayerFiringPositionPatch),nameof(LayerFiringPositionPatch.Reach)); }
            if(instruction.Calls(subtract)) instruction.operand=AccessTools.Method(typeof(CombatCoordinates),nameof(CombatCoordinates.Difference));
            if(instruction.Calls(squared)) instruction.operand=AccessTools.Method(typeof(CombatCoordinates),nameof(CombatCoordinates.DistanceSquared));
            if(instruction.Calls(distance)) instruction.operand=AccessTools.Method(typeof(CombatCover),nameof(CombatCover.Distance));
            if(instruction.Calls(inRange)) instruction.operand=AccessTools.Method(typeof(CombatCover),nameof(CombatCover.InRange));
            if(instruction.Calls(cover)) instruction.operand=AccessTools.Method(typeof(CombatCover),nameof(CombatCover.BlockChance));
            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(AttackTargetFinder),nameof(AttackTargetFinder.CanSee))]
internal static class LayerPerceptionPatch
{
    static bool Prefix(Thing seer,Thing target,Func<IntVec3,bool> validator,ref bool __result)
    {
        var owner=CombatCoordinates.Cross(seer?.Map,seer?.Position??IntVec3.Invalid,target?.Position??IntVec3.Invalid);
        if(owner==null || target?.Map!=owner.map) return true;
        var solution=owner.Combat.Solve(seer!,seer!.Position,target,float.PositiveInfinity);
        if(!solution.GeometricallyPossible) { __result=false;return false; }
        if(validator!=null)
        {
            var scene=new LayerShotScene(owner);
            foreach(var span in ShotGeometry.Cells(scene.Point(seer.Position,CombatService.SourceHeight(seer)),
                solution.Aim,3))
                if(owner.Layout!.Contains(new LayerCell(span.Layer,span.X,span.Z)) &&
                    !validator(owner.ToStorage(new LayerCell(span.Layer,span.X,span.Z))))
                { __result=false;return false; }
        }
        __result=true;return false;
    }
}

[HarmonyPatch(typeof(JobGiver_AIFightEnemy),"ShouldLoseTarget")]
internal static class LayerKeepShootingTargetPatch
{
    private static readonly Func<JobGiver_AIFightEnemy,int> KeepTicks=
        AccessTools.MethodDelegate<Func<JobGiver_AIFightEnemy,int>>(
            AccessTools.PropertyGetter(typeof(JobGiver_AIFightEnemy),"TicksSinceEngageToLoseTarget"));
    static void Postfix(JobGiver_AIFightEnemy __instance,Pawn pawn,float ___targetKeepRadius,ref bool __result)
    {
        Thing target=pawn.mindState.enemyTarget;
        var owner=CombatCoordinates.Cross(pawn.Map,pawn.Position,target?.Position??IntVec3.Invalid);
        if(!__result || owner==null || target?.Spawned!=true || target.Map!=pawn.Map ||
            Find.TickManager.TicksGame-pawn.mindState.lastEngageTargetTick>KeepTicks(__instance)) return;
        owner.TryDecode(pawn.Position,out var source);owner.TryDecode(target.Position,out var destination);
        int dx=destination.X-source.X,dz=destination.Z-source.Z;
        if(dx*dx+dz*dz>___targetKeepRadius*___targetKeepRadius) return;
        Verb? verb=((IAttackTargetSearcher)pawn).CurrentEffectiveVerb;
        if(pawn.CanReach(target,PathEndMode.Touch,Danger.Deadly) ||
            verb is Verb_LaunchProjectile && (verb.CanHitTarget(target) || LayerTacticalTarget.FindPosition(pawn,target,verb)))
            __result=(target as IAttackTarget)?.ThreatDisabled(pawn)??false;
    }
}


internal static class LayerTacticalTarget
{
    internal static bool FindPosition(Pawn pawn,Thing target,Verb verb,IntVec3 locus=default,float locusRadius=0)=>
        CastPositionFinder.TryFindCastPosition(new CastPositionRequest {
            caster=pawn,target=target,verb=verb,maxRangeFromTarget=verb.EffectiveRange,
            locus=locus,maxRangeFromLocus=locusRadius,wantCoverFromTarget=false
        },out _);
}

[HarmonyPatch(typeof(AttackTargetFinder),nameof(AttackTargetFinder.BestAttackTarget))]
internal static class LayerTacticalAcquirePatch
{
    [ThreadStatic] private static bool querying;
    static void Postfix(IAttackTargetSearcher searcher,TargetScanFlags flags,Predicate<Thing> validator,
        float minDist,float maxDist,IntVec3 locus,float maxTravelRadiusFromLocus,bool canBashDoors,
        bool canTakeTargetsCloserThanEffectiveMinRange,bool canBashFences,bool onlyRanged,ref IAttackTarget? __result)
    {
        if(querying || __result!=null || searcher is not Pawn pawn || pawn.InAggroMentalState ||
            LayerMapComponent.For(pawn.Map)==null || searcher.CurrentEffectiveVerb is not Verb_LaunchProjectile verb ||
            (flags&TargetScanFlags.NeedReachable)!=0 || (flags&TargetScanFlags.NeedReachableIfCantHitFromMyPos)==0) return;
        var excluded=new HashSet<Thing>();
        querying=true;
        try
        {
            while(true)
            {
                var target=AttackTargetFinder.BestAttackTarget(searcher,
                    flags&~TargetScanFlags.NeedReachableIfCantHitFromMyPos,
                    t=>!excluded.Contains(t) && (validator==null || validator(t)),minDist,maxDist,locus,
                    maxTravelRadiusFromLocus,canBashDoors,canTakeTargetsCloserThanEffectiveMinRange,canBashFences,onlyRanged);
                if(target==null) return;
                if(LayerTacticalTarget.FindPosition(pawn,target.Thing,verb,locus,
                    maxTravelRadiusFromLocus<9999 ? maxTravelRadiusFromLocus : 0))
                { __result=target;return; }
                excluded.Add(target.Thing);
            }
        }
        finally { querying=false; }
    }
}
