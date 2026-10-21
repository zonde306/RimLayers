namespace RimLayers.Cargo;

/// <summary>Position is measured in physical floor elevations, never storage-band IDs.</summary>
public sealed class HoistMotion
{
    public float Position;
    public int Destination;
    public int SetupRemaining;
    public bool Moving;
    public void Begin(int target, bool electric, int setupTicks)
    {
        Destination=target;
        Moving=Math.Abs(Position-target)>0.0001f;
        SetupRemaining=Moving&&electric?setupTicks:0;
    }
    public void Advance(int delta, bool canRun, int ticksPerFloor)
    {
        if(!Moving||!canRun||delta<=0)return;
        int setup=Math.Min(delta,SetupRemaining);
        SetupRemaining-=setup;delta-=setup;
        float distance=delta/(float)ticksPerFloor;
        Position+=Math.Sign(Destination-Position)*Math.Min(Math.Abs(Destination-Position),distance);
        if(Math.Abs(Position-Destination)<0.0001f){Position=Destination;Moving=false;SetupRemaining=0;}
    }
    public int Remaining(int ticksPerFloor)=>Moving?SetupRemaining+(int)Math.Ceiling(Math.Abs(Destination-Position)*ticksPerFloor):0;
}
