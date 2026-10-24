namespace RimLayers.Environment;

/// <summary>Pure, deterministic climate/geology policy shared by runtime and batch tests.</summary>
public static class EnvironmentMath
{
    public static float RockTarget(float annualMean,int depth,float offset,float gradient)=>annualMean+offset+gradient*depth;
    public static float Follow(float value,float target,int ticks,float days,int depth)=>
        target+(value-target)*(float)Math.Exp(-ticks/(60000d*days*depth*depth));
    public static float Decay(float value,int ticks,float halfLifeHours)=>value*(float)Math.Pow(.5,ticks/(2500d*halfLifeHours));
    public static float MineralBudgetFactor(int depth,float growth,float cap)=>Math.Min(cap,1+growth*Math.Max(0,depth-1));
    public static float LavaFraction(int depth,int start,float initial,float growth,float cap)=>
        depth<start?0:(float)Math.Min(cap,initial*Math.Pow(growth,depth-start));
    public static float DepthWeight(int depth,float b5,float b9)
    {
        if(depth<=1)return 1;
        if(depth<=5)return 1+(b5-1)*(depth-1)/4;
        return b5+(b9-b5)*Math.Min(4,depth-5)/4;
    }
    public static float TemperatureScore(float temperature,float cold,float hot)=>
        Math.Clamp((temperature-cold)/10,0,1)*Math.Clamp((hot-temperature)/10,0,1);
    public static float Wind(int elevation,float strength)=>(float)Math.Pow(1+Math.Max(0,elevation),.2*strength);
    // Underground ecology counts. Kept here so batch tests can prove the shape without a Map.
    public static int SoilCells(int candidates,float coverage)=>coverage<=0||candidates<=0?0:
        Math.Min(candidates,(int)Math.Round(candidates*(double)Math.Min(1,coverage),MidpointRounding.AwayFromZero));
    public static int GeyserCount(int area,float per10K)=>per10K<=0||area<=0?0:
        (int)Math.Round((double)area/10000*per10K,MidpointRounding.AwayFromZero);
    public static int PlantCount(int soilCells,float density)=>soilCells<=0||density<=0?0:
        (int)Math.Round(soilCells*PerCellFloraDensity*(double)density,MidpointRounding.AwayFromZero);
    public const float PerCellFloraDensity=.06f;
    /// <summary>Cut-off that keeps exactly the top <paramref name="coverage"/> fraction of values.</summary>
    public static double QuantileThreshold(List<double> values,float coverage)
    {
        if(values.Count==0)return double.MaxValue;
        if(coverage<=0)return double.MaxValue;
        if(coverage>=1)return double.MinValue;
        var sorted=new List<double>(values);
        sorted.Sort();
        int keep=(int)Math.Round(sorted.Count*(double)Math.Min(1,coverage),MidpointRounding.AwayFromZero);
        keep=Math.Clamp(keep,1,sorted.Count);
        return sorted[^keep];
    }
    // Smooth spatial bands, independently offset by resource, host and adjacent depth.
    // Depth changes the phase slowly; the cells are never copied from a preceding layer.
    public static double Band(int seed,int x,int z,int depth)
    {
        double phase=(uint)Mix(seed)* (Math.PI*2/uint.MaxValue);
        return .08+.92*Math.Pow(.5+.5*Math.Sin(x*.057+z*.031+Math.Sin(z*.019+phase)*1.8+depth*.19+phase),4);
    }
    public static int Mix(int value)
    {
        unchecked { uint x=(uint)value;x^=x>>16;x*=0x7feb352d;x^=x>>15;x*=0x846ca68b;x^=x>>16;return (int)x; }
    }
    public static int StableHash(string text)
    {unchecked{int hash=5381;foreach(char c in text)hash=(hash*33)^c;return hash;}}
}
