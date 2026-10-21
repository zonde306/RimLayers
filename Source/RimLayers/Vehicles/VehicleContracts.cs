using Verse;
using Verse.AI;

namespace RimLayers.Vehicles;

[Flags]
public enum VehicleCapabilities { None=0, GroundMovement=1, WorldTravel=2, ManualDriving=4, ContinuousFlight=8 }

/// <summary>One provider per game. Optional flight implementations never leak into ground transport.</summary>
internal interface IVehicleAdapter
{
    string Provider { get; }
    bool IsVehicle(Thing thing);
    bool CanMove(Pawn vehicle);
    double Heading(Pawn vehicle);
    VehicleHull Hull(Pawn vehicle);
    IEnumerable<IntVec3> OccupiedCells(Pawn vehicle,IntVec3 position,double heading);
    void SetHeading(Pawn vehicle,double heading);
    VehicleVisual Visual(Pawn vehicle,UnityEngine.Vector3 position);
    IReadOnlyList<IntVec3>? FindLeg(Pawn vehicle,IntVec3 from,LocalTargetInfo target,PathEndMode endMode);
    void StartLeg(Pawn vehicle,IntVec3 destination);
    void Stop(Pawn vehicle);
    void Fail(Pawn vehicle);
    void Commit(Pawn vehicle,IntVec3 position,double heading);
}

internal interface IVehicleFlightAdapter
{
    bool Active(Pawn vehicle);
    double Height(Pawn vehicle);
    string State(Pawn vehicle);
    bool Manual(Pawn vehicle);
    bool BeginRoute(Pawn vehicle,LocalTargetInfo destination,PathEndMode mode);
    bool OwnsRoute(Pawn vehicle);
    void CancelRoute(Pawn vehicle);
}

internal readonly record struct VehicleVisual(UnityEngine.Mesh Mesh,UnityEngine.Material Material,UnityEngine.Vector3 Position,UnityEngine.Quaternion Rotation);

public sealed class VehicleClearance:DefModExtension
{
    public float height=3;
    public override IEnumerable<string> ConfigErrors()
    {
        if(!float.IsFinite(height)||height<=0)yield return "VehicleClearance.height must be finite and positive.";
    }
}

/// <summary>Serializes logical intent, never a third-party path, comp, or copied Pawn.</summary>
public sealed class VehicleTravelState:IExposable
{
    public Pawn Vehicle=null!;
    public LocalTargetInfo Destination;
    public PathEndMode EndMode;
    public Building_VehicleConnector? Entry,Exit;
    public IntVec3 EntryCell,ExitCell;
    public IntVec3 Waypoint=IntVec3.Invalid;
    public double Heading;
    public double CabinElevation;
    public int RemainingTicks,TotalTicks;
    internal int NextRouteRetry,LookAheadTick;
    internal IntVec3 LookAheadCell=IntVec3.Invalid;
    internal bool LookAheadBlocked;
    public VehicleRamp Ramp;
    public VehicleTravelPhase Phase;
    public int JobId;
    public long QueueTicket;
    public bool AbortRequested,Manual;
    public double ManualFraction,ManualLateral;
    public List<string> Claims=new();
    public void ExposeData()
    {
        Scribe_References.Look(ref Vehicle,"vehicle");
        Scribe_TargetInfo.Look(ref Destination,"destination");
        Scribe_Values.Look(ref EndMode,"endMode");
        Scribe_References.Look(ref Entry,"entry");Scribe_References.Look(ref Exit,"exit");
        Scribe_Values.Look(ref EntryCell,"entryCell");Scribe_Values.Look(ref ExitCell,"exitCell");
        Scribe_Values.Look(ref Waypoint,"waypoint",IntVec3.Invalid);
        Scribe_Values.Look(ref CabinElevation,"cabinElevation");
        Scribe_Values.Look(ref Heading,"heading");Scribe_Values.Look(ref RemainingTicks,"remainingTicks");
        Scribe_Values.Look(ref TotalTicks,"totalTicks");
        double rampX=Ramp.StartX,rampZ=Ramp.StartZ,rampYaw=Ramp.Yaw,rampHeight=Ramp.BaseHeight;
        Scribe_Values.Look(ref rampX,"rampX");Scribe_Values.Look(ref rampZ,"rampZ");
        Scribe_Values.Look(ref rampYaw,"rampYaw");Scribe_Values.Look(ref rampHeight,"rampHeight");
        if(Scribe.mode==LoadSaveMode.LoadingVars)Ramp=new(rampX,rampZ,rampYaw,rampHeight);
        Scribe_Values.Look(ref Phase,"phase");Scribe_Values.Look(ref JobId,"jobId");
        Scribe_Values.Look(ref QueueTicket,"queueTicket");Scribe_Values.Look(ref AbortRequested,"abortRequested");
        Scribe_Values.Look(ref Manual,"manual");
        Scribe_Values.Look(ref ManualFraction,"manualFraction");Scribe_Values.Look(ref ManualLateral,"manualLateral");
        Scribe_Collections.Look(ref Claims,"claims",LookMode.Value);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)Claims??=new();
    }
}
public enum VehicleTravelPhase { Approaching, Waiting, Crossing, Held, Recovering, FinalLeg, Queued }
