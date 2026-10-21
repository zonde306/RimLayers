namespace RimLayers.Vehicles;

/// <summary>Logical metres/cells only. Storage-band offsets must never enter motion geometry.</summary>
public readonly record struct VehiclePoint(double X, double Height, double Z)
{
    public VehiclePoint Lerp(VehiclePoint end,double t)=>new(X+(end.X-X)*t,Height+(end.Height-Height)*t,Z+(end.Z-Z)*t);
}
public readonly record struct VehicleHull(double Width,double Length,double Height)
{
    public bool Valid=>double.IsFinite(Width)&&double.IsFinite(Length)&&double.IsFinite(Height)&&Width>0&&Length>0&&Height>0;
}
public readonly record struct VehiclePose(VehiclePoint Position,double Heading);
public readonly record struct VehicleCell(int X,int Z);

public static class VehicleGeometry
{
    public const double StoreyHeight=3;
    public const int RampLength=6;
    public const int LiftSize=9;
    private const double Epsilon=1e-7;

    /// <summary>Cells intersecting the oriented hull, using the separating-axis test (not just corners).</summary>
    public static IEnumerable<VehicleCell> Footprint(VehicleHull hull,VehiclePose pose)
    {
        Validate(hull,pose);
        double a=pose.Heading*Math.PI/180,c=Math.Cos(a),s=-Math.Sin(a);
        double hw=hull.Width/2,hl=hull.Length/2;
        double rx=Math.Abs(c)*hw+Math.Abs(s)*hl,rz=Math.Abs(s)*hw+Math.Abs(c)*hl;
        int minX=(int)Math.Floor(pose.Position.X-rx+Epsilon),maxX=(int)Math.Ceiling(pose.Position.X+rx-Epsilon)-1;
        int minZ=(int)Math.Floor(pose.Position.Z-rz+Epsilon),maxZ=(int)Math.Ceiling(pose.Position.Z+rz-Epsilon)-1;
        for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
        {
            double dx=x+.5-pose.Position.X,dz=z+.5-pose.Position.Z;
            if(Math.Abs(dx*c+dz*s)<hw+.5*(Math.Abs(c)+Math.Abs(s))-Epsilon&&
               Math.Abs(-dx*s+dz*c)<hl+.5*(Math.Abs(c)+Math.Abs(s))-Epsilon)
                yield return new(x,z);
        }
    }
    public static bool FitsLift(VehicleHull hull,double heading)
    {
        if(!hull.Valid||!double.IsFinite(heading))return false;
        double a=heading*Math.PI/180,c=Math.Abs(Math.Cos(a)),s=Math.Abs(Math.Sin(a));
        return hull.Width*c+hull.Length*s<=LiftSize+Epsilon&&hull.Width*s+hull.Length*c<=LiftSize+Epsilon;
    }
    public static double SignedTurn(double from,double to)=>((to-from)%360+540)%360-180;
    public static VehiclePose Interpolate(VehiclePose from,VehiclePose to,double t)=>
        new(from.Position.Lerp(to.Position,t),from.Heading+SignedTurn(from.Heading,to.Heading)*t);
    private static void Validate(VehicleHull hull,VehiclePose pose)
    {
        if(!hull.Valid||!double.IsFinite(pose.Position.X)||!double.IsFinite(pose.Position.Z)||
           !double.IsFinite(pose.Position.Height)||!double.IsFinite(pose.Heading))
            throw new ArgumentOutOfRangeException(nameof(pose),"Vehicle geometry requires a finite pose and positive dimensions.");
    }
}
