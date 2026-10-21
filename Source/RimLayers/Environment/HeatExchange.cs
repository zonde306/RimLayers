namespace RimLayers.Environment;

public readonly record struct HeatVolume(double Temperature,double Capacity,bool Reservoir=false);
public readonly record struct HeatEdge(int A,int B,double Conductance);

/// <summary>One simultaneous conservative step, in native room heat units (degree * cell).</summary>
public static class HeatExchange
{
    public static float OutdoorChange(float temperature,float outdoor,float coverage,bool thinRoof)
    {
        if(coverage<0.001f)return 0;
        float diff=outdoor-temperature;
        if(Math.Abs(diff)>=100)diff=Math.Sign(diff)*100+5*(diff-Math.Sign(diff)*100);
        return diff*coverage*(thinRoof?5E-05f:0.0007f)*120f;
    }
    public static double[] Calculate(ReadOnlySpan<HeatVolume> volumes,ReadOnlySpan<HeatEdge> edges)
    {
        var degree=new double[volumes.Length];var energy=new double[volumes.Length];
        foreach(var edge in edges)
        { degree[edge.A]+=edge.Conductance;degree[edge.B]+=edge.Conductance; }
        foreach(var edge in edges)
        {
            var a=volumes[edge.A];var b=volumes[edge.B];
            double scale=1;
            if(!a.Reservoir) scale=Math.Min(scale,0.25*a.Capacity/degree[edge.A]);
            if(!b.Reservoir) scale=Math.Min(scale,0.25*b.Capacity/degree[edge.B]);
            double transfer=(a.Temperature-b.Temperature)*edge.Conductance*scale;
            energy[edge.A]-=transfer;energy[edge.B]+=transfer;
        }
        return energy;
    }
}
