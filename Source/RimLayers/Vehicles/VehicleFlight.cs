using RimLayers.Core;
using RimLayers.Recovery;
using Verse;
using Verse.AI;
namespace RimLayers.Vehicles;

/// <summary>Own coordinate datum and navigation intent. The provider saves its own dynamics.</summary>
public sealed class VehicleFlightState:IExposable
{
    public Pawn Vehicle=null!;
    public double Datum;
    public int ProxyLayer;
    public LocalTargetInfo Destination;
    public PathEndMode EndMode;
    public bool Landing,ImpactCommitted,RouteActive;
    public int JobId;
    public double GoalHeight;
    internal VehicleAirGuidance? Guidance;
    internal int NextPlanTick;
    internal long PlannedRevision;
    public IntVec3 ImpactCell=IntVec3.Invalid;
    public double ImpactHeight;
    internal RampBody Previous;
    internal bool HasPrevious;
    public void ExposeData()
    {
        Scribe_References.Look(ref Vehicle,"vehicle");Scribe_Values.Look(ref Datum,"heightDatum");
        Scribe_Values.Look(ref ProxyLayer,"proxyLayer");Scribe_TargetInfo.Look(ref Destination,"destination");
        Scribe_Values.Look(ref EndMode,"endMode");Scribe_Values.Look(ref Landing,"landing");
        Scribe_Values.Look(ref ImpactCommitted,"impactCommitted");
        Scribe_Values.Look(ref RouteActive,"routeActive");Scribe_Values.Look(ref JobId,"jobId");Scribe_Values.Look(ref GoalHeight,"goalHeight");
        Scribe_Values.Look(ref ImpactCell,"impactCell",IntVec3.Invalid);Scribe_Values.Look(ref ImpactHeight,"impactHeight");
    }
}

public sealed class VehicleFlight:MapComponent
{
    private List<VehicleFlightState> flights=new();
    private readonly Dictionary<int,VehicleFlightState> index=new();
    public VehicleFlight(Map map):base(map){}
    internal VehicleFlightState? State(Pawn pawn)=>index.GetValueOrDefault(pawn.thingIDNumber);
    internal VehicleFlightState Ensure(Pawn pawn)
    {
        if(State(pawn) is {} old)return old;
        var owner=LayerMapComponent.For(map)!;owner.TryDecode(pawn.Position,out var at);
        var state=new VehicleFlightState{Vehicle=pawn,Datum=owner.Elevation(at.LayerId)*3,ProxyLayer=at.LayerId};
        flights.Add(state);index.Add(pawn.thingIDNumber,state);return state;
    }
    public override void ExposeData()
    {
        base.ExposeData();Scribe_Collections.Look(ref flights,"vehicleFlights",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)
        {flights??=new();index.Clear();foreach(var s in flights.Where(s=>s.Vehicle!=null))index[s.Vehicle.thingIDNumber]=s;}
    }
    public override void FinalizeInit()
    {
        base.FinalizeInit();
        foreach(var s in flights.Where(s=>s.Vehicle is {Destroyed:false}))RecoveryCompatibility.DeferFor(s.Vehicle,120);
    }
    internal static int ProxyAt(LayerMapComponent owner,double height)
    {
        // Air above the highest constructed layer remains in its last storage band.
        // Stable layer IDs are not elevation indices (underground layers can be appended).
        int result=0;int best=int.MinValue;
        for(int id=0;id<owner.Layout!.LayerCount;id++)
        {int elevation=owner.Elevation(id);if(elevation*3<=height+1e-5&&elevation>best){result=id;best=elevation;}}
        if(best==int.MinValue)result=Enumerable.Range(0,owner.Layout.LayerCount).MinBy(owner.Elevation);
        return result;
    }
    internal void Begin(Pawn pawn)
    {
        var s=Ensure(pawn);var owner=LayerMapComponent.For(map)!;owner.TryDecode(pawn.Position,out var at);
        s.Datum=owner.Elevation(at.LayerId)*3;s.ProxyLayer=at.LayerId;s.HasPrevious=false;s.ImpactCommitted=false;s.Landing=false;
        RecoveryCompatibility.DeferFor(pawn,120);
    }
}
