namespace RimLayers;

/// <summary>Pure rules shared by path costs, combat queries and focused tests.</summary>
public static class SettingsMath
{
    public static int BackgroundQueueBase(int depth)=>1900-depth*100;
    public static int TravelTicks(int baseline,float multiplier) => Math.Max(1,(int)Math.Round(baseline*multiplier,MidpointRounding.AwayFromZero));
    // Scale the existing capped height effect; multiplier zero restores native range.
    public static float RangeMultiplier(int elevationDifference,bool curved,float strength) =>
        curved?1:1+Math.Clamp(.1f*elevationDifference,-.2f,.2f)*strength;
    public static int MaterialCount(int baseline,float multiplier) => baseline<=0?baseline:Math.Max(1,(int)Math.Round(baseline*multiplier,MidpointRounding.AwayFromZero));
}
