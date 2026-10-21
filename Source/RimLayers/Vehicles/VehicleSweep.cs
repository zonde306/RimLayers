namespace RimLayers.Vehicles;

public readonly record struct VehicleObstacle(double MinX,double Bottom,double MinZ,double MaxX,double Top,double MaxZ,int Key);
public readonly record struct VehicleSweepHit(double Fraction,int ObstacleKey);

/// <summary>Continuous translation sweep. Rotation is conservatively enclosed in small angular intervals.</summary>
public static class VehicleSweep
{
    public static VehicleSweepHit? FirstHit(VehicleHull hull,VehiclePose from,VehiclePose to,IEnumerable<VehicleObstacle> obstacles)
    {
        if(!hull.Valid)throw new ArgumentOutOfRangeException(nameof(hull));
        // Footprint validates every pose component even when the obstacle set is empty.
        _=VehicleGeometry.Footprint(hull,from).First();_=VehicleGeometry.Footprint(hull,to).First();
        double turn=VehicleGeometry.SignedTurn(from.Heading,to.Heading);
        int segments=Math.Max(1,(int)Math.Ceiling(Math.Abs(turn)/2));
        var boxes=obstacles.ToArray();
        double radius=Math.Sqrt(hull.Width*hull.Width+hull.Length*hull.Length)/2;
        double padding=2*radius*Math.Sin(Math.Abs(turn)/segments*Math.PI/720);
        var expanded=hull with{Width=hull.Width+2*padding,Length=hull.Length+2*padding};
        for(int i=0;i<segments;i++)
        {
            var a=VehicleGeometry.Interpolate(from,to,(double)i/segments);
            var b=VehicleGeometry.Interpolate(from,to,(double)(i+1)/segments);
            double heading=VehicleGeometry.Interpolate(from,to,(i+.5)/segments).Heading;
            VehicleSweepHit? best=null;
            foreach(var box in boxes)
            {
                if(!Valid(box))throw new ArgumentOutOfRangeException(nameof(obstacles));
                if(Sweep(expanded,a.Position,b.Position,heading,box) is not {} hit)continue;
                double t=(i+hit)/segments;
                if(best==null||t<best.Value.Fraction||t==best.Value.Fraction&&box.Key<best.Value.ObstacleKey)
                    best=new(t,box.Key);
            }
            if(best!=null)return best;
        }
        return null;
    }
    private static bool Valid(VehicleObstacle b)=>double.IsFinite(b.MinX)&&double.IsFinite(b.MaxX)&&double.IsFinite(b.Bottom)&&
        double.IsFinite(b.Top)&&double.IsFinite(b.MinZ)&&double.IsFinite(b.MaxZ)&&b.MinX<b.MaxX&&b.MinZ<b.MaxZ&&b.Bottom<=b.Top;
    private static double? Sweep(VehicleHull hull,VehiclePoint a,VehiclePoint b,double heading,VehicleObstacle box)
    {
        double begin=0,end=1,angle=heading*Math.PI/180,c=Math.Cos(angle),s=-Math.Sin(angle);
        double hx=hull.Width/2,hz=hull.Length/2;
        double bx=(box.MaxX-box.MinX)/2,bz=(box.MaxZ-box.MinZ)/2;
        double x=a.X-(box.MinX+box.MaxX)/2,z=a.Z-(box.MinZ+box.MaxZ)/2;
        double dx=b.X-a.X,dz=b.Z-a.Z;
        bool Axis(double start,double velocity,double extent)
        {
            if(Math.Abs(velocity)<1e-12)return Math.Abs(start)<extent-1e-9;
            double lo=(-extent-start)/velocity,hi=(extent-start)/velocity;
            if(lo>hi)(lo,hi)=(hi,lo);
            begin=Math.Max(begin,lo);end=Math.Min(end,hi);
            return begin<end-1e-10;
        }
        if(!Axis(x,dx,bx+Math.Abs(c)*hx+Math.Abs(s)*hz)||
           !Axis(z,dz,bz+Math.Abs(s)*hx+Math.Abs(c)*hz)||
           !Axis(x*c+z*s,dx*c+dz*s,hx+Math.Abs(c)*bx+Math.Abs(s)*bz)||
           !Axis(-x*s+z*c,-dx*s+dz*c,hz+Math.Abs(s)*bx+Math.Abs(c)*bz))return null;
        // Ground contact is permitted; crossing a horizontal plane in either direction is not.
        double y=a.Height+hull.Height/2-(box.Bottom+box.Top)/2;
        if(!Axis(y,b.Height-a.Height,hull.Height/2+(box.Top-box.Bottom)/2))return null;
        return begin>=0&&begin<=1?begin:null;
    }
}
