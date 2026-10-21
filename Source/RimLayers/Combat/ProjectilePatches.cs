using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Combat;

[HarmonyPatch(typeof(Projectile),nameof(Projectile.Launch),new[]{
    typeof(Thing),typeof(Vector3),typeof(LocalTargetInfo),typeof(LocalTargetInfo),
    typeof(ProjectileHitFlags),typeof(bool),typeof(Thing),typeof(ThingDef) })]
internal static class LayerLaunchPatch
{
    static void Postfix(Projectile __instance,Vector3 origin)
    {
        LayerMapComponent.For(__instance.Map)?.Combat.Launched(__instance,origin);
    }
}

[HarmonyPatch(typeof(Projectile),nameof(Projectile.ExactPosition),MethodType.Getter)]
internal static class LayerProjectilePositionPatch
{
    static void Postfix(Projectile __instance,ref Vector3 __result)
    {
        var flight=CombatService.Flight(__instance);
        var owner=LayerMapComponent.For(__instance.Map);
        if(flight==null || owner==null) return;
        var point=flight.Point(flight.Progress);
        // Storage remains in the projection band while the 3D sweep owns world-boundary exits.
        __result.x=Math.Clamp((float)point.X,0.001f,owner.Layout!.Width-0.001f);
        __result.z=Math.Clamp((float)point.Z,0.001f,owner.Layout.Height-0.001f)+flight.StorageLayer*owner.Layout!.Stride;
    }
}

[HarmonyPatch(typeof(Projectile),nameof(Projectile.DrawPos),MethodType.Getter)]
internal static class LayerProjectileDrawPatch
{
    static void Postfix(Projectile __instance,ref Vector3 __result)
    {
        var flight=CombatService.Flight(__instance);
        var owner=LayerMapComponent.For(__instance.Map);
        if(flight==null || owner==null) return;
        var point=flight.DrawPoint;
        int layer=flight.ImpactCommitted || flight.ShieldCommitted ? flight.ImpactLayer : owner.Layout!.ProjectionLayer(point.Height);
        __result.x=(float)point.X;__result.z=(float)point.Z+layer*owner.Layout!.Stride;
    }
}

[HarmonyPatch(typeof(Projectile),"CheckForFreeInterceptBetween")]
internal static class LayerProjectileSweepPatch
{
    static bool Prefix(Projectile __instance,Vector3 lastExactPos,Vector3 newExactPos,ref bool __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null) return true;
        var flight=CombatService.Flight(__instance);
        if(flight!=null) { __result=owner.Combat.Sweep(flight);return false; }
        // Preserve native same-floor ballistics, but crossing a storage gap is a world exit.
        if(!owner.TryDecode(lastExactPos.ToIntVec3(),out var start) ||
            !owner.TryDecode(newExactPos.ToIntVec3(),out var end) || start.LayerId!=end.LayerId)
        { __instance.Destroy();__result=true;return false; }
        return true;
    }
}

[HarmonyPatch(typeof(Projectile),"CanHit")]
internal static class LayerProjectileCandidatePatch
{
    static bool Prefix(Projectile __instance,Thing thing,ref bool __result)
    {
        var flight=CombatService.Flight(__instance);
        var owner=LayerMapComponent.For(__instance.Map);
        if(flight==null || owner==null || owner.Combat.GeometricHitAllowed(flight,thing)) return true;
        __result=false;return false;
    }
}

/// <summary>Guard the complete native override chain once, before its damage/EMP/explosion effects.</summary>
[HarmonyPatch]
internal static class LayerImpactReceiptPatch
{
    [ThreadStatic] private static HashSet<Projectile>? executing;
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(Type type in typeof(Projectile).Assembly.GetTypes())
        {
            if(!typeof(Projectile).IsAssignableFrom(type)) continue;
            MethodInfo? method=type.GetMethod("Impact",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|
                BindingFlags.DeclaredOnly,null,new[]{typeof(Thing),typeof(bool)},null);
            if(method!=null && !method.IsAbstract) yield return method;
        }
    }
    static bool Prefix(Projectile __instance,out bool __state)
    {
        __state=false;
        var flight=CombatService.Flight(__instance);
        var owner=LayerMapComponent.For(__instance.Map);
        if(flight==null || owner==null) return true;
        executing??=new();
        if(executing.Contains(__instance)) return true; // A derived native Impact calling base.
        if(flight.ImpactCommitted) return false;
        ShotPoint point=flight.PendingImpact??flight.Point(flight.Progress);
        owner.Combat.PlaceImpact(flight,point,
            flight.PendingImpact!=null || flight.ShieldCommitted ? flight.ImpactLayer : null);
        flight.ImpactCommitted=true;flight.PendingImpact=null;flight.ActivePoint=null;
        owner.Combat.CommittedImpacts++;
        executing.Add(__instance);__state=true;
        return true;
    }
    static Exception? Finalizer(Projectile __instance,bool __state,Exception? __exception)
    {
        if(__state) executing?.Remove(__instance);
        return __exception;
    }
}


[HarmonyPatch(typeof(Projectile),"TickInterval")]
internal static class LayerProjectileStorageStepPatch
{
    static void Postfix(Projectile __instance)
    {
        var flight=CombatService.Flight(__instance);
        var owner=LayerMapComponent.For(__instance.Map);
        if(flight==null || owner==null || __instance.Destroyed || flight.ImpactCommitted) return;
        var point=flight.Point(flight.Progress);
        int layer=owner.Layout!.ProjectionLayer(point.Height);
        int x=(int)Math.Floor(point.X),z=(int)Math.Floor(point.Z);
        if(owner.Layout.Contains(new RimLayers.Layout.LayerCell(layer,x,z)))
            __instance.Position=owner.ToStorage(new RimLayers.Layout.LayerCell(layer,x,z));
    }
}

// Physical flight already chooses the displayed floor. The native flat-map arc would add a
// second, unrelated northward displacement to grenades and overhead shots.
[HarmonyPatch(typeof(Projectile),"ArcHeightFactor",MethodType.Getter)]
internal static class LayerProjectileVisualArcPatch
{
    static void Postfix(Projectile __instance,ref float __result)
    { if(CombatService.Flight(__instance)!=null) __result=0; }
}
