using HarmonyLib;
using RimLayers.Environment;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void EnvironmentWeatherChecks()
    {
        var actor=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTagIsDisabled(WorkTags.Violent));
        var weather=map.weatherManager.curWeather;var previousPosition=actor.Position;
        var ticksPerMove=AccessTools.Method(typeof(Pawn),"TicksPerMove");
        float Move()=> (float)ticksPerMove.Invoke(actor,new object[]{false})!;
        void Weather(string name)
        {
            map.weatherManager.TransitionTo(DefDatabase<WeatherDef>.GetNamed(name));
            map.weatherManager.curWeatherAge=(int)WeatherManager.TransitionTicks+1;
            owner.Environment.Invalidate();
        }
        Weather("Clear");actor.Position=EnvAt(-1,53,53);actor.Notify_Teleported();float indoorClear=Move();
        Weather("Rain");float indoorRain=Move();
        Check("native-move-speed-in-sealed-basement-ignores-rain-penalty",Math.Abs(indoorClear-indoorRain)<.001f);
        actor.Position=EnvAt(0,72,72);actor.Notify_Teleported();float outdoorRain=Move();
        Weather("Clear");float outdoorClear=Move();
        Check("native-exposed-movement-keeps-rain-penalty",outdoorRain>outdoorClear);
        actor.Position=EnvAt(-1,53,53);actor.Notify_Teleported();
        float Toxic()=>actor.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.ToxicBuildup)?.Severity??0;
        float before=Toxic();LocalAirborneToxicity.Fallout(actor,1);
        Check("native-toxicity-does-not-penetrate-sealed-basement",Toxic()==before);
        actor.Position=EnvAt(0,72,72);actor.Notify_Teleported();LocalAirborneToxicity.Fallout(actor,1);
        Check("native-toxicity-still-affects-exposed-colonist",Toxic()>before);
        Weather("Fog");
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        actor.equipment.DestroyAllEquipment(DestroyMode.Vanish);actor.equipment.AddEquipment(gun);
        var verb=gun.GetComp<CompEquippable>().PrimaryVerb;
        var target=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),EnvAt(0,74,72),map);
        float Report()=> (float)AccessTools.Field(typeof(ShotReport),"factorFromWeather").GetValue(ShotReport.HitReportFor(actor,verb,target))!;
        Check("native-hit-report-uses-exposed-local-fog",Math.Abs(Report()-.5f)<.001f);
        RimLayersMod.Settings.Environment.WeatherPenalty=0;
        Check("zero-weather-penalty-removes-native-report-penalty",Math.Abs(Report()-1)<.001f);
        RimLayersMod.Settings.Environment.WeatherPenalty=1;
        float path=LocalWeather.PathValue(owner,EnvAt(-1,72,72),EnvAt(0,72,72),owner.Environment.Accuracy);
        Check("cross-layer-weather-ray-integrates-affected-segments",path>.5f&&path<1,path.ToString());
        var blind=DefDatabase<WeatherDef>.GetNamedSilentFail("BlindFog");
        if(blind!=null)
        {
            Weather("BlindFog");float cap=map.weatherManager.CurWeatherMaxRangeCap;
            Check("blind-fog-native-cap-is-local-on-the-ray",cap>0&&Math.Abs(LocalWeather.Range(owner,EnvAt(0,72,72),EnvAt(0,74,72),100)-cap)<.001f);
            Check("sealed-basement-does-not-inherit-global-fog-range-cap",LocalWeather.Range(owner,EnvAt(-1,52,52),EnvAt(-1,54,52),100)==100);
        }
        target.Destroy(DestroyMode.Vanish);Weather(weather.defName);actor.Position=previousPosition;actor.Notify_Teleported();
    }
}
