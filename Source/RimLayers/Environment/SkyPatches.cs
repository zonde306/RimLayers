using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.Noise;

namespace RimLayers.Environment;

/// <summary>Domain-specific views of roofs, not a global Roofed replacement.</summary>
internal static class SkyQueries
{
    private static readonly AccessTools.FieldRef<RoofGrid,Map> MapOf=AccessTools.FieldRefAccess<RoofGrid,Map>("map");
    private static readonly AccessTools.FieldRef<SteadyEnvironmentEffects,Map> WeatherMap=AccessTools.FieldRefAccess<SteadyEnvironmentEffects,Map>("map");
    internal static bool Roofed(IntVec3 cell,Map map)=>LayerMapComponent.For(map) is { } owner ? !owner.Sky.IsExposed(cell) : cell.Roofed(map);
    internal static bool RoofedGrid(RoofGrid grid,IntVec3 cell)=>Roofed(cell,MapOf(grid));
    internal static bool RoofedIndex(RoofGrid grid,int index)=>Roofed(CellIndicesUtility.IndexToCell(index,MapOf(grid).Size.x),MapOf(grid));
    internal static RoofDef? VisualRoof(RoofGrid grid,int index)
    {
        Map map=MapOf(grid);IntVec3 cell=CellIndicesUtility.IndexToCell(index,map.Size.x);
        RoofDef roof=grid.RoofAt(index);
        return roof==null && LayerMapComponent.For(map) is { } owner && !owner.Sky.IsExposed(cell) ? RoofDefOf.RoofConstructed : roof;
    }
    internal static float WeatherNoise(ModuleBase noise,IntVec3 at,SteadyEnvironmentEffects weather)
    {
        var owner=LayerMapComponent.For(WeatherMap(weather));
        if(owner!=null && owner.TryDecode(at,out var local))at=new IntVec3(local.X,0,local.Z);
        return noise.GetValue(at);
    }
    /// <summary>
    /// Redirects every call to <paramref name="original"/> inside one native method.
    ///
    /// A missing call site used to throw, and Harmony runs transpilers inside the mod constructor's
    /// patch loop, so one incompatible third-party transpiler that already rewrote the same call took
    /// the whole mod down (no patches at all, game looks inert). Another mod owning this call site is
    /// a legitimate outcome, not a broken install: warn and leave the method untouched instead.
    /// The warning keeps genuine RimWorld API drift visible in the log.
    /// </summary>
    internal static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions,MethodInfo original,MethodInfo replacement,int expected)
    {
        int count=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(original)) { instruction.opcode=OpCodes.Call;instruction.operand=replacement;count++; }
            yield return instruction;
        }
        if(count!=expected)
            Log.Warning($"[RimLayers] Skipped layered sky hook {original.Name}: expected {expected} call site(s), found {count}. "+
                "Another mod most likely patched the same method first; that method keeps its vanilla sky behaviour.");
    }
}

[HarmonyPatch(typeof(GlowGrid),nameof(GlowGrid.GroundGlowAt))]
internal static class LogicalSkyGlowPatch
{
    static bool Prefix(IntVec3 c,Map ___map,ref bool ignoreSky,ref float __result)
    {
        var owner=LayerMapComponent.For(___map);
        if(owner==null)return true;
        if(!owner.TryDecode(c,out _)) { __result=0;return false; }
        if(!owner.Sky.IsExposed(c))ignoreSky=true;
        return true;
    }
}

[HarmonyPatch(typeof(SteadyEnvironmentEffects),"DoCellSteadyEffects")]
internal static class LogicalWeatherPatch
{
    static bool Prefix(IntVec3 c,Map ___map)=>LayerMapComponent.For(___map) is not { } owner || owner.TryDecode(c,out _);
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(RoofGrid),nameof(RoofGrid.Roofed),new[]{typeof(IntVec3)}),
        AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.RoofedGrid)),1);
}

[HarmonyPatch]
internal static class LogicalPrecipitationPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SteadyEnvironmentEffects),nameof(SteadyEnvironmentEffects.AddFallenSnowAt));
        yield return AccessTools.Method(typeof(SteadyEnvironmentEffects),nameof(SteadyEnvironmentEffects.AddFallenSandAt));
    }
    static bool Prefix(IntVec3 c,Map ___map)=>LayerMapComponent.For(___map) is not { } owner ||
        owner.Structure.HasSurface(c) && owner.Sky.IsExposed(c);
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original=AccessTools.Method(typeof(ModuleBase),nameof(ModuleBase.GetValue),new[]{typeof(IntVec3)});int count=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(original))
            {
                var argument=new CodeInstruction(OpCodes.Ldarg_0);argument.labels.AddRange(instruction.labels);instruction.labels.Clear();
                yield return argument;instruction.opcode=OpCodes.Call;
                instruction.operand=AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.WeatherNoise));count++;
            }
            yield return instruction;
        }
        if(count!=1)throw new InvalidOperationException($"RimLayers precipitation noise: expected one call, found {count}");
    }
}

[HarmonyPatch(typeof(SteadyEnvironmentEffects),nameof(SteadyEnvironmentEffects.FinalDeteriorationRate),new[]{typeof(Thing),typeof(List<string>)})]
internal static class DeteriorationSkyPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(GridsUtility),nameof(GridsUtility.Roofed)),AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.Roofed)),1);
}

[HarmonyPatch(typeof(GasGrid),"TryDissipateGases")]
internal static class GasSkyDissipationPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(GridsUtility),nameof(GridsUtility.Roofed)),AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.Roofed)),1);
}

[HarmonyPatch(typeof(Fire),"VulnerableToRain")]
internal static class FireSkyRainPatch
{
    static void Postfix(Fire __instance,ref bool __result)
    { if(__result && LayerMapComponent.For(__instance.Map) is { } owner && !owner.Sky.IsExposedAbove(__instance.Position))__result=false; }
}

[HarmonyPatch(typeof(SectionLayer_LightingOverlay),"GenerateLightingOverlay")]
internal static class LogicalLightingOverlayPatch
{
    [HarmonyAfter("OskarPotocki.VEF")]
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        instructions=SkyQueries.Replace(instructions,AccessTools.Method(typeof(RoofGrid),nameof(RoofGrid.RoofAt),new[]{typeof(int)}),
            AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.VisualRoof)),1);
        return SkyQueries.Replace(instructions,AccessTools.Method(typeof(RoofGrid),nameof(RoofGrid.Roofed),new[]{typeof(int)}),
            AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.RoofedIndex)),1);
    }
}

[HarmonyPatch(typeof(Verse.AI.Pawn_MindState),nameof(Verse.AI.Pawn_MindState.MindStateTickInterval))]
internal static class WeatherThoughtSkyPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(GridsUtility),nameof(GridsUtility.Roofed)),AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.Roofed)),1);
}
