using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Cargo;

public sealed class Building_CargoHoist:Building_CargoPort,IHaulSource,IThingHolder,IThingHolderEvents<Thing>
{
    private ThingOwner<Thing> contents;
    private StorageSettings settings=null!;
    public HoistMotion Motion=new();
    public List<CargoRequest> Requests=new();
    private int originElevation;
    public Building_CargoHoist(){contents=new(this,false);}
    public ThingOwner<Thing> Contents=>contents;
    public bool HaulSourceEnabled=>Spawned;
    public int TicksPerFloor=>Props.electric?Props.electricTicksPerFloor:Props.manualTicksPerFloor;
    public bool Powered=>!Props.electric||this.TryGetComp<CompPowerTrader>()?.PowerOn==true;
    public override void PostMake()
    {
        base.PostMake();Master=this;settings=new(this);
        if(def.building.defaultStorageSettings!=null)settings.CopyFrom(def.building.defaultStorageSettings);
    }
    public override StorageSettings GetStoreSettings()=>settings;
    public ThingOwner GetDirectlyHeldThings()=>contents;
    public void GetChildHolders(List<IThingHolder> result)=>ThingOwnerUtility.AppendThingHoldersFromThings(result,contents);
    public void Notify_ItemAdded(Thing item){if(Spawned)Map.listerHaulables.Notify_AddedThing(item);}
    public void Notify_ItemRemoved(Thing item){if(Spawned)Map.listerHaulables.Notify_HaulSourceChanged(this);}
    public override void Notify_SettingsChanged(){if(Spawned)Map.listerHaulables.Notify_HaulSourceChanged(this);}
    public override void SpawnSetup(Map map,bool respawningAfterLoad)
    {
        bool first=!initialized;
        Master=this;base.SpawnSetup(map,respawningAfterLoad);
        int elevation=Elevation;
        if(!respawningAfterLoad)
        {
            // Translation during native gravship transport preserves relative cage height.
            if(initialized){float offset=elevation-originElevation;Motion.Position+=offset;Motion.Destination+=(int)offset;}
            else{Motion.Position=elevation;Motion.Destination=elevation;initialized=true;}
            originElevation=elevation;
        }
        if(Props.direction!=0&&!respawningAfterLoad&&first&&map.GetComponent<LayerMapComponent>() is {} owner)
        {
            var other=owner.Adjacent(Position,Props.direction);
            if(CargoPlacement.Free(map,other,null)&&CargoPlacement.Supported(map,other))
            {
                var port=(Building_CargoPort)ThingMaker.MakeThing(CargoDefOf.RL_CargoPort);
                port.Master=this;port.SetFaction(Faction);
                GenSpawn.Spawn(port,other,map,Rotation);
            }
        }
        CargoNetwork.For(map).Reconnect();
    }
    private bool initialized;
    public bool CanCall(Building_CargoPort port)=>port.Master==this&&port.Available&&RouteExists(Motion.Position,port.Elevation)&&
        (!Props.electric||Powered||!Motion.Moving&&Math.Abs(Motion.Position-port.Elevation)<.001f);
    public int EstimatedWait(Building_CargoPort port)
    {
        int wait=Motion.Remaining(TicksPerFloor);
        float from=Motion.Moving?Motion.Destination:Motion.Position;
        foreach(var request in Requests.Where(r=>r.Valid))
        {
            float d=Math.Abs(from-request.Port.Elevation);
            if(d>.001f)wait+=(int)Math.Ceiling(d*TicksPerFloor)+(Props.electric?Props.electricSetupTicks:0);
            wait+=Math.Max(0,Props.handlingTicks-request.Handled);from=request.Port.Elevation;
        }
        float last=Math.Abs(from-port.Elevation);
        if(last>.001f)wait+=(int)Math.Ceiling(last*TicksPerFloor)+(Props.electric?Props.electricSetupTicks:0);
        return wait+Props.handlingTicks;
    }

    /// <summary>Simulate actual stacks plus promised incoming stacks; quality/stuff use CanStackWith.</summary>
    public int SpaceFor(Thing item,Job? excluding=null,bool includePromises=true)
    {
        var stacks=contents.Select(t=>(thing:t,count:t.stackCount)).ToList();
        foreach(var r in Requests.Where(r=>includePromises&&r.Deposit&&r.Job!=excluding&&r.Item is {Destroyed:false}))
        {
            int count=r.Count;
            for(int i=0;i<stacks.Count&&count>0;i++)
                if(stacks[i].thing.CanStackWith(r.Item))
                {int add=Math.Min(count,Math.Max(0,stacks[i].thing.def.stackLimit-stacks[i].count));stacks[i]=(stacks[i].thing,stacks[i].count+add);count-=add;}
            while(count>0){int add=Math.Min(count,r.Item!.def.stackLimit);stacks.Add((r.Item,add));count-=add;}
        }
        int space=Math.Max(0,Props.capacity-stacks.Count)*item.def.stackLimit;
        foreach(var s in stacks)if(s.thing.CanStackWith(item))space+=Math.Max(0,item.def.stackLimit-s.count);
        return space;
    }
    public CargoRequest? Request(Pawn pawn,Building_CargoPort port,Thing? item,int count,bool deposit,bool call=false,Job? job=null)
    {
        job??=pawn.CurJob;
        if(job==null)return null;
        var existing=Requests.FirstOrDefault(r=>r.Pawn==pawn&&r.Job==job&&r.Port==port&&(r.Item==item||deposit)&&r.Deposit==deposit);
        if(existing!=null){if(deposit)existing.Item=item;return existing;}
        if(!CargoNetwork.CanOperate(pawn)||!CanCall(port)||port.IsForbidden(pawn))return null;
        if(!call)
        {
            if(item==null||item.Destroyed||item.def.category!=ThingCategory.Item||item.IsForbidden(pawn))return null;
            if(deposit)
            {
                if(!GetStoreSettings().AllowedToAccept(item))return null;
                count=Math.Min(count,SpaceFor(item,job));
            }
            else
            {
                if(!contents.Contains(item))return null;
                count=Math.Min(count,item.stackCount-Requests.Where(r=>!r.Deposit&&r.Item==item&&r.Job!=job).Sum(r=>r.Count));
            }
            if(count<=0)return null;
        }
        var request=new CargoRequest{Pawn=pawn,Job=job,Port=port,Item=item,Count=count,Deposit=deposit,CallOnly=call};
        Requests.Add(request);return request;
    }
    public void Release(Pawn pawn,Job job)=>Requests.RemoveAll(r=>r.Pawn==pawn&&r.Job==job);
    public bool AtPort(Building_CargoPort port)=>!Motion.Moving&&Math.Abs(Motion.Position-port.Elevation)<.001f;
    public bool Ready(CargoRequest r)=>Requests.FirstOrDefault()==r&&AtPort(r.Port)&&r.Handled>=Props.handlingTicks;
    internal void ServiceTick()
    {
        foreach(var r in Requests.Where(r=>r.Deposit&&r.Pawn?.CurJob==r.Job))
            if(r.Job.targetA.Thing is {} actual)r.Item=actual;
        Requests.RemoveAll(r=>!r.Valid||r.Port.Master!=this||
            !r.CallOnly&&!r.Deposit&&!contents.Contains(r.Item));
        var active=Requests.FirstOrDefault();
        bool attending=active!=null&&active.Pawn.jobs.curDriver?.CurToilIndex==active.ToilIndex&&
            active.Pawn.CanReachImmediate(active.Port,PathEndMode.Touch)&&!active.Pawn.pather.Moving;
        if(!Motion.Moving&&active!=null&&attending&&!AtPort(active.Port))
            Motion.Begin(active.Port.Elevation,Props.electric,Props.electricSetupTicks);
        bool route=RouteIntact();
        Motion.Advance(1,route&&Powered&&(Props.electric||attending),TicksPerFloor);
        if(active!=null&&attending&&AtPort(active.Port))active.Handled=Math.Min(Props.handlingTicks,active.Handled+1);
        if(this.TryGetComp<CompPowerTrader>() is {} power)power.PowerOutput=-(Motion.Moving&&route&&Powered?Props.runningWatts:Props.idleWatts);
    }
    private bool RouteIntact()=>!Motion.Moving||RouteExists(Motion.Position,Motion.Destination);
    public bool RouteExists(float from,int destination)
    {
        if(!Spawned)return false;
        var network=CargoNetwork.For(Map);
        int low=(int)Math.Floor(Math.Min(from,destination)),high=(int)Math.Ceiling(Math.Max(from,destination));
        for(int e=low;e<=high;e++)
            if(!network.Ports.Any(p=>p.Master==this&&p.Elevation==e&&network.Connected(p)))return false;
        return true;
    }
    public IntVec3 BasketCell
    {
        get
        {
            var owner=LayerMapComponent.For(Map);
            if(owner==null||!owner.TryDecode(Position,out var at))return Position;
            int e=(int)Math.Round(Motion.Position,MidpointRounding.AwayFromZero);
            return owner.Layout!.TryGetAdjacentLayer(at.LayerId,e-owner.Elevation(at.LayerId),out _)?owner.AtElevation(e,at.X,at.Z):
                CargoNetwork.For(Map).Ports.Where(p=>p.Master==this).OrderBy(p=>Math.Abs(p.Elevation-Motion.Position)).FirstOrDefault()?.Position??Position;
        }
    }
    public override void Destroy(DestroyMode mode=DestroyMode.Vanish)
    {
        if(Spawned&&contents.Count>0)
        {
            var owner=LayerMapComponent.For(Map);
            if(owner!=null){owner.Recovery.TakeContents(this,BasketCell);owner.Recovery.Flush();}
            else contents.TryDropAll(Position,Map,ThingPlaceMode.Near);
            if(contents.Count>0){Log.Error("[RimLayers] Cargo destruction refused: goods could not be preserved.");return;}
        }
        if(Spawned)
        {
            foreach(var p in CargoNetwork.For(Map).Ports.Where(p=>p!=this&&p.Master==this).ToArray())
                if(Props.direction!=0)p.Destroy(DestroyMode.Vanish);else p.Master=null;
        }
        base.Destroy(mode);
    }
    public override void DeSpawn(DestroyMode mode=DestroyMode.Vanish)
    {
        Requests.Clear();base.DeSpawn(mode); // Native transport owns this intact holder.
    }
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref contents,"cargoContents",this);Scribe_Deep.Look(ref settings,"cargoSettings",this);
        Scribe_Collections.Look(ref Requests,"cargoRequests",LookMode.Deep);
        Scribe_Values.Look(ref Motion.Position,"cargoPosition");
        Scribe_Values.Look(ref Motion.Destination,"cargoDestination");
        Scribe_Values.Look(ref Motion.SetupRemaining,"cargoSetup");
        Scribe_Values.Look(ref Motion.Moving,"cargoMoving");
        Scribe_Values.Look(ref initialized,"cargoInitialized");
        Scribe_Values.Look(ref originElevation,"cargoOriginElevation");
        if(Scribe.mode==LoadSaveMode.PostLoadInit){Master=this;Requests??=new();}
    }
}
