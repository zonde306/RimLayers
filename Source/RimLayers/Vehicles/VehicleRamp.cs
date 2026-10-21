namespace RimLayers.Vehicles;

/// <summary>A rigid chassis over a six-metre grade. Heights are absolute metres.
/// The chassis clears both grade breaks; it never clips through the upper landing.</summary>
public readonly record struct RampBody(VehiclePoint Bottom,double Yaw,double Pitch,double Bank=0);
public readonly record struct VehicleRamp(double StartX,double StartZ,double Yaw,double BaseHeight)
{
    public double Along(double x,double z)
    {
        double a=Yaw*Math.PI/180;return (x-StartX)*Math.Sin(a)+(z-StartZ)*Math.Cos(a);
    }
    public double Surface(double along)=>BaseHeight+Math.Clamp(along/VehicleGeometry.RampLength,0,1)*VehicleGeometry.StoreyHeight;
    public RampBody Body(VehicleHull hull,double x,double z)
    {
        double along=Along(x,z),axle=Math.Max(.25,hull.Length*.35);
        double pitch=Math.Atan2(Surface(along+axle)-Surface(along-axle),2*axle);
        double half=hull.Length*Math.Cos(pitch)/2,slope=Math.Tan(pitch);
        double bottom=Math.Max(Surface(along-half)+slope*half,Surface(along+half)-slope*half);
        foreach(double junction in new[]{0d,(double)VehicleGeometry.RampLength})
            if(junction>=along-half&&junction<=along+half)bottom=Math.Max(bottom,Surface(junction)-slope*(junction-along));
        return new(new(x,bottom,z),Yaw,pitch);
    }
}

/// <summary>Continuous SAT translation of the oriented 3D chassis. Angular changes are
/// enclosed by a conservative radius bound, so sampling cannot tunnel through a thin roof.</summary>
public static class VehicleBodySweep
{
    private readonly record struct V(double X,double Y,double Z)
    {
        public static V operator +(V a,V b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static V operator -(V a,V b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static V operator *(V a,double b)=>new(a.X*b,a.Y*b,a.Z*b);
        public double Dot(V b)=>X*b.X+Y*b.Y+Z*b.Z;
        public V Cross(V b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public double Length=>Math.Sqrt(Dot(this));
    }
    public static bool Intersects(VehicleHull hull,RampBody from,RampBody to,VehicleObstacle obstacle)=>Fraction(hull,from,to,obstacle).HasValue;
    public static double? Fraction(VehicleHull hull,RampBody from,RampBody to,VehicleObstacle obstacle)
    {
        double a=from.Yaw*Math.PI/180,p=from.Pitch;
        var right=new V(Math.Cos(a),0,-Math.Sin(a));
        var forward=new V(Math.Sin(a)*Math.Cos(p),Math.Sin(p),Math.Cos(a)*Math.Cos(p));
        var up=new V(-Math.Sin(a)*Math.Sin(p),Math.Cos(p),-Math.Cos(a)*Math.Sin(p));
        var flatRight=right;var flatUp=up;
        right=flatRight*Math.Cos(from.Bank)+flatUp*Math.Sin(from.Bank);
        up=flatUp*Math.Cos(from.Bank)-flatRight*Math.Sin(from.Bank);
        var centre=new V(from.Bottom.X,from.Bottom.Height,from.Bottom.Z)+up*(hull.Height/2);
        var movement=new V(to.Bottom.X-from.Bottom.X,to.Bottom.Height-from.Bottom.Height,to.Bottom.Z-from.Bottom.Z);
        var target=new V((obstacle.MinX+obstacle.MaxX)/2,(obstacle.Bottom+obstacle.Top)/2,(obstacle.MinZ+obstacle.MaxZ)/2);
        var extents=new V((obstacle.MaxX-obstacle.MinX)/2,(obstacle.Top-obstacle.Bottom)/2,(obstacle.MaxZ-obstacle.MinZ)/2);
        double angular=Math.Abs(to.Bank-from.Bank)+Math.Abs(to.Pitch-from.Pitch)+Math.Abs(VehicleGeometry.SignedTurn(from.Yaw,to.Yaw))*Math.PI/180;
        double padding=Math.Sqrt(hull.Width*hull.Width+hull.Length*hull.Length+hull.Height*hull.Height)*angular;
        // Cheap conservative world-axis bounds before allocating the SAT axis set. Air
        // route prediction calls this for thousands of separated floor/column candidates.
        double rx=Math.Abs(right.X)*hull.Width/2+Math.Abs(forward.X)*hull.Length/2+Math.Abs(up.X)*hull.Height/2+padding;
        double ry=Math.Abs(right.Y)*hull.Width/2+Math.Abs(forward.Y)*hull.Length/2+Math.Abs(up.Y)*hull.Height/2+padding;
        double rz=Math.Abs(right.Z)*hull.Width/2+Math.Abs(forward.Z)*hull.Length/2+Math.Abs(up.Z)*hull.Height/2+padding;
        if(Math.Max(centre.X,centre.X+movement.X)+rx<obstacle.MinX||Math.Min(centre.X,centre.X+movement.X)-rx>obstacle.MaxX||
           Math.Max(centre.Y,centre.Y+movement.Y)+ry<obstacle.Bottom||Math.Min(centre.Y,centre.Y+movement.Y)-ry>obstacle.Top||
           Math.Max(centre.Z,centre.Z+movement.Z)+rz<obstacle.MinZ||Math.Min(centre.Z,centre.Z+movement.Z)-rz>obstacle.MaxZ)return null;
        V[] body={right,forward,up},world={new(1,0,0),new(0,1,0),new(0,0,1)};
        var axes=body.Concat(world).Concat(body.SelectMany(b=>world.Select(b.Cross)));
        double enter=0,leave=1;
        foreach(var axis in axes)
        {
            double length=axis.Length;if(length<1e-9)continue;
            double radius=Math.Abs(axis.Dot(right))*hull.Width/2+Math.Abs(axis.Dot(forward))*hull.Length/2+
                Math.Abs(axis.Dot(up))*hull.Height/2+Math.Abs(axis.X)*extents.X+Math.Abs(axis.Y)*extents.Y+Math.Abs(axis.Z)*extents.Z+padding*length-1e-7;
            double offset=axis.Dot(centre-target),speed=axis.Dot(movement);
            if(Math.Abs(speed)<1e-12){if(Math.Abs(offset)>=radius)return null;continue;}
            double first=(-radius-offset)/speed,last=(radius-offset)/speed;
            if(first>last)(first,last)=(last,first);
            enter=Math.Max(enter,first);leave=Math.Min(leave,last);
            if(enter>=leave)return null;
        }
        return enter<leave?enter:null;
    }
}
