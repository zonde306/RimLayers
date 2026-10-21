namespace RimLayers.Combat;

public readonly record struct ExposureSample(ShotPoint Aim,ShotObstruction? Obstruction)
{
    public bool Clear => Obstruction==null;
}

/// <summary>Fixed, deterministic silhouette samples. No RNG in queries, UI or AI.</summary>
public static class ExposureGeometry
{
    public static ShotPoint[] Body(ShotPoint start,ShotPoint center,double bottom,double height,double halfWidth)
    {
        double dx=center.X-start.X,dz=center.Z-start.Z,length=Math.Sqrt(dx*dx+dz*dz);
        double sideX=length>1e-9 ? -dz/length : 1,sideZ=length>1e-9 ? dx/length : 0;
        var result=new ShotPoint[9];int n=0;
        foreach(double level in new[]{0.25,0.60,0.85})
            foreach(double side in new[]{-1d,0d,1d})
                result[n++]=new(center.X+sideX*halfWidth*side,bottom+height*level,center.Z+sideZ*halfWidth*side);
        return result;
    }
    public static ShotPoint Point(ShotPoint start,ShotPoint end,double arc,double t)
    {
        var point=start.At(end,t);
        return point with { Height=point.Height+4*arc*t*(1-t) };
    }
    public static ShotObstruction? First(ShotPoint start,ShotPoint end,double arc,IShotScene scene)
    {
        int pieces=Math.Max(1,(int)Math.Ceiling(Math.Sqrt(arc/0.01)));
        for(int i=0;i<pieces;i++)
        {
            double low=(double)i/pieces,high=(double)(i+1)/pieces;
            var hit=ShotGeometry.First(Point(start,end,arc,low),Point(start,end,arc,high),scene);
            if(hit is {} contact) return contact with { Time=low+(high-low)*contact.Time };
        }
        return null;
    }
}
