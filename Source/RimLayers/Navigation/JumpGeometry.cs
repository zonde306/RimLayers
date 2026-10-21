using RimLayers.Combat;

namespace RimLayers.Navigation;

/// <summary>Feet trajectory in logical space. Radius is an axis-aligned half-width, not a bullet ray.</summary>
public readonly record struct JumpArc(ShotPoint Start, ShotPoint End, double Rise, double BodyHeight, double HalfWidth)
{
    public ShotPoint Point(double t)
    {
        var p = Start.At(End, t);
        return p with { Height = p.Height + 4 * Rise * t * (1 - t) };
    }
    public double Distance => Math.Sqrt(Math.Pow(End.X-Start.X,2)+Math.Pow(End.Z-Start.Z,2)+Math.Pow(End.Height-Start.Height,2));
}

/// <summary>Exact quadratic/plane and swept AABB tests; no tick-size or rendering dependency.</summary>
public static class JumpGeometry
{
    public const double FeetOffset = 0.03;
    public static double RiseFor(double heightDifference) => Math.Max(1, (Math.Abs(heightDifference)+2)/4);
    public static bool InRange(JumpArc arc, double range) => arc.Distance <= range + 1e-6;

    public static ShotObstruction? First(JumpArc arc, IShotScene scene, double from=0, double to=1)
    {
        ShotObstruction? best = null;
        // Enumerate cells near the horizontal centreline, not its full enclosing rectangle.
        var cells = new HashSet<(int X,int Z)>();
        var a=arc.Start with {Height=1}; var b=arc.End with {Height=1};
        int margin=(int)Math.Ceiling(arc.HalfWidth);
        foreach(var span in ShotGeometry.Cells(a,b,scene.LayerHeight))
            for(int dx=-margin;dx<=margin;dx++)
                for(int dz=-margin;dz<=margin;dz++) cells.Add((span.X+dx,span.Z+dz));
        foreach(var cell in cells)
        {
            double enter=from,exit=to;
            if(!Axis(arc.Start.X,arc.End.X,cell.X-arc.HalfWidth,cell.X+1+arc.HalfWidth,ref enter,ref exit) ||
               !Axis(arc.Start.Z,arc.End.Z,cell.Z-arc.HalfWidth,cell.Z+1+arc.HalfWidth,ref enter,ref exit))continue;
            for(int upper=scene.MinLayer;upper<=scene.MaxLayer+1;upper++)
            {
                bool floor=upper<=scene.MaxLayer && scene.Floor(upper,cell.X,cell.Z);
                bool roof=scene.Roof(upper-1,cell.X,cell.Z);
                if(!floor&&!roof)continue;
                double plane=upper*scene.LayerHeight;
                var t=Vertical(arc,plane-arc.BodyHeight,plane,enter,exit);
                if(t is {} contact && (best==null||contact<best.Value.Time))
                    best=new(floor?ObstructionKind.Floor:ObstructionKind.Roof,floor?upper:upper-1,cell.X,cell.Z,contact);
            }
            for(int layer=scene.MinLayer;layer<=scene.MaxLayer;layer++)
            {
                var solid=scene.Solid(layer,cell.X,cell.Z);
                if(solid.Height<=0)continue;
                double bottom=layer*scene.LayerHeight;
                var t=Vertical(arc,bottom-arc.BodyHeight,bottom+solid.Height,enter,exit);
                if(t is {} contact && (best==null||contact<best.Value.Time))
                    best=new(ObstructionKind.Wall,layer,cell.X,cell.Z,contact,solid.ThingKey);
            }
        }
        return best;
    }

    private static bool Axis(double a,double b,double min,double max,ref double enter,ref double exit)
    {
        if(Math.Abs(b-a)<1e-12)return a>=min&&a<=max;
        double t0=(min-a)/(b-a),t1=(max-a)/(b-a);
        enter=Math.Max(enter,Math.Min(t0,t1));exit=Math.Min(exit,Math.Max(t0,t1));
        return enter<=exit+1e-10;
    }
    private static double? Vertical(JumpArc arc,double min,double max,double enter,double exit)
    {
        double c=arc.Start.Height,b=arc.End.Height-c+4*arc.Rise,a=-4*arc.Rise;
        var cuts=new List<double>{enter,exit};
        void Roots(double height)
        {
            if(Math.Abs(a)<1e-12)
            {
                if(Math.Abs(b)>1e-12){double t=(height-c)/b;if(t>enter&&t<exit)cuts.Add(t);}
                return;
            }
            double d=b*b-4*a*(c-height);if(d<0)return;
            double root=Math.Sqrt(d);
            foreach(double t in new[]{(-b-root)/(2*a),(-b+root)/(2*a)})
                if(t>enter&&t<exit)cuts.Add(t);
        }
        Roots(min);Roots(max);cuts.Sort();
        bool Inside(double t){double h=(a*t+b)*t+c;return h>=min-1e-9&&h<=max+1e-9;}
        for(int i=0;i<cuts.Count;i++)
        {
            if(Inside(cuts[i]))return cuts[i];
            if(i+1<cuts.Count&&Inside((cuts[i]+cuts[i+1])/2))return cuts[i];
        }
        return null;
    }
}
