using RimLayers.Core;
using RimWorld.Planet;
using Verse;
namespace RimLayers.Vehicles;

/// <summary>Own logical destination and recovery phase; native holders retain the original aircraft.</summary>
public sealed class VehicleWorldIntent:IExposable
{
    public Pawn Vehicle=null!;
    public MapParent? Destination,Origin;
    public int Layer,X,Z,OriginLayer,OriginX,OriginZ;
    public Rot4 Rotation;
    public bool HasDestination,Waiting;
    public Thing? Skyfaller;
    public string Reason="";
    public void ExposeData()
    {
        Scribe_References.Look(ref Vehicle,"vehicle");Scribe_References.Look(ref Destination,"destination");
        Scribe_References.Look(ref Origin,"origin");Scribe_References.Look(ref Skyfaller,"skyfaller");
        Scribe_Values.Look(ref Layer,"layer");Scribe_Values.Look(ref X,"x");Scribe_Values.Look(ref Z,"z");
        Scribe_Values.Look(ref OriginLayer,"originLayer");Scribe_Values.Look(ref OriginX,"originX");Scribe_Values.Look(ref OriginZ,"originZ");
        Scribe_Values.Look(ref Rotation,"rotation");Scribe_Values.Look(ref HasDestination,"hasDestination");
        Scribe_Values.Look(ref Waiting,"waiting");Scribe_Values.Look(ref Reason,"reason","");
    }
}
public sealed class VehicleWorldTravel:GameComponent
{
    private List<VehicleWorldIntent> intents=new();
    public VehicleWorldTravel(Game game){}
    internal static VehicleWorldTravel Current=>Verse.Current.Game.GetComponent<VehicleWorldTravel>();
    internal VehicleWorldIntent? State(Pawn vehicle)=>intents.FirstOrDefault(s=>s.Vehicle==vehicle);
    internal VehicleWorldIntent Ensure(Pawn vehicle)
    {var s=State(vehicle);if(s!=null)return s;s=new(){Vehicle=vehicle};intents.Add(s);return s;}
    internal IEnumerable<VehicleWorldIntent> WaitingFor(Map map)=>intents.Where(s=>s.Waiting&&s.Destination?.Map==map);
    internal bool HasWaiting(Map map)=>WaitingFor(map).Any();
    public override void ExposeData()
    {
        Scribe_Collections.Look(ref intents,"rimLayersVehicleWorldIntents",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit){intents??=new();intents.RemoveAll(s=>s.Vehicle==null);}
    }
    internal void DestinationFor(Pawn vehicle,MapParent parent,IntVec3 cell,Rot4 rotation)
    {
        if(LayerMapComponent.For(parent.Map) is not {} owner||!owner.TryDecode(cell,out var at))
        {
            // An explicitly chosen ordinary map replaces an earlier held layered destination.
            // Let the provider handle that new journey under its original contract.
            if(State(vehicle) is {} previous){previous.HasDestination=false;previous.Waiting=false;previous.Destination=parent;}
            return;
        }
        var s=Ensure(vehicle);s.Destination=parent;s.Layer=at.LayerId;s.X=at.X;s.Z=at.Z;s.Rotation=rotation;s.HasDestination=true;
    }
    internal void DrawRecovery(UnityEngine.Rect rect,Map map)
    {
        if(!Widgets.ButtonText(rect,"RL_VehicleLandingWaiting".Translate(WaitingFor(map).Count())))return;
        var adapter=FrameworkWorldAdapter.Installed;
        Find.WindowStack.Add(new FloatMenu(WaitingFor(map).Select(s=>new FloatMenuOption(s.Vehicle.LabelCap+": "+(adapter==null?"RL_VehicleUnavailable".Translate():s.Reason.Translate()),adapter==null?null:()=>adapter.ChooseAgain(s))).ToList()));
    }
}
