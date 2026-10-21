using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Environment;
using RimWorld;
using Verse;

namespace RimLayers.Underground;

[HarmonyPatch(typeof(InfestationCellFinder),"GetScoreAt")]
internal static class EcologicalInfestationScore
{
    [ThreadStatic] private static bool active;
    private static float Cold()=>active?LayerEnvironment.Settings.InsectCold:-17;
    private static float Warm()=>Cold()+10;
    private static float Light()=>active?Math.Max(.01f,1-.82f*LayerEnvironment.Settings.LightSuppression):.18f;
    static void Prefix(Map map,out bool __state){__state=active;active=LayerMapComponent.For(map)?.Environment.Enabled==true;}
    static void Postfix(IntVec3 cell,Map map,ref float __result)
    {
        if(!active||__result<=0)return;
        var owner=LayerMapComponent.For(map)!;
        float hot=Math.Clamp((LayerEnvironment.Settings.InsectHot-cell.GetTemperature(map))/10,0,1);
        __result*=MathF.Pow(hot,1.2f);
        if(owner.TryDecode(cell,out var c)&&owner.Elevation(c.LayerId)<0)__result*=LayerEnvironment.Settings.InfestationWeight;
    }
    static Exception? Finalizer(bool __state,Exception? __exception){active=__state;return __exception;}
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int count=0;
        foreach(var code in instructions)
        {
            if(code.opcode==OpCodes.Ldc_R4&&code.operand is float value)
            {
                string? helper=value==-17?nameof(Cold):value==-7?nameof(Warm):value==.18f?nameof(Light):null;
                if(helper!=null){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(EcologicalInfestationScore),helper);count++;}
            }
            yield return code;
        }
        if(count!=4)throw new InvalidOperationException("RimLayers infestation temperature/light policy changed.");
    }
}
[HarmonyPatch(typeof(CompSpawnerHives),"CanSpawnHiveAt")]
internal static class HiveSameLayerCandidates
{
    static void Postfix(IntVec3 c,Map map,IntVec3 parentPos,ref bool __result)
    {
        var owner=LayerMapComponent.For(map);
        if(__result&&owner!=null)__result=owner.TryDecode(c,out var a)&&owner.TryDecode(parentPos,out var b)&&a.LayerId==b.LayerId;
    }
}
[HarmonyPatch(typeof(CompSpawnerPawn),"TrySpawnPawn")]
internal static class HivePawnLocalSpawn
{
    internal static IntVec3 LocalCell(IntVec3 root,Map map,int radius,Predicate<IntVec3>? validator)
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null||!owner.TryDecode(root,out var parent))return CellFinder.RandomClosewalkCellNear(root,map,radius,validator);
        return CellFinder.RandomClosewalkCellNear(root,map,radius,c=>owner.TryDecode(c,out var q)&&q.LayerId==parent.LayerId&&(validator?.Invoke(c)??true));
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(CellFinder),nameof(CellFinder.RandomClosewalkCellNear)),AccessTools.Method(typeof(HivePawnLocalSpawn),nameof(LocalCell)),1);
}
[HarmonyPatch(typeof(TunnelHiveSpawner),"Spawn")]
internal static class TunnelPawnLocalSpawn
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(CellFinder),nameof(CellFinder.RandomClosewalkCellNear)),
        AccessTools.Method(typeof(HivePawnLocalSpawn),nameof(HivePawnLocalSpawn.LocalCell)),1);
}
internal static class HiveClock
{
    internal static bool Active(ThingComp comp)=>comp.parent is Hive&&LayerMapComponent.For(comp.parent.Map)?.Environment.Enabled==true;
    internal static int Scaled(int tick,float multiplier)=>multiplier<=0?tick:
        Find.TickManager.TicksGame+(int)Math.Ceiling(Math.Max(0,tick-Find.TickManager.TicksGame)/multiplier);
}
[HarmonyPatch(typeof(CompSpawnerPawn),nameof(CompSpawnerPawn.CompTick))]
internal static class HivePawnPause
{
    static bool Prefix(CompSpawnerPawn __instance)
    {
        if(!HiveClock.Active(__instance)||LayerEnvironment.Settings.InsectSpawnSpeed>0||!__instance.canSpawnPawns)return true;
        if(__instance.nextPawnSpawnTick>=0)__instance.nextPawnSpawnTick++;
        return false;
    }
}
[HarmonyPatch(typeof(CompSpawnerPawn),nameof(CompSpawnerPawn.CalculateNextPawnSpawnTick),new[]{typeof(float)})]
internal static class HivePawnInterval
{
    static void Postfix(CompSpawnerPawn __instance)
    {if(HiveClock.Active(__instance)&&__instance.canSpawnPawns)__instance.nextPawnSpawnTick=HiveClock.Scaled(__instance.nextPawnSpawnTick,LayerEnvironment.Settings.InsectSpawnSpeed);}
}
[HarmonyPatch(typeof(CompSpawnerHives),nameof(CompSpawnerHives.CompTick))]
internal static class HiveReproductionPause
{
    static bool Prefix(CompSpawnerHives __instance,ref int ___nextHiveSpawnTick,bool ___canSpawnHives)
    {
        if(!HiveClock.Active(__instance)||LayerEnvironment.Settings.HiveReproductionSpeed>0||!___canSpawnHives)return true;
        ___nextHiveSpawnTick++;return false;
    }
}
[HarmonyPatch(typeof(CompSpawnerHives),nameof(CompSpawnerHives.CalculateNextHiveSpawnTick))]
internal static class HiveReproductionInterval
{
    static void Postfix(CompSpawnerHives __instance,ref int ___nextHiveSpawnTick,bool ___canSpawnHives)
    {if(HiveClock.Active(__instance)&&___canSpawnHives)___nextHiveSpawnTick=HiveClock.Scaled(___nextHiveSpawnTick,LayerEnvironment.Settings.HiveReproductionSpeed);}
}
