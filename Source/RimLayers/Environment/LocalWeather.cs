using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Combat;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

public static class LocalWeather
{
    public static float PathExposure(LayerMapComponent owner,IntVec3 source,IntVec3 target)
    {
        if(!owner.Environment.Enabled||!owner.TryDecode(source,out _)||!owner.TryDecode(target,out _))return 1;
        var scene=new LayerShotScene(owner);
        var a=scene.Point(source);var b=scene.Point(target);
        double result=0;
        foreach(var span in ShotGeometry.Cells(a,b,3))
            if(owner.Layout!.TryGetLayerAtElevation(span.Layer,out int layer)&&
                owner.Layout.Contains(new(layer,span.X,span.Z)))
                result+=(span.End-span.Start)*owner.Environment.WeatherExposure(owner.ToStorage(new(layer,span.X,span.Z)));
        return (float)Math.Clamp(result,0,1);
    }
    public static float Range(LayerMapComponent owner,IntVec3 source,IntVec3 target,float original)
    {
        float cap=owner.map.weatherManager.CurWeatherMaxRangeCap;
        if(!owner.Environment.Enabled||!LayerEventPolicy.Known(owner.map.weatherManager.curWeather))return original;
        float inverse=PathValue(owner,source,target,owner.Environment.InverseRange)*LayerEnvironment.Settings.WeatherPenalty;
        return inverse<=0?original:Math.Min(original,1/inverse);
    }
    internal static float PathValue(LayerMapComponent owner,IntVec3 source,IntVec3 target,Func<IntVec3,float> value)
    {
        if(!owner.TryDecode(source,out _)||!owner.TryDecode(target,out _))return 0;
        var scene=new LayerShotScene(owner);var a=scene.Point(source);var b=scene.Point(target);double result=0;
        foreach(var span in ShotGeometry.Cells(a,b,3))
            if(owner.Layout!.TryGetLayerAtElevation(span.Layer,out int layer)&&owner.Layout.Contains(new(layer,span.X,span.Z)))
                result+=(span.End-span.Start)*value(owner.ToStorage(new(layer,span.X,span.Z)));
        return (float)result;
    }
    private static readonly AccessTools.FieldRef<WeatherManager,Map> WeatherMap=AccessTools.FieldRefAccess<WeatherManager,Map>("map");
    internal static bool AshRoofed(IntVec3 cell,Map map)=>LayerMapComponent.For(map) is {} o&&o.Environment.Enabled?
        o.Environment.Concentration(cell,Atmosphere.Ash)<.25f:cell.Roofed(map);
    internal static float DeferredRangeCap(WeatherManager weather)=>LayerMapComponent.For(
        WeatherMap(weather))?.Environment.Enabled==true&&LayerEventPolicy.Known(weather.curWeather)?-1:weather.CurWeatherMaxRangeCap;
    internal static bool DeathCandidate(Corpse corpse,bool ignoreIndoors)
    {
        var owner=LayerMapComponent.For(corpse.MapHeld);
        if(owner?.Environment.Enabled!=true)return MutantUtility.CanResurrectAsShambler(corpse,ignoreIndoors);
        return owner.Environment.Concentration(corpse.PositionHeld,Atmosphere.Death)>=.5f&&MutantUtility.CanResurrectAsShambler(corpse,true);
    }
}
[HarmonyPatch(typeof(Pawn),"TicksPerMove")]
internal static class LocalWeatherMovement
{
    internal static bool OutdoorForMove(RoofGrid grid,IntVec3 cell, Pawn pawn)=>
        LayerMapComponent.For(pawn.Map)?.Environment.Enabled==true&&LayerEventPolicy.Known(pawn.Map.weatherManager.curWeather)?false:grid.Roofed(cell);
    internal static float MoveMultiplier(WeatherManager weather,Pawn pawn)
    {
        var owner=LayerMapComponent.For(pawn.Map);
        if(owner?.Environment.Enabled!=true||!LayerEventPolicy.Known(weather.curWeather))return weather.CurMoveSpeedMultiplier;
        return Math.Max(.05f,1+(owner.Environment.Move(pawn.Position)-1)*LayerEnvironment.Settings.WeatherPenalty);
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var roof=AccessTools.Method(typeof(RoofGrid),nameof(RoofGrid.Roofed),new[]{typeof(IntVec3)});
        var speed=AccessTools.PropertyGetter(typeof(WeatherManager),nameof(WeatherManager.CurMoveSpeedMultiplier));
        int found=0;
        foreach(var code in instructions)
        {
            string? helper=code.Calls(roof)?nameof(OutdoorForMove):code.Calls(speed)?nameof(MoveMultiplier):null;
            if(helper!=null)
            {
                var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);code.labels.Clear();yield return load;
                code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(LocalWeatherMovement),helper);found++;
            }
            yield return code;
        }
        if(found!=2)throw new InvalidOperationException("RimLayers local movement weather hooks changed.");
    }
}
[HarmonyPatch(typeof(ShotReport),nameof(ShotReport.HitReportFor))]
internal static class LocalWeatherAccuracy
{
    private static readonly AccessTools.StructFieldRef<ShotReport,float> Weather=AccessTools.StructFieldRefAccess<ShotReport,float>("factorFromWeather");
    static void Postfix(Thing caster,Verb verb,LocalTargetInfo target,ref ShotReport __result)
    {
        var owner=LayerMapComponent.For(caster.Map);
        if(owner?.Environment.Enabled!=true||!LayerEventPolicy.Known(caster.Map.weatherManager.curWeather)||verb.EquipmentSource?.TryGetComp<CompUniqueWeapon>()?.IgnoreAccuracyMaluses==true)return;
        Weather(ref __result)=Math.Max(0,1+(LocalWeather.PathValue(owner,caster.Position,target.Cell,owner.Environment.Accuracy)-1)*LayerEnvironment.Settings.WeatherPenalty);
    }
}
// Remove the target-independent fog cap only from native range calculation; enforce it on the actual ray below.
[HarmonyPatch(typeof(VerbProperties),nameof(VerbProperties.AdjustedRange))]
internal static class LocalFogRangeBase
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.PropertyGetter(typeof(WeatherManager),nameof(WeatherManager.CurWeatherMaxRangeCap)),
        AccessTools.Method(typeof(LocalWeather),nameof(LocalWeather.DeferredRangeCap)),2);
}
[HarmonyPatch(typeof(Verb),nameof(Verb.OutOfRange))]
internal static class LocalFogRayRange
{
    static void Postfix(Verb __instance,IntVec3 root,LocalTargetInfo targ,ref bool __result)
    {
        var owner=LayerMapComponent.For(__instance.caster?.Map);
        if(__result||owner?.Environment.Enabled!=true||__instance.verbProps.IsMeleeAttack||
            __instance.EquipmentSource?.TryGetComp<CompUniqueWeapon>()?.IgnoreAccuracyMaluses==true)return;
        if(!owner.TryDecode(root,out var a)||!owner.TryDecode(targ.Cell,out var b))return;
        float range=LocalWeather.Range(owner,root,targ.Cell,owner.Combat.EffectiveRange(__instance,root,targ.Cell));
        __result=(a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z)>range*range;
    }
}
internal static class LocalAirborneToxicity
{
    internal static void Fallout(Pawn p,float extraFactor)
    {
        var owner=LayerMapComponent.For(p.Map);
        if(!p.Spawned||owner?.Environment.Enabled!=true){ToxicUtility.DoAirbornePawnToxicDamage(p,extraFactor);return;}
        float concentration=owner.Environment.Concentration(p.Position,Atmosphere.Toxic);
        if(concentration>0)ToxicUtility.DoPawnToxicDamage(p,extraFactor*concentration);
    }
    private static readonly AccessTools.FieldRef<WeatherWorker,WeatherDef> Weather=AccessTools.FieldRefAccess<WeatherWorker,WeatherDef>("def");
    internal static void Rain(Pawn p,float extraFactor,WeatherWorker worker)
    {
        var owner=LayerMapComponent.For(p.Map);
        if(Weather(worker).defName!="ToxRain"||!p.Spawned||owner?.Environment.Enabled!=true)
        {ToxicUtility.DoAirbornePawnToxicDamage(p,extraFactor);return;}
        if(owner.Environment.Sky(p.Position))ToxicUtility.DoPawnToxicDamage(p,extraFactor);
    }
}
[HarmonyPatch(typeof(GameCondition_ToxicFallout),"DoPawnsToxicDamage")]
internal static class FalloutConcentrationDamage
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(ToxicUtility),nameof(ToxicUtility.DoAirbornePawnToxicDamage)),
        AccessTools.Method(typeof(LocalAirborneToxicity),nameof(LocalAirborneToxicity.Fallout)),1);
}
[HarmonyPatch(typeof(WeatherWorker),nameof(WeatherWorker.WeatherTick))]
internal static class ToxicRainContact
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native=AccessTools.Method(typeof(ToxicUtility),nameof(ToxicUtility.DoAirbornePawnToxicDamage));int count=0;
        foreach(var code in instructions)
        {
            if(code.Calls(native))
            {
                var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);code.labels.Clear();yield return load;
                code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(LocalAirborneToxicity),nameof(LocalAirborneToxicity.Rain));count++;
            }
            yield return code;
        }
        if(count!=1)throw new InvalidOperationException("RimLayers toxic rain contact hook changed.");
    }
}
[HarmonyPatch(typeof(GameCondition_ToxicFallout),"DoCellSteadyEffects")]
internal static class LocalToxicPlantEffects
{
    internal static bool Sheltered(IntVec3 c,Map map)=>LayerMapComponent.For(map) is {} o&&o.Environment.Enabled?
        o.Environment.Concentration(c,Atmosphere.Toxic)<=0:c.Roofed(map);
    internal static float Scale(float value,IntVec3 c,Map map)=>LayerMapComponent.For(map) is {} o&&o.Environment.Enabled?
        value*o.Environment.Concentration(c,Atmosphere.Toxic):value;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int roofs=0,values=0;var roof=AccessTools.Method(typeof(GridsUtility),nameof(GridsUtility.Roofed));
        foreach(var code in instructions)
        {
            if(code.Calls(roof)){code.operand=AccessTools.Method(typeof(LocalToxicPlantEffects),nameof(Sheltered));roofs++;}
            yield return code;
            if(code.opcode==OpCodes.Ldc_R4&&code.operand is float f&&(f==.0065f||f==3000f))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_1);yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(LocalToxicPlantEffects),nameof(Scale)));values++;
            }
        }
        if(roofs!=1||values!=2)throw new InvalidOperationException("RimLayers fallout plant concentration hooks changed.");
    }
}
[HarmonyPatch(typeof(SteadyEnvironmentEffects),"DoCellSteadyEffects")]
internal static class ResidualToxicPlantEffects
{
    private static readonly GameCondition_ToxicFallout effects=new();
    static void Postfix(IntVec3 c,Map ___map)
    {
        var owner=LayerMapComponent.For(___map);
        if(owner?.Environment.HasResidual(Atmosphere.Toxic)==true&&owner.Environment.Concentration(c,Atmosphere.Toxic)>0)
            effects.DoCellSteadyEffects(c,___map);
    }
}
[HarmonyPatch(typeof(BloodRainUtility),nameof(BloodRainUtility.ExposedToBloodRain))]
internal static class LocalBloodRain
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(GridsUtility),nameof(GridsUtility.Roofed)),AccessTools.Method(typeof(SkyQueries),nameof(SkyQueries.Roofed)),1);
}
[HarmonyPatch(typeof(GameCondition_VolcanicAsh),nameof(GameCondition_VolcanicAsh.GameConditionTick))]
internal static class LocalAshEffects
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(GridsUtility),nameof(GridsUtility.Roofed)),AccessTools.Method(typeof(LocalWeather),nameof(LocalWeather.AshRoofed)),1);
}
[HarmonyPatch(typeof(GameCondition_DeathPall),nameof(GameCondition_DeathPall.GameConditionTick))]
internal static class LocalDeathPallEffects
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(MutantUtility),nameof(MutantUtility.CanResurrectAsShambler)),AccessTools.Method(typeof(LocalWeather),nameof(LocalWeather.DeathCandidate)),1);
}
[HarmonyPatch(typeof(WeatherEvent_LightningStrike),nameof(WeatherEvent_LightningStrike.DoStrike))]
internal static class LocalLightningStrike
{
    internal static bool Prefix(ref IntVec3 strikeLoc,Map map)
    {
        var owner=LayerMapComponent.For(map);
        if(owner?.Environment.Enabled!=true)return true;
        int x,z;
        if(strikeLoc.IsValid&&owner.TryDecode(strikeLoc,out var c)){x=c.X;z=c.Z;}
        else{x=Rand.Range(0,owner.Layout!.Width);z=Rand.Range(0,owner.Layout!.Height);}
        foreach(int layer in owner.Layout!.OrderedLayers.Reverse())
        {
            var at=owner.ToStorage(new(layer,x,z));
            if(!owner.Structure.HasSurface(at)||!owner.Environment.Sky(at)||!at.Standable(map))continue;
            strikeLoc=at;return true;
        }
        // The one randomly chosen geographic column is blocked. Do not duplicate or relocate its strike.
        return false;
    }
}
[HarmonyPatch(typeof(SteadyEnvironmentEffects),"DoCellSteadyEffects")]
internal static class MagmaHeatMultiplier
{
    internal static float Heat(TerrainDef terrain)=>terrain.heatPerTick*
        (terrain.defName is "RL_LavaDeep" or "RL_LavaShallow"?LayerEnvironment.Settings.LavaHeat:1);
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var field=AccessTools.Field(typeof(TerrainDef),nameof(TerrainDef.heatPerTick));int count=0;
        foreach(var code in instructions)
        {
            if(code.opcode==OpCodes.Ldfld&&Equals(code.operand,field))
            {code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(MagmaHeatMultiplier),nameof(Heat));count++;}
            yield return code;
        }
        if(count!=2)throw new InvalidOperationException("RimLayers native magma heat hook changed.");
    }
}

[HarmonyPatch(typeof(SteadyEnvironmentEffects),"DoCellSteadyEffects")]
internal static class DeepMagmaRoomHeat
{
    static void Postfix(IntVec3 c,Map ___map)
    {
        var terrain=c.GetTerrain(___map);
        if(terrain.defName!="RL_LavaDeep"||c.GetRoom(___map)!=null||LayerEnvironment.Settings.LavaHeat<=0)return;
        var owner=LayerMapComponent.For(___map);
        if(owner==null||!owner.TryDecode(c,out var local))return;
        var contacts=new Dictionary<Room,int>();
        foreach(var offset in GenAdj.CardinalDirections)
        {
            var next=c+offset;
            if(!owner.TryDecode(next,out var q)||q.LayerId!=local.LayerId)continue;
            var room=next.GetRoom(___map);
            if(room!=null&&!room.UsesOutdoorTemperature)contacts[room]=contacts.GetValueOrDefault(room)+1;
        }
        int faces=contacts.Values.Sum();
        foreach(var contact in contacts)
            contact.Key.PushHeat(terrain.heatPerTick*1666.6666f*LayerEnvironment.Settings.LavaHeat*contact.Value/faces);
    }
}
