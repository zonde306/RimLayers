using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Combat;

[HarmonyPatch(typeof(Projectile_Explosive),"Explode")]
internal static class LayerDetonationOriginPatch
{
    static void Prefix(Projectile_Explosive __instance,out (Map Map,IntVec3 Cell,Vector3 Point)? __state)
    {
        __state=BlastAccess.Impact;
        if(CombatService.Flight(__instance) is {} flight && __instance.Map is {} map)
            BlastAccess.Impact=(map,__instance.Position,flight.ImpactPoint);
    }
    static Exception? Finalizer((Map Map,IntVec3 Cell,Vector3 Point)? __state,Exception? __exception)
    { BlastAccess.Impact=__state;return __exception; }
}

[HarmonyPatch(typeof(Explosion),nameof(Explosion.StartExplosion))]
internal static class LayerBlastStartPatch
{
    static void Prefix(Explosion __instance)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner!=null && owner.TryDecode(__instance.Position,out _)) owner.Combat.BeginBlast(__instance);
    }
}

[HarmonyPatch(typeof(DamageWorker),nameof(DamageWorker.ExplosionCellsToHit),new[]{typeof(Explosion)})]
internal static class LayerBlastCoveragePatch
{
    static bool Prefix(Explosion explosion,ref IEnumerable<IntVec3> __result)
    {
        var owner=LayerMapComponent.For(explosion.Map);
        if(owner==null || CombatService.Blast(explosion)==null) return true;
        __result=owner.Combat.BlastCells(explosion);return false;
    }
}

[HarmonyPatch(typeof(Explosion),"AddCellsNeighbors")]
internal static class LayerBlastNeighborsPatch
{
    static bool Prefix(Explosion __instance,List<IntVec3> cells)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null || CombatService.Blast(__instance)==null) return true;
        var all=cells.Where(c=>owner.TryDecode(c,out _)).ToHashSet();
        var added=BlastAccess.Neighbors(__instance);added.Clear();
        foreach(var cell in cells)
        {
            if(!owner.TryDecode(cell,out var place) || !cell.Walkable(owner.map)) continue;
            foreach(var delta in GenAdj.AdjacentCells)
            {
                IntVec3 next=cell+delta;
                if(owner.TryDecode(next,out var target) && target.LayerId==place.LayerId && all.Add(next)) added.Add(next);
            }
        }
        cells.Clear();cells.AddRange(all);return false;
    }
}

// This gate also covers an explicit caller-supplied overrideCells list. Empty filtered lists
// stay empty, rather than accidentally selecting vanilla's fallback radial coverage.
[HarmonyPatch(typeof(DamageWorker),nameof(DamageWorker.ExplosionStart))]
internal static class LayerBlastCellGatePatch
{
    static void Prefix(Explosion explosion,List<IntVec3> cellsToAffect)
    {
        var owner=LayerMapComponent.For(explosion.Map);
        if(owner==null || CombatService.Blast(explosion)==null) return;
        var seen=new HashSet<IntVec3>();
        cellsToAffect.RemoveAll(c=>!owner.TryDecode(c,out _) || !seen.Add(c));
    }
}

[HarmonyPatch(typeof(Explosion),"GetCellAffectTick")]
internal static class LayerBlastTimingPatch
{
    static bool Prefix(Explosion __instance,IntVec3 cell,ref int __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null || CombatService.Blast(__instance)==null) return true;
        __result=BlastAccess.Started(__instance)+(int)(owner.Combat.BlastDistance(__instance,cell)*1.5f/__instance.propagationSpeed);
        return false;
    }
}
[HarmonyPatch(typeof(Explosion),nameof(Explosion.GetDamageAmountAt))]
internal static class LayerBlastFalloffPatch
{
    static bool Prefix(Explosion __instance,IntVec3 c,ref int __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null || CombatService.Blast(__instance)==null) return true;
        __result=__instance.damageFalloff ? Mathf.Max(GenMath.RoundRandom(Mathf.Lerp(__instance.damAmount,
            __instance.damAmount*0.2f,owner.Combat.BlastDistance(__instance,c)/__instance.radius)),1) : __instance.damAmount;
        return false;
    }
}
[HarmonyPatch(typeof(Explosion),nameof(Explosion.GetArmorPenetrationAt))]
internal static class LayerBlastArmorPatch
{
    static bool Prefix(Explosion __instance,IntVec3 c,ref float __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null || CombatService.Blast(__instance)==null) return true;
        __result=__instance.damageFalloff ? Mathf.Lerp(__instance.armorPenetration,__instance.armorPenetration*0.2f,
            owner.Combat.BlastDistance(__instance,c)/__instance.radius) : __instance.armorPenetration;
        return false;
    }
}
[HarmonyPatch(typeof(Explosion),"AffectCell")]
internal static class LayerBlastAffectPatch
{
    static bool Prefix(Explosion __instance,IntVec3 c)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner==null || CombatService.Blast(__instance)==null) return true;
        owner.Combat.AffectBlastCell(__instance,c);return false;
    }
}

[HarmonyPatch]
internal static class LayerBlastWorkerGeometryPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(Type type in typeof(DamageWorker).Assembly.GetTypes())
            if(typeof(DamageWorker).IsAssignableFrom(type))
                foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                    if(method.GetMethodBody()!=null && (method.Name==nameof(DamageWorker.ExplosionAffectCell) ||
                        method.Name=="ExplosionDamageThing")) yield return method;
    }
    // The only patched magnitudes in native ExplosionAffectCell are blast-center distances
    // (cell flash colour and snow). Scope never changes other game vector operations.
    public static float Magnitude(ref IntVec3 difference)
    {
        var explosion=BlastAccess.Current;
        var owner=LayerMapComponent.For(explosion?.Map);
        return owner!=null && explosion!=null ? owner.Combat.BlastDistance(explosion,BlastAccess.Cell) : difference.LengthHorizontal;
    }
    public static bool InRange(ref IntVec3 a,IntVec3 b,float radius)
    {
        var explosion=BlastAccess.Current;
        var owner=LayerMapComponent.For(explosion?.Map);
        return owner!=null && explosion!=null && b==explosion.Position ? owner.Combat.BlastDistance(explosion,a)<=radius : a.InHorDistOf(b,radius);
    }
    public static float Distance(IntVec3 a,IntVec3 b)
    {
        var explosion=BlastAccess.Current;
        var owner=LayerMapComponent.For(explosion?.Map);
        if(owner!=null && explosion!=null)
        {
            if(b==explosion.Position) return owner.Combat.BlastDistance(explosion,a);
            if(a==explosion.Position) return owner.Combat.BlastDistance(explosion,b);
        }
        return a.DistanceTo(b);
    }
    public static IntVec3 Difference(IntVec3 a,IntVec3 b)
    {
        var owner=LayerMapComponent.For(BlastAccess.Current?.Map);
        if(owner!=null && owner.TryDecode(a,out var left) && owner.TryDecode(b,out var right))
            return new IntVec3(left.X-right.X,0,left.Z-right.Z);
        return a-b;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var distance=AccessTools.Method(typeof(IntVec3Utility),nameof(IntVec3Utility.DistanceTo));
        var length=AccessTools.PropertyGetter(typeof(IntVec3),nameof(IntVec3.LengthHorizontal));
        var inRange=AccessTools.Method(typeof(IntVec3),nameof(IntVec3.InHorDistOf));
        var subtract=AccessTools.Method(typeof(IntVec3),"op_Subtraction",new[]{typeof(IntVec3),typeof(IntVec3)});
        foreach(var code in instructions)
        {
            if(code.Calls(distance)) code.operand=AccessTools.Method(typeof(LayerBlastWorkerGeometryPatch),nameof(Distance));
            if(code.Calls(length) && __originalMethod.Name==nameof(DamageWorker.ExplosionAffectCell))
                code.operand=AccessTools.Method(typeof(LayerBlastWorkerGeometryPatch),nameof(Magnitude));
            if(code.Calls(inRange)) code.operand=AccessTools.Method(typeof(LayerBlastWorkerGeometryPatch),nameof(InRange));
            if(code.Calls(subtract) && __originalMethod.Name=="ExplosionDamageThing")
                code.operand=AccessTools.Method(typeof(LayerBlastWorkerGeometryPatch),nameof(Difference));
            yield return code;
        }
    }
}
