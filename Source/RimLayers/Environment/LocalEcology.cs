using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(Plant),nameof(Plant.GrowthRateFactor_Drought),MethodType.Getter)]
internal static class LocalDroughtPlants
{
    static void Postfix(Plant __instance,ref float __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner?.Environment.Enabled==true&&!owner.Environment.Sky(__instance.Position))__result=1;
    }
}
[HarmonyPatch(typeof(NoxiousHazeUtility),nameof(NoxiousHazeUtility.IsExposedToNoxiousHaze),new[]{typeof(Thing),typeof(IntVec3),typeof(Map)})]
internal static class LocalHazeExposure
{
    static bool Prefix(Thing thing,IntVec3 cell,Map map,ref bool __result)
    {
        var owner=LayerMapComponent.For(map);
        if(owner?.Environment.Enabled!=true)return true;
        __result=ModsConfig.BiotechActive&&!(thing is Pawn pawn&&pawn.kindDef.immuneToGameConditionEffects)&&
            owner.Environment.Concentration(cell,Atmosphere.Smog)>.001f;
        return false;
    }
}
[HarmonyPatch(typeof(Plant),nameof(Plant.GrowthRateFactor_NoxiousHaze),MethodType.Getter)]
internal static class LocalHazePlants
{
    static void Postfix(Plant __instance,ref float __result)
    {
        var owner=LayerMapComponent.For(__instance.Map);
        if(owner?.Environment.Enabled==true)__result=1-.5f*owner.Environment.Concentration(__instance.Position,Atmosphere.Smog);
    }
}
[HarmonyPatch(typeof(StatPart_NoxiousHaze),nameof(StatPart_NoxiousHaze.TransformValue))]
internal static class LocalHazeStats
{
    static bool Prefix(StatRequest req,ref float val,float ___multiplier)
    {
        var thing=req.Thing;var owner=LayerMapComponent.For(thing?.MapHeld);
        if(owner?.Environment.Enabled!=true)return true;
        if(thing!=null&&thing.def.deteriorateFromEnvironmentalEffects&&
            !(thing is Pawn pawn&&pawn.kindDef.immuneToGameConditionEffects))
            val*=1+(___multiplier-1)*owner.Environment.Concentration(thing.PositionHeld,Atmosphere.Smog);
        return false;
    }
}
[HarmonyPatch(typeof(StatPart_NoxiousHaze),nameof(StatPart_NoxiousHaze.ExplanationPart))]
internal static class LocalHazeExplanation
{
    static bool Prefix(StatRequest req,float ___multiplier,ref string? __result)
    {
        var thing=req.Thing;var owner=LayerMapComponent.For(thing?.MapHeld);
        if(owner?.Environment.Enabled!=true)return true;
        float concentration=thing!=null&&thing.def.deteriorateFromEnvironmentalEffects&&
            !(thing is Pawn pawn&&pawn.kindDef.immuneToGameConditionEffects)?
            owner.Environment.Concentration(thing.PositionHeld,Atmosphere.Smog):0;
        __result=concentration>0?"StatsReport_MultiplierFor".Translate(GameConditionDefOf.NoxiousHaze.label)+
            (": x"+(1+(___multiplier-1)*concentration).ToStringPercent()):null;
        return false;
    }
}
[HarmonyPatch(typeof(ThoughtWorker_Aurora),"CurrentStateInternal")]
internal static class LocalAuroraThought
{
    static void Postfix(Pawn p,ref ThoughtState __result)
    {
        var owner=LayerMapComponent.For(p.MapHeld);
        if(__result.Active&&owner?.Environment.Enabled==true&&!owner.Environment.Sky(p.PositionHeld))__result=false;
    }
}
[HarmonyPatch(typeof(ThoughtWorker_GameCondition),"CurrentStateInternal")]
internal static class LocalSporeThought
{
    static void Postfix(ThoughtWorker_GameCondition __instance,Pawn p,ref ThoughtState __result)
    {
        var owner=LayerMapComponent.For(p.MapHeld);
        if(__instance.def.gameCondition?.defName=="BioluminescentSpores"&&__result.Active&&
            owner?.Environment.Enabled==true&&!owner.Environment.Sky(p.PositionHeld))__result=false;
    }
}
[HarmonyPatch(typeof(CompPowerPlantWind),nameof(CompPowerPlantWind.CompTick))]
internal static class UpperWindPower
{
    internal static float Wind(WindManager manager,CompPowerPlantWind comp)
    {
        var owner=LayerMapComponent.For(comp.parent.Map);
        if(owner?.Environment.Enabled!=true)return manager.WindSpeed;
        return owner.Environment.WindExposed(comp.parent.Position)?manager.WindSpeed*owner.Environment.WindFactor(comp.parent.Position):0;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native=AccessTools.PropertyGetter(typeof(WindManager),nameof(WindManager.WindSpeed));int count=0;
        foreach(var code in instructions)
        {
            if(code.Calls(native))
            {
                var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);code.labels.Clear();yield return load;
                code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(UpperWindPower),nameof(Wind));count++;
            }
            yield return code;
        }
        if(count!=1)throw new InvalidOperationException("RimLayers wind-power hook changed.");
    }
}
[HarmonyPatch(typeof(CompPowerPlantWind),"RecalculateBlockages")]
internal static class WindRoofEnvelope
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(RoofGrid),nameof(RoofGrid.Roofed),new[]{typeof(IntVec3)}),
        AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.RoofedGrid)),1);
}
