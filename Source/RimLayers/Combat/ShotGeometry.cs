namespace RimLayers.Combat;

public readonly record struct ShotPoint(double X, double Height, double Z)
{
    public ShotPoint At(ShotPoint end, double t) =>
        new(X + (end.X - X) * t, Height + (end.Height - Height) * t, Z + (end.Z - Z) * t);
}
public enum ObstructionKind { Floor, Roof, Wall }
public readonly record struct ShotObstruction(ObstructionKind Kind, int Layer, int X, int Z, double Time, int ThingKey = 0);
public interface IShotScene
{
    int Layers { get; }
    int MinLayer => 0;
    int MaxLayer => Layers-1;
    double LayerHeight { get; }
    bool Floor(int layer, int x, int z);
    bool Roof(int layer, int x, int z);
    (double Height, int ThingKey) Solid(int layer, int x, int z);
}

/// <summary>Exact grid/height interval traversal; storage-band spacing never enters geometry.</summary>
public static class ShotGeometry
{
    private const double Epsilon = 1e-9;
    public readonly record struct Span(int Layer,int X,int Z,double Start,double End);
    public static IEnumerable<Span> Cells(ShotPoint start,ShotPoint end,double layerHeight)
    {
        double[] times=CutTimes(start,end,layerHeight);
        for(int i=1;i<times.Length;i++)
        {
            var middle=start.At(end,(times[i-1]+times[i])/2);
            yield return new((int)Math.Floor(middle.Height/layerHeight),(int)Math.Floor(middle.X),
                (int)Math.Floor(middle.Z),times[i-1],times[i]);
        }
    }
    private static double[] CutTimes(ShotPoint start,ShotPoint end,double layerHeight)
    {
        var cuts = new SortedSet<double> { 0, 1 };
        void Axis(double a, double b, double step)
        {
            if (Math.Abs(b-a) <= Epsilon) return;
            for (double boundary = (Math.Floor(Math.Min(a,b)/step)+1)*step; boundary < Math.Max(a,b); boundary += step)
            {
                double t = (boundary-a)/(b-a);
                if (t > Epsilon && t < 1-Epsilon) cuts.Add(t);
            }
        }
        Axis(start.X,end.X,1); Axis(start.Z,end.Z,1);
        Axis(start.Height,end.Height,layerHeight);
        return cuts.ToArray();
    }

    // Closed finite cylinder: same logical footprint and one-storey height, including top/bottom.
    public static double? CylinderEntry(ShotPoint start,ShotPoint end,double x,double z,
        double radius,double bottom,double top)
    {
        double dx=end.X-start.X,dz=end.Z-start.Z,dy=end.Height-start.Height;
        double ox=start.X-x,oz=start.Z-z,a=dx*dx+dz*dz,b=2*(ox*dx+oz*dz),c=ox*ox+oz*oz-radius*radius;
        double enter=0,exit=1;
        if(a<Epsilon) { if(c>0) return null; }
        else
        {
            double discriminant=b*b-4*a*c;
            if(discriminant<0) return null;
            double root=Math.Sqrt(discriminant);
            enter=Math.Max(enter,(-b-root)/(2*a));exit=Math.Min(exit,(-b+root)/(2*a));
        }
        if(Math.Abs(dy)<Epsilon) { if(start.Height<bottom || start.Height>top) return null; }
        else
        {
            double low=(bottom-start.Height)/dy,high=(top-start.Height)/dy;
            enter=Math.Max(enter,Math.Min(low,high));exit=Math.Min(exit,Math.Max(low,high));
        }
        return enter<=exit+Epsilon ? enter : null;
    }

    /// <summary>Keep a solid impact in its incoming air volume, including above the top roof.
    /// The surface's owner layer is not necessarily the side where the projectile stopped.</summary>
    public static ShotPoint Approach(ShotPoint start,ShotPoint end,double time)
    {
        double dx=end.X-start.X,dy=end.Height-start.Height,dz=end.Z-start.Z;
        double length=Math.Sqrt(dx*dx+dy*dy+dz*dz);
        return start.At(end,length==0 ? 0 : Math.Max(0,time-0.0001/length));
    }

    public static ShotObstruction? First(ShotPoint start, ShotPoint end, IShotScene scene)
    {
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Height) || !double.IsFinite(start.Z) ||
            !double.IsFinite(end.X) || !double.IsFinite(end.Height) || !double.IsFinite(end.Z) ||
            !double.IsFinite(scene.LayerHeight) || scene.LayerHeight <= 0)
            throw new ArgumentException("Shot geometry needs finite coordinates and positive layer height.");
        double dy = end.Height - start.Height;
        ShotObstruction? best = null;
        void Consider(ShotObstruction hit)
        {
            if (hit.Time >= -Epsilon && hit.Time <= 1 + Epsilon && (best == null || hit.Time < best.Value.Time))
                best = hit;
        }
        if (Math.Abs(dy) > Epsilon)
            for (int upper = scene.MinLayer; upper <= scene.MaxLayer+1; upper++)
            {
                double t = (upper * scene.LayerHeight - start.Height) / dy;
                if (t <= Epsilon || t > 1 + Epsilon) continue;
                var point = start.At(end, t);
                int x = (int)Math.Floor(point.X), z = (int)Math.Floor(point.Z);
                if (upper <= scene.MaxLayer && scene.Floor(upper,x,z))
                    Consider(new(ObstructionKind.Floor,upper,x,z,t));
                else if (scene.Roof(upper-1,x,z))
                    Consider(new(ObstructionKind.Roof,upper-1,x,z,t));
            }
        double[] times = CutTimes(start,end,scene.LayerHeight);
        for (int i=1;i<times.Length;i++)
        {
            double a=times[i-1], b=times[i];
            if (best != null && a >= best.Value.Time) break;
            ShotPoint middle = start.At(end,(a+b)/2);
            int layer=(int)Math.Floor(middle.Height/scene.LayerHeight);
            if (layer < scene.MinLayer || layer > scene.MaxLayer) continue;
            int x=(int)Math.Floor(middle.X), z=(int)Math.Floor(middle.Z);
            var solid=scene.Solid(layer,x,z);
            if (solid.Height <= 0) continue;
            double bottom=layer*scene.LayerHeight, top=bottom+solid.Height;
            double enter=a, exit=b;
            if (Math.Abs(dy)<=Epsilon)
            {
                if (start.Height<bottom || start.Height>top) continue;
            }
            else
            {
                double lower=(bottom-start.Height)/dy, upper=(top-start.Height)/dy;
                enter=Math.Max(enter,Math.Min(lower,upper));
                exit=Math.Min(exit,Math.Max(lower,upper));
            }
            if (enter <= exit + Epsilon)
                Consider(new(ObstructionKind.Wall,layer,x,z,Math.Max(0,enter),solid.ThingKey));
        }
        return best;
    }
}
