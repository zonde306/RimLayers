namespace RimLayers.Vehicles;

/// <summary>Entire rigid-body segment, including pitch and bank. All input is immutable
/// logical geometry, suitable for worker-side route prediction without touching the map.</summary>
public static class VehicleFlightSweep
{
    public static RampBody Interpolate(RampBody from,RampBody to,double t)=>new(from.Bottom.Lerp(to.Bottom,t),
        from.Yaw+VehicleGeometry.SignedTurn(from.Yaw,to.Yaw)*t,from.Pitch+(to.Pitch-from.Pitch)*t,from.Bank+(to.Bank-from.Bank)*t);
    public static VehicleSweepHit? FirstHit(VehicleHull hull,RampBody from,RampBody to,IReadOnlyList<VehicleObstacle> obstacles)
    {
        if(!hull.Valid||!Finite(from)||!Finite(to))throw new ArgumentOutOfRangeException(nameof(from));
        double angular=Math.Abs(VehicleGeometry.SignedTurn(from.Yaw,to.Yaw))+Math.Abs(to.Pitch-from.Pitch)*180/Math.PI+Math.Abs(to.Bank-from.Bank)*180/Math.PI;
        int steps=Math.Max(1,(int)Math.Ceiling(angular/.5));
        for(int i=0;i<steps;i++)
        {
            var a=Interpolate(from,to,(double)i/steps);var b=Interpolate(from,to,(double)(i+1)/steps);
            VehicleSweepHit? hit=null;
            foreach(var obstacle in obstacles)
                if(VehicleBodySweep.Fraction(hull,a,b,obstacle) is {} fraction)
                {
                    double t=(i+fraction)/steps;
                    if(hit==null||t<hit.Value.Fraction||t==hit.Value.Fraction&&obstacle.Key<hit.Value.ObstacleKey)hit=new(t,obstacle.Key);
                }
            if(hit!=null)return hit;
        }
        return null;
    }
    private static bool Finite(RampBody b)=>double.IsFinite(b.Bottom.X)&&double.IsFinite(b.Bottom.Height)&&double.IsFinite(b.Bottom.Z)&&
        double.IsFinite(b.Yaw)&&double.IsFinite(b.Pitch)&&double.IsFinite(b.Bank);
}
