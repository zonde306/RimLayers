using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static bool HasWetMemory()=>pawn.needs.mood.thoughts.memories.Memories.Any(m=>m.def==ThoughtDefOf.SoakingWet);
    private static void CheckWeatherMemory(bool loaded)
    {
        if(loaded)Check("restart-keeps-native-weather-memory",HasWetMemory());
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.needs.mood.CurLevel=1;
        map.weatherManager.TransitionTo(DefDatabase<WeatherDef>.GetNamed("Rain"));
        map.weatherManager.curWeatherAge=(int)WeatherManager.TransitionTicks+1;
        Check("weather-memory-fixture-is-native-rain",map.weatherManager.CurWeatherLerped.weatherThought==ThoughtDefOf.SoakingWet);
        pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.SoakingWet);
        pawn.Position=CoveredSky;pawn.Notify_Teleported();
        pawn.mindState.MindStateTickInterval(120);
        Check("overhead-roof-prevents-real-rain-memory",!HasWetMemory()&&!pawn.Position.Roofed(map)&&!owner.Sky.IsExposed(pawn.Position));
        pawn.Position=ExposedSky;pawn.Notify_Teleported();
        pawn.mindState.MindStateTickInterval(120);
        Check("open-terrace-gains-native-soaking-wet-memory",HasWetMemory());
        pawn.Position=CoveredSky;pawn.Notify_Teleported();
        pawn.mindState.MindStateTickInterval(120);
        Check("shelter-does-not-erase-existing-native-memory",HasWetMemory());
    }
}
