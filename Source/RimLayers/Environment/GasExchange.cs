namespace RimLayers.Environment;

/// <summary>Snapshot exchange on a vertical chain; each packed byte remains native-owned.</summary>
public static class GasExchange
{
    public static void Apply(Span<uint> cells,ReadOnlySpan<bool> open,float strength=1)
    {
        if(open.Length!=Math.Max(0,cells.Length-1)) throw new ArgumentException("One permission per adjacent pair is required.");
        if(!float.IsFinite(strength)||strength<0||strength>4)throw new ArgumentOutOfRangeException(nameof(strength));
        for(int pass=0;pass<(int)strength;pass++)Step(cells,open,1);
        if(strength%1>0)Step(cells,open,strength%1);
    }
    private static void Step(Span<uint> cells,ReadOnlySpan<bool> open,float fraction)
    {
        Span<int> delta=stackalloc int[cells.Length];
        // BlindSmoke has no native diffusion. Preserve that channel, rather than silently
        // giving one diffusion policy to all gas types.
        for(int channel=1;channel<4;channel++)
        {
            delta.Clear();int shift=channel*8;
            for(int edge=0;edge<open.Length;edge++)
            {
                if(!open[edge]) continue;
                int a=(int)((cells[edge]>>shift)&255),b=(int)((cells[edge+1]>>shift)&255),difference=a-b;
                if(Math.Abs(difference)<=1) continue; // One native concentration unit is the discrete equilibrium.
                int transfer=Math.Sign(difference)*(int)Math.Floor(((Math.Abs(difference)+3)/4)*fraction);
                delta[edge]-=transfer;delta[edge+1]+=transfer;
            }
            for(int i=0;i<cells.Length;i++)
            {
                int value=(int)((cells[i]>>shift)&255)+delta[i];
                // Degree is at most two. Quarter-step exchange cannot overspend a cell,
                // including two simultaneous outward transfers and integer rounding.
                if((uint)value>255) throw new InvalidOperationException("Non-conservative gas exchange.");
                cells[i]=(cells[i]&~(255u<<shift))|((uint)value<<shift);
            }
        }
    }
}
