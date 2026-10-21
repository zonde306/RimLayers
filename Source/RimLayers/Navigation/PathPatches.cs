using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Navigation;

[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[] {
    typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?),
    typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
internal static class LayerPathPatch
{
    [HarmonyPriority(Priority.High)]
    static bool Prefix(Map ___map, IntVec3 start, LocalTargetInfo target, TraverseParms traverseParms,
        PathFinderCostTuning? tuning, PathEndMode peMode, PathRequest.IPathGridCustomizer customizer, ref PawnPath __result)
    {
        var owner = LayerMapComponent.For(___map);
        if (LayerRouter.Busy || owner == null || !owner.Connectors.HasAny) return true;
        __result = LayerRouter.Find(owner, start, target, traverseParms, tuning, peMode, customizer);
        return false;
    }
}

[HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach), new[] {
    typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
internal static class LayerReachPatch
{
    static bool Prefix(Map ___map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode,
        TraverseParms traverseParams, ref bool __result)
    {
        var owner = LayerMapComponent.For(___map);
        if (LayerRouter.Busy || LayerReachability.InsideNative || owner == null || !owner.Connectors.HasAny) return true;
        __result = LayerReachability.CanReach(owner, start, dest, peMode, traverseParams);
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_PathFollower), "GenerateNewPathRequest")]
internal static class LayerPathRequestPatch
{
    static void Postfix(Pawn_PathFollower __instance, Pawn ___pawn, PathEndMode ___peMode, ref PathRequest __result)
    {
        var owner = LayerMapComponent.For(___pawn.Map);
        if (__result == null || owner == null || !owner.Connectors.HasAny) return;
        PathRequest original = __result;
        var answered = ___pawn.Map.pathFinder.CreateRequest(original.Start, original.Target, null,
            original.TraverseParms, original.Tuning, original.EndMode, ___pawn, original.customizer);
        original.Dispose();
        var path = LayerRouter.Find(owner, answered.Start, answered.Target, answered.TraverseParms,
            answered.Tuning, answered.EndMode, answered.customizer);
        answered.Resolve(path.Found ? path : null);
        __result = answered;
    }
}

[HarmonyPatch(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell")]
internal static class StairStepCostPatch
{
    static void Postfix(Pawn_PathFollower __instance, Pawn ___pawn)
    {
        var owner = LayerMapComponent.For(___pawn.Map);
        if (owner != null && owner.TryDecode(___pawn.Position, out var from) &&
            owner.TryDecode(__instance.nextCell, out var to) && from.LayerId != to.LayerId)
        {
            int cost=SettingsMath.TravelTicks(LayerRouter.StairTicks,RimLayersMod.Settings.StairTime);
            if(__instance.curPath!=null&&LayerRouter.HasMetadata(__instance.curPath,out var tag)&&tag.Steps.TryGetValue((___pawn.Position,__instance.nextCell),out var edge))cost=edge.CurrentTicks;
            __instance.nextCellCostLeft = __instance.nextCellCostTotal = cost;
        }
    }
}

[HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
internal static class StairStepCommitPatch
{
    static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn, out IntVec3 __state)
    {
        __state = IntVec3.Invalid;
        var owner = LayerMapComponent.For(___pawn.Map);
        if (owner == null || Math.Abs(___pawn.Position.x - __instance.nextCell.x) <= 1 &&
            Math.Abs(___pawn.Position.z - __instance.nextCell.z) <= 1) return true;
        __state = ___pawn.Position;
        if (__instance.curPath == null || !LayerRouter.HasMetadata(__instance.curPath, out var tag) ||
            !tag.Steps.TryGetValue((___pawn.Position, __instance.nextCell), out var edge) ||
            !owner.Connectors.Validate(edge, tag.TraverseParms))
        {
            // Reset replans the SAME destination while preserving the Driver and native reservation.
            __instance.ResetToCurrentPosition();
            __state = IntVec3.Invalid;
            return false;
        }
        return true;
    }

    static void Postfix(Pawn ___pawn, IntVec3 __state)
    {
        if (__state.IsValid && ___pawn.Position != __state) ___pawn.Drawer?.tweener?.Notify_Teleported();
    }
}

[HarmonyPatch(typeof(PawnTweener), "TweenedPosRoot")]
internal static class StairTweenPatch
{
    static void Postfix(Pawn ___pawn, ref Vector3 __result)
    {
        var c = LayerMapComponent.For(___pawn.Map);
        if (c != null && ___pawn.pather?.Moving == true && c.TryDecode(___pawn.Position, out var from) &&
            c.TryDecode(___pawn.pather.nextCell, out var to) && from.LayerId != to.LayerId)
        {
            __result.x = ___pawn.Position.x + 0.5f;
            __result.z = ___pawn.Position.z + 0.5f;
        }
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.PostMapInit))]
internal static class StairLoadPathPatch
{
    static void Prefix(Pawn __instance)
    {
        if (LayerMapComponent.For(__instance.Map) == null || __instance.pather == null) return;
        var path = __instance.pather;
        if (Math.Abs(path.nextCell.x - __instance.Position.x) > 1 || Math.Abs(path.nextCell.z - __instance.Position.z) > 1)
        {
            path.nextCell = __instance.Position;
            path.nextCellCostLeft = 0;
            path.nextCellCostTotal = 1;
        }
    }
}

[HarmonyPatch(typeof(PathRequest), "ValidateInt")]
internal static class LayerLegValidationPatch
{
    static bool Prefix(PathRequest __instance, Map ___map, ref bool __result)
    {
        if (!LayerRouter.IsLegRequest(__instance, ___map) || LayerMapComponent.For(___map) == null ||
            __instance.TraverseParms.mode != TraverseMode.ByPawn) return true;
        // Vanilla ByPawn validation uses pawn.Position, which is NOT this hypothetical leg's start.
        // Keep the real Pawn/Map and native region rules, but validate the requested start.
        var pawn = __instance.TraverseParms.pawn;
        __result = pawn != null && pawn.Spawned && pawn.Map == ___map && __instance.Start.IsValid &&
            __instance.Target.IsValid && ___map.reachability.CanReach(__instance.Start, __instance.Target,
                __instance.EndMode, __instance.TraverseParms);
        return false;
    }
}
