using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Recovery;

[HarmonyPatch(typeof(GenSpawn),nameof(GenSpawn.Spawn),new[]{typeof(Thing),typeof(IntVec3),typeof(Map),typeof(Rot4),typeof(WipeMode),typeof(bool),typeof(bool)})]
internal static class RecoverySpawnPatch
{
    [HarmonyPriority(Priority.First)]
    static bool Prefix(Thing newThing,IntVec3 loc,Map map,Rot4 rot,ref Thing? __result)
    {
        if(map==null||RecoveryCompatibility.TransactionActive||loc.InBounds(map)&&GenAdj.OccupiedRect(loc,rot,newThing.def.Size).InBounds(map))return true;
        if(EntityRecoveryService.Current?.CaptureInvalidSpawn(newThing,map,loc,rot)!=true)return true;
        __result=newThing;return false;
    }
    static void Postfix(Thing newThing,Map map)
    {
        if(newThing.Spawned&&newThing.Map==map)
        {EntityRecoveryService.Current?.Remember(newThing,map);EntityRecoveryService.Current?.Notice(newThing,map,"Spawn");}
    }
}

[HarmonyPatch(typeof(Thing),nameof(Thing.Position),MethodType.Setter)]
internal static class RecoveryPositionPatch
{
    static bool Prefix(Thing __instance,IntVec3 value)
    {
        if(EntityRecoveryService.Busy||!__instance.Spawned||EntityRecoveryService.Current is not {} service)return true;
        service.Remember(__instance,__instance.Map);
        if(value.InBounds(__instance.Map)&&GenAdj.OccupiedRect(value,__instance.Rotation,__instance.def.Size).InBounds(__instance.Map)||!RimLayersMod.Settings.AutoRecovery||RecoveryCompatibility.TransactionActive||
            LayerMapComponent.For(__instance.Map) is not {} owner||RecoveryCompatibility.Excluded(__instance,owner)||!RecoveryCompatibility.SupportedKind(__instance))return true;
        // Reject only the illegal grid mutation, then resolve after the caller's transaction.
        service.Notice(__instance,__instance.Map,"InvalidCoordinates",value);return false;
    }
    static void Postfix(Thing __instance)
    {
        if(EntityRecoveryService.Busy||!__instance.Spawned)return;
        EntityRecoveryService.Current?.Remember(__instance,__instance.Map);
        EntityRecoveryService.Current?.Notice(__instance,__instance.Map,"Position");
    }
}

[HarmonyPatch(typeof(Map),nameof(Map.FinalizeInit))]
internal static class RecoveryMapReadyPatch
{
    [HarmonyPriority(Priority.Last)]
    static void Postfix(Map __instance)=>EntityRecoveryService.Current?.QueueMap(__instance);
}

[HarmonyPatch(typeof(PawnsFinder),nameof(PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead),MethodType.Getter)]
internal static class RecoveryPawnEnumeration
{
    static void Postfix(ref List<Pawn> __result)
    {
        if(EntityRecoveryService.Current is not {StoredCount:>0} service)return;
        __result=__result.Concat(service.HeldPawns()).Distinct().ToList();
    }
}
[HarmonyPatch(typeof(PawnsFinder),nameof(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive),MethodType.Getter)]
internal static class RecoveryLivingPawnEnumeration
{
    static void Postfix(ref List<Pawn> __result)
    {
        if(EntityRecoveryService.Current is not {StoredCount:>0} service)return;
        __result=__result.Concat(service.HeldPawns().Where(p=>!p.Dead)).Distinct().ToList();
    }
}
[HarmonyPatch(typeof(WorldPawnGC),"GetCriticalPawnReason")]
internal static class RecoveryPawnGcProtection
{
    static bool Prefix(Pawn pawn,ref string? __result)
    {if(!EntityRecoveryService.Contains(pawn))return true;__result="RimLayersRecovery";return false;}
}
[HarmonyPatch(typeof(GameEnder),nameof(GameEnder.CheckOrUpdateGameOver))]
internal static class RecoveryColonySurvival
{
    static bool Prefix(GameEnder __instance)
    {
        if(EntityRecoveryService.Current is not {StoredCount:>0} service||!service.HeldPawns().Any(p=>!p.Dead&&!p.Destroyed&&p.IsFreeColonist))return true;
        __instance.gameEnding=false;return false;
    }
}

// Native recovery destroys a Pawn when its radial search fails. Unsupported layered
// airspace joins the persistent queue instead; ordinary obstructed floors stay native.
[HarmonyPatch(typeof(Verse.AI.Pawn_PathFollower),nameof(Verse.AI.Pawn_PathFollower.TryRecoverFromUnwalkablePosition))]
internal static class RecoveryNativeUnwalkablePatch
{
    static bool Prefix(Pawn ___pawn,ref bool __result)
    {
        if(!___pawn.Spawned||LayerMapComponent.For(___pawn.Map) is not {} owner||RecoveryLanding.HasSupport(owner,___pawn,___pawn.Position))return true;
        if(owner.PendingCollapse.Contains(___pawn.Position)&&owner.StructuralCells.Contains(___pawn.Position)){__result=false;return false;}
        if(!RimLayersMod.Settings.AutoRecovery||RecoveryCompatibility.TransactionActive||RecoveryCompatibility.Excluded(___pawn,owner)||EntityRecoveryService.Current is not {} service)return true;
        service.Notice(___pawn,___pawn.Map,"Unsupported");__result=false;return false;
    }
}

// Native job cleanup normally drops carried Things. During this one Pawn's recovery,
// retain the original carried owner while allowing normal cleanup/reservation release.
[HarmonyPatch(typeof(Verse.AI.Pawn_JobTracker),"CleanupCurrentJob")]
internal static class RecoveryCarriedThingPreservation
{
    static void Prefix(Pawn ___pawn,ref bool? carryThingAfterJobOverride)
    {if(___pawn==EntityRecoveryService.TransferringPawn)carryThingAfterJobOverride=true;}
}
