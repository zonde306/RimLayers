using RimLayers.Core;
using RimLayers.Recovery;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Vehicles;

/// <summary>Map-owned custody survives removal of a lift station. No vehicle is owned by a disposable building.</summary>
public sealed partial class VehicleTraffic:MapComponent,IThingHolder,ISuspendableThingHolder
{
    private ThingOwner<Thing> held;
    private List<VehicleTravelState> trips=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int,VehicleTravelState> tripIndex=new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<VehicleTravelState> nativeFailures=new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<VehicleTravelState> pendingCancellations=new();
    private readonly VehicleReservations reservations=new();
    private long nextTicket=1;
    private bool restoreAfterLoad,reservationConflict;
    private readonly HashSet<Building_VehicleConnector> connectors=new();
    public IReadOnlyCollection<Building_VehicleConnector> Connectors=>connectors;
    internal IEnumerable<VehicleTravelState> Trips=>trips;
    internal long Version {get;private set;}
    [ThreadStatic] internal static bool InternalCommand;
    [ThreadStatic] internal static Pawn? Boarding;
    public VehicleTraffic(Map map):base(map)=>held=new(this);
    public bool IsContentsSuspended=>true;
    public IThingHolder ParentHolder=>null!;
    public ThingOwner GetDirectlyHeldThings()=>held;
    public void GetChildHolders(List<IThingHolder> children)=>ThingOwnerUtility.AppendThingHoldersFromThings(children,held);
    internal void Register(Building_VehicleConnector b){connectors.Add(b);Version++;}
    internal void Remove(Building_VehicleConnector b){connectors.Remove(b);Version++;}
    internal VehicleTravelState? Trip(Pawn p)=>tripIndex.TryGetValue(p.thingIDNumber,out var trip)?trip:null;
    internal bool DeferNativeFailure(Pawn pawn)
    {
        if(Trip(pawn) is not {} trip)return false;
        if(trip.Manual&&GenCommandLine.CommandLineArgPassed("rimlayersv1test"))Log.Message("[RimLayers.ManualTrace] native failure: "+System.Environment.StackTrace);
        nativeFailures.Enqueue(trip);return true;
    }
    internal static void Command(Action action)
    {bool previous=InternalCommand;InternalCommand=true;try{action();}finally{InternalCommand=previous;}}
    internal bool Begin(Pawn pawn,LocalTargetInfo destination,PathEndMode endMode)
    {
        if(VehicleCompatibility.Adapter is not {} adapter||!adapter.CanMove(pawn))return false;
        if(Trip(pawn) is {} existing&&existing.Destination==destination&&existing.EndMode==endMode&&existing.JobId==(pawn.CurJob?.loadID??-1))return true;
        if(Trip(pawn) is {Phase:VehicleTravelPhase.Crossing or VehicleTravelPhase.Held or VehicleTravelPhase.Recovering}){Cancel(pawn);return false;}
        var edge=VehicleRoutePlanner.FirstCrossing(this,pawn,destination,endMode);
        if(edge==null)return false;
        Cancel(pawn);
        var trip=new VehicleTravelState{Vehicle=pawn,Destination=destination,EndMode=endMode,JobId=pawn.CurJob?.loadID??-1};
        trips.Add(trip);tripIndex[pawn.thingIDNumber]=trip;Approach(trip,edge);return true;
    }
    private void Approach(VehicleTravelState trip,VehicleEdge edge)
    {
        reservations.Release(trip.Vehicle.thingIDNumber);trip.QueueTicket=0;
        trip.Claims=VehicleRoutePlanner.Claims(LayerMapComponent.For(map)!,edge,VehicleCompatibility.Adapter!.Hull(trip.Vehicle));
        trip.Entry=edge.Entry;trip.Exit=edge.Exit;trip.EntryCell=edge.From;trip.ExitCell=edge.To;trip.Waypoint=IntVec3.Invalid;
        trip.CabinElevation=LayerMapComponent.For(map)!.Elevation(LayerMapComponent.For(map)!.TryDecode(edge.From,out var start)?start.LayerId:0);
        trip.Heading=edge.Heading;trip.RemainingTicks=trip.TotalTicks=edge.Ticks;
        if(!edge.Entry.IsLift)trip.Ramp=VehicleRampService.Profile(LayerMapComponent.For(map)!,edge.Entry);trip.Phase=VehicleTravelPhase.Approaching;
        if(Reserve(trip))Command(()=>VehicleCompatibility.Adapter!.StartLeg(trip.Vehicle,trip.EntryCell));
        else {trip.Phase=VehicleTravelPhase.Queued;Command(()=>VehicleCompatibility.Adapter!.Stop(trip.Vehicle));}
    }
    internal void Cancel(Pawn pawn)
    {
        var t=Trip(pawn);if(t==null)return;
        if(t.Manual&&GenCommandLine.CommandLineArgPassed("rimlayersv1test"))Log.Message("[RimLayers.ManualTrace] cancel: "+System.Environment.StackTrace);
        if(!UnityData.IsInMainThread){pendingCancellations.Enqueue(t);return;}
        if(held.Contains(pawn)){ReturnToOrigin(t);return;}
        if(t.Manual){SteelManualAdapter.Release(pawn);t.Manual=false;t.RemainingTicks=(int)Math.Round(t.TotalTicks*(1-t.ManualFraction));}
        if(t.Phase==VehicleTravelPhase.Crossing){t.AbortRequested=true;return;}
        Forget(t);
    }
    private void Forget(VehicleTravelState trip)
    {reservations.Release(trip.Vehicle.thingIDNumber);trips.Remove(trip);tripIndex.TryRemove(trip.Vehicle.thingIDNumber,out _);if(reservationConflict)RestoreReservations();}
    private static void ReturnToOrigin(VehicleTravelState trip)
    {
        trip.AbortRequested=true;trip.Phase=VehicleTravelPhase.Recovering;
        trip.Exit=trip.Entry;trip.ExitCell=trip.EntryCell;trip.RemainingTicks=0;
    }
    private bool Reserve(VehicleTravelState trip)
    {
        if(trip.QueueTicket==0)trip.QueueTicket=nextTicket++;
        reservations.Enqueue(trip.Vehicle.thingIDNumber,trip.QueueTicket,trip.Claims);
        return reservations.TryAcquire(trip.Vehicle.thingIDNumber);
    }
    private void RestoreReservations()
    {
        reservations.Clear();reservationConflict=false;
        foreach(var trip in trips.OrderBy(t=>t.QueueTicket).ThenBy(t=>t.Vehicle.thingIDNumber))
        {
            nextTicket=Math.Max(nextTicket,trip.QueueTicket+1);
            if(trip.QueueTicket==0)trip.QueueTicket=nextTicket++;
            if(trip.Phase is not (VehicleTravelPhase.Held or VehicleTravelPhase.Crossing or VehicleTravelPhase.Recovering or VehicleTravelPhase.Approaching or VehicleTravelPhase.FinalLeg))continue;
            if(trip.Claims.Count==0||!reservations.RestoreActive(trip.Vehicle.thingIDNumber,trip.QueueTicket,trip.Claims))reservationConflict=true;
        }
        foreach(var trip in trips.Where(t=>t.Phase is VehicleTravelPhase.Waiting or VehicleTravelPhase.Queued))
            if(trip.Claims.Count>0)reservations.Enqueue(trip.Vehicle.thingIDNumber,trip.QueueTicket,trip.Claims);
        if(reservationConflict)Log.Error("[RimLayers vehicles] Saved corridor claims conflict or are missing. Transport is paused; original vehicles remain in custody.");
    }
    internal bool IntermediateArrival(Pawn pawn)
    {
        var trip=Trip(pawn);
        if(trip?.Waypoint.IsValid==true)
        {
            trip.Waypoint=IntVec3.Invalid;Command(()=>VehicleCompatibility.Adapter!.Stop(pawn));
            if(!StartClearLeg(trip)){if(trip.Phase==VehicleTravelPhase.Approaching)trip.Phase=VehicleTravelPhase.Queued;else trip.Waypoint=pawn.Position;}
            return true;
        }
        if(trip?.Phase==VehicleTravelPhase.FinalLeg){Forget(trip);return false;}
        if(trip?.Phase!=VehicleTravelPhase.Approaching)return false;
        Command(()=>VehicleCompatibility.Adapter!.Stop(pawn));
        trip.Phase=VehicleTravelPhase.Waiting;return true;
    }
    internal bool RecoverAt(Building_VehicleConnector station)
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null||!owner.TryDecode(station.Position,out var target))return false;
        bool recovered=false;
        foreach(var t in trips.Where(t=>held.Contains(t.Vehicle)))
        {
            if(!owner.TryDecode(t.EntryCell,out var origin)||target.X!=origin.X||target.Z!=origin.Z)continue;
            if(!VehicleSpace.CanStand(LayerMapComponent.For(map)!,t.Vehicle,station.Position,t.Heading,out _))continue;
            t.AbortRequested=true;t.Exit=station;t.ExitCell=station.Position;t.RemainingTicks=0;t.Phase=VehicleTravelPhase.Recovering;recovered=true;
        }
        return recovered;
    }
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref held,"rimLayersVehiclesHeld",this);
        Scribe_Collections.Look(ref trips,"rimLayersVehicleTrips",LookMode.Deep);
        Scribe_Values.Look(ref nextTicket,"rimLayersVehicleNextTicket",1);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)
        {
            held??=new(this);trips??=new();
            trips.RemoveAll(t=>t.Vehicle==null||t.Vehicle.Destroyed);
            foreach(var t in trips)
            {
                if(held.Contains(t.Vehicle)&&t.Phase is not (VehicleTravelPhase.Held or VehicleTravelPhase.Recovering))ReturnToOrigin(t);
                else if(!held.Contains(t.Vehicle)&&t.Phase is (VehicleTravelPhase.Held or VehicleTravelPhase.Recovering))t.Phase=VehicleTravelPhase.Approaching;
            }
            tripIndex.Clear();foreach(var t in trips)tripIndex[t.Vehicle.thingIDNumber]=t;
            restoreAfterLoad=true;
        }
    }
    public override void FinalizeInit()
    {
        base.FinalizeInit();connectors.Clear();
        foreach(var c in map.listerThings.AllThings.OfType<Building_VehicleConnector>())connectors.Add(c);
        Version++;
    }
    public override void MapComponentTick()
    {
        if(VehicleCompatibility.Adapter is not {} adapter||LayerMapComponent.For(map) is not {} owner)return;
        if(restoreAfterLoad)
        {
            restoreAfterLoad=false;RestoreReservations();
            foreach(var trip in trips.Where(t=>t.Phase==VehicleTravelPhase.Approaching).ToArray())
                if(trip.Vehicle.Spawned)
                {
                    if(Valid(trip))Command(()=>adapter.StartLeg(trip.Vehicle,trip.Waypoint.IsValid?trip.Waypoint:trip.EntryCell));else Replan(trip);
                }
            foreach(var trip in trips.Where(t=>t.Phase==VehicleTravelPhase.FinalLeg).ToArray())
                if(trip.Vehicle.Spawned)Command(()=>VehicleCompatibility.Resume(trip.Vehicle,trip.Waypoint.IsValid?new LocalTargetInfo(trip.Waypoint):trip.Destination,trip.Waypoint.IsValid?PathEndMode.OnCell:trip.EndMode));
        }
        while(pendingCancellations.TryDequeue(out var cancelled))if(ReferenceEquals(Trip(cancelled.Vehicle),cancelled))Cancel(cancelled.Vehicle);
        while(nativeFailures.TryDequeue(out var failure))
            if(ReferenceEquals(Trip(failure.Vehicle),failure)&&failure.Vehicle.Spawned)
            {
                Command(()=>adapter.Stop(failure.Vehicle));
                if(failure.Phase==VehicleTravelPhase.FinalLeg)
                {
                    if(!failure.Destination.IsValid||failure.Destination.HasThing&&failure.Destination.Thing.Destroyed){Forget(failure);Command(()=>adapter.Fail(failure.Vehicle));}
                    else failure.Waypoint=failure.Vehicle.Position;
                }
                else failure.Phase=VehicleTravelPhase.Queued;
            }
        if(reservationConflict)
        {
            foreach(var recover in trips.Where(t=>t.Phase==VehicleTravelPhase.Recovering&&held.Contains(t.Vehicle)).ToArray())TickHeld(recover,owner);
            foreach(var cancel in trips.Where(t=>t.Phase==VehicleTravelPhase.Crossing&&t.AbortRequested&&t.Vehicle.Spawned).ToArray())TickRamp(cancel,owner);
            if(reservationConflict)return;
        }
        foreach(var trip in trips.ToArray())
        {
            var pawn=trip.Vehicle;
            if(pawn.Destroyed){Forget(trip);continue;}
            if(held.Contains(pawn)){TickHeld(trip,owner);continue;}
            if(!pawn.Spawned||pawn.Map!=map)
            {Cancel(pawn);continue;}
            if(trip.Manual)
            {
                RecoveryCompatibility.DeferFor(pawn,3);
                if(!VehicleRampService.ModulesIntact(owner,trip)){SteelManualAdapter.Release(pawn);trip.Manual=false;Board(trip);ReturnToOrigin(trip);}
                continue;
            }
            if(trip.Phase==VehicleTravelPhase.Crossing)
            {
                if(!adapter.CanMove(pawn)||(pawn.CurJob?.loadID??-1)!=trip.JobId)trip.AbortRequested=true;
                TickRamp(trip,owner);continue;
            }
            if(!adapter.CanMove(pawn)||(pawn.CurJob?.loadID??-1)!=trip.JobId)
            {Cancel(pawn);continue;}
            if(trip.Phase==VehicleTravelPhase.FinalLeg)
            {
                bool inside=adapter.OccupiedCells(pawn,pawn.Position,adapter.Heading(pawn)).Any(c=>owner.TryDecode(c,out var at)&&trip.Claims.Contains($"cell:{at.LayerId}:{at.X}:{at.Z}"));
                if(trip.Waypoint==pawn.Position&&Find.TickManager.TicksGame%30==pawn.thingIDNumber%30)StartClearLeg(trip);
                if(!inside&&!trip.Waypoint.IsValid)Forget(trip);continue;
            }
            if(trip.Phase==VehicleTravelPhase.Queued)
            {
                if(!Reserve(trip))continue;
                if(!Valid(trip)){Replan(trip);continue;}
                if(!VehicleSpace.CanStand(owner,pawn,trip.EntryCell,trip.Heading,out _))continue;
                if(Find.TickManager.TicksGame%30!=pawn.thingIDNumber%30)continue;
                trip.Phase=VehicleTravelPhase.Approaching;
                if(!StartClearLeg(trip))trip.Phase=VehicleTravelPhase.Queued;continue;
            }
            // Position reaches the last cell before the native braking/arrival callback.
            // Only IntermediateArrival may hand a fully stopped vehicle to the facility.
            if(trip.Phase==VehicleTravelPhase.Approaching)continue;
            if(trip.Phase==VehicleTravelPhase.Waiting)
            {
                if(!Valid(trip)) {Replan(trip);continue;}
                if(!VehicleSpace.CanStand(owner,pawn,trip.EntryCell,trip.Heading,out _)||!VehicleSpace.CanStand(owner,pawn,trip.ExitCell,trip.Heading,out _))continue;
                if(!VehicleSpace.CanTurn(owner,pawn,trip.EntryCell,adapter.Heading(pawn),trip.Heading))continue;
                if(!Reserve(trip))continue;
                Command(()=>adapter.Commit(pawn,trip.EntryCell,trip.Heading));
                if(trip.Entry!.IsLift)
                {
                    if(!trip.Entry.Powered||!ShaftVacant(trip,owner))continue;
                    Board(trip);
                }
                else trip.Phase=VehicleTravelPhase.Crossing;
            }
            if(trip.Manual)
            {
                RecoveryCompatibility.DeferFor(pawn,3);
                if(!VehicleRampService.ModulesIntact(owner,trip)){SteelManualAdapter.Release(pawn);trip.Manual=false;Board(trip);ReturnToOrigin(trip);}
                continue;
            }
            if(trip.Phase==VehicleTravelPhase.Crossing)
            {
                TickRamp(trip,owner);
            }
        }
    }
    private bool Valid(VehicleTravelState t)
    {
        if(t.Entry is not {Spawned:true} a||t.Exit is not {Spawned:true} b||a.IsForbidden(t.Vehicle)||b.IsForbidden(t.Vehicle))return false;
        var owner=LayerMapComponent.For(map)!;
        // A temporary actor at a landing, or a power outage, is a wait condition rather than a broken route.
        return a.IsLift?VehicleRoutePlanner.ShaftIntact(this,owner,a,b,false)&&
            VehicleFacilitySpace.FitsCabin(owner,t.Vehicle,a,t.Heading)&&VehicleFacilitySpace.FitsCabin(owner,t.Vehicle,b,t.Heading):
            VehicleRampService.ModulesIntact(owner,t);
    }
    private void TickHeld(VehicleTravelState trip,LayerMapComponent owner)
    {
        var adapter=VehicleCompatibility.Adapter!;
        bool intact=trip.Entry is {Spawned:true}&&trip.Exit is {Spawned:true}&&VehicleRoutePlanner.ShaftIntact(this,owner,trip.Entry,trip.Exit,false);
        if(!intact&&trip.Phase!=VehicleTravelPhase.Recovering)
        {ReturnToOrigin(trip);}
        if(trip.Phase!=VehicleTravelPhase.Recovering)
        {
            if(!VehicleRoutePlanner.ShaftIntact(this,owner,trip.Entry!,trip.Exit!,true)||!ShaftVacant(trip,owner))return;
            if(trip.RemainingTicks>0)--trip.RemainingTicks;
            owner.TryDecode(trip.EntryCell,out var entry);owner.TryDecode(trip.ExitCell,out var exit);
            double fraction=trip.TotalTicks<=0?1:1-(double)trip.RemainingTicks/trip.TotalTicks;
            trip.CabinElevation=owner.Elevation(entry.LayerId)+(owner.Elevation(exit.LayerId)-owner.Elevation(entry.LayerId))*fraction;
            if(trip.RemainingTicks>0)return;
        }
        // A cancelled/invalid journey never silently falls back to the surface or resumes its old destination.
        if(trip.Phase!=VehicleTravelPhase.Recovering&&(trip.Exit is not {Spawned:true,IsLift:true}||trip.Exit.Position!=trip.ExitCell))return;
        if(!VehicleSpace.CanStand(owner,trip.Vehicle,trip.ExitCell,trip.Heading,out _))return;
        var pawn=trip.Vehicle;
        try
        {
            using var recovery=RecoveryCompatibility.BeginHandling(pawn);
            // Match the checked orientation before SpawnSetup claims cells or GenSpawn considers wiping.
            adapter.SetHeading(pawn,trip.Heading);
            held.Remove(pawn);
            GenSpawn.Spawn(pawn,trip.ExitCell,map,pawn.Rotation,WipeMode.Vanish);
            Command(()=>adapter.Commit(pawn,trip.ExitCell,trip.Heading));
        }
        catch(Exception e)
        {
            if(!pawn.Spawned)
            {
                if(!held.Contains(pawn))held.TryAdd(pawn,false);
                ReturnToOrigin(trip);
            }
            else Forget(trip); // Spawn succeeded: retain this same live object rather than duplicating custody.
            Log.Error("[RimLayers vehicles] Lift unloading failed; original vehicle retained: "+e);
            return;
        }
        if(trip.AbortRequested){Forget(trip);Command(()=>adapter.Fail(pawn));}else Continue(trip);
    }
    private void Replan(VehicleTravelState trip)
    {
        if(GenCommandLine.CommandLineArgPassed("rimlayersv1test")&&trip.Entry is {Spawned:true} a&&trip.Exit is {Spawned:true} b)
        {
            var owner=LayerMapComponent.For(map)!;var adapter=VehicleCompatibility.Adapter!;
            var standA=VehicleSpace.CanStand(owner,trip.Vehicle,trip.EntryCell,trip.Heading,out var reasonA);
            var standB=VehicleSpace.CanStand(owner,trip.Vehicle,trip.ExitCell,trip.Heading,out var reasonB);
            Log.Message($"[RimLayers.VehicleGate] id={trip.Vehicle.ThingID} phase={trip.Phase} angles={trip.Heading}/{adapter.Heading(trip.Vehicle)} station={a.Powered}/{b.Powered} shaft={VehicleRoutePlanner.ShaftIntact(this,owner,a,b,true)} stand={standA}:{reasonA}/{standB}:{reasonB} fits={VehicleFacilitySpace.FitsCabin(owner,trip.Vehicle,a,adapter.Heading(trip.Vehicle))}/{VehicleFacilitySpace.FitsCabin(owner,trip.Vehicle,b,adapter.Heading(trip.Vehicle))} edges={VehicleRoutePlanner.Edges(this,trip.Vehicle).Count}");
        }
        var edge=VehicleRoutePlanner.FirstCrossing(this,trip.Vehicle,trip.Destination,trip.EndMode);
        if(edge!=null){Approach(trip,edge);return;}
        Forget(trip);Command(()=>VehicleCompatibility.Adapter!.Fail(trip.Vehicle));
        Messages.Message("RL_VehicleRouteLost".Translate(),trip.Vehicle,MessageTypeDefOf.RejectInput,false);
    }
    private void Continue(VehicleTravelState trip)
    {
        var owner=LayerMapComponent.For(map)!;
        if(!owner.TryDecode(trip.Vehicle.Position,out var here)||!owner.TryDecode(trip.Destination.Cell,out var end)){Replan(trip);return;}
        if(here.LayerId!=end.LayerId){Replan(trip);return;}
        trip.Phase=VehicleTravelPhase.FinalLeg;
        Command(()=>VehicleCompatibility.Resume(trip.Vehicle,trip.Destination,trip.EndMode));
    }
}
