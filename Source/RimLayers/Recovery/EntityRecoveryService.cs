using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Recovery;

/// <summary>One game-wide owner and work budget. The same Things are stored, never rebuilt or merged.</summary>
public sealed class EntityRecoveryService:GameComponent,IThingHolder,ISuspendableThingHolder
{
    private ThingOwner<Thing> held;
    private List<RecoveryRecord> records=new();
    private List<RecoveryHistory> history=new();
    private readonly Dictionary<int,RecoveryRecord> byId=new();
    private readonly Dictionary<int,(int Pawn,int Thing,int Audit)> cursors=new();
    private readonly Dictionary<int,int> warnings=new();
    private sealed class Location { internal int MapId;internal IntVec3 Registered=IntVec3.Invalid,Safe=IntVec3.Invalid; }
    private readonly ConditionalWeakTable<Thing,Location> locations=new();
    private int mapCursor,recordCursor,warningCount,nextWarning;
    [ThreadStatic] internal static bool Busy;
    [ThreadStatic] internal static Pawn? TransferringPawn;
    private static readonly AccessTools.FieldRef<Thing,IntVec3> RawPosition=AccessTools.FieldRefAccess<Thing,IntVec3>("positionInt");
    public static EntityRecoveryService? Current=>Verse.Current.Game?.GetComponent<EntityRecoveryService>();
    public EntityRecoveryService(Game game)=>held=new(this);
    public IThingHolder ParentHolder=>null!;
    public bool IsContentsSuspended=>true;
    public ThingOwner GetDirectlyHeldThings()=>held;
    public void GetChildHolders(List<IThingHolder> children)=>ThingOwnerUtility.AppendThingHoldersFromThings(children,held);
    public IReadOnlyList<RecoveryRecord> Records=>records;
    public int StoredCount=>held.Count;
    public long Inspected { get; private set; }
    public long CandidateCells { get; private set; }
    public long Corrected { get; private set; }
    public double InspectionMilliseconds { get; private set; }
    public double RecoveryMilliseconds { get; private set; }
    public double MaximumTickMilliseconds { get; private set; }
    public static bool Contains(Thing thing)
    {
        for(IThingHolder? holder=thing.ParentHolder;holder!=null;holder=holder.ParentHolder)
            if(holder is EntityRecoveryService)return true;
        return false;
    }
    public IEnumerable<Pawn> HeldPawns()
    {
        var pending=new Stack<IThingHolder>();pending.Push(this);var seen=new HashSet<IThingHolder>();
        while(pending.Count>0)
        {
            var holder=pending.Pop();if(!seen.Add(holder))continue;
            if(holder.GetDirectlyHeldThings() is {} contents)
                foreach(var thing in contents)if(thing is Pawn pawn)yield return pawn;
            var children=new List<IThingHolder>();holder.GetChildHolders(children);
            foreach(var child in children)if(child!=null)pending.Push(child);
        }
    }
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref held,"rimLayersRecoveryContents",this);
        Scribe_Collections.Look(ref records,"rimLayersRecoveryRecords",LookMode.Deep);
        Scribe_Collections.Look(ref history,"rimLayersRecoveryHistory",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)
        {
            held??=new(this);records??=new();history??=new();byId.Clear();
            records.RemoveAll(r=>r.Thing==null||r.Thing.Destroyed);
            foreach(var record in records)
            {
                byId[record.Thing!.thingIDNumber]=record;
                record.Stage=held.Contains(record.Thing)?RecoveryStage.Stored:RecoveryStage.Pending;
                record.Search=null;
            }
        }
    }
    public override void LoadedGame(){base.LoadedGame();foreach(var map in Find.Maps)QueueMap(map);}
    public Map? OriginalMap(RecoveryRecord record)=>Find.Maps.FirstOrDefault(m=>m.uniqueID==record.MapId&&!m.Disposed);
    internal void Remember(Thing thing,Map map)
    {
        if(!RecoveryCompatibility.SupportedKind(thing)||!thing.Spawned||LayerMapComponent.For(map) is not {} owner)return;
        var info=locations.GetOrCreateValue(thing);
        if(info.MapId!=map.uniqueID){info.MapId=map.uniqueID;info.Safe=IntVec3.Invalid;}
        if(thing.Position.InBounds(map)&&thing.Position.GetThingList(map).Contains(thing))info.Registered=thing.Position;
        if(RecoveryLanding.HasSupport(owner,thing,thing.Position))info.Safe=thing.Position;
    }
    internal void Notice(Thing thing,Map map,string reason="Unsupported",IntVec3? requested=null)
    {
        if(Busy||RecoveryCompatibility.TransactionActive||!RimLayersMod.Settings.AutoRecovery||LayerMapComponent.For(map) is not {} owner||RecoveryCompatibility.Excluded(thing,owner))return;
        if(thing.holdingOwner!=null&&(!thing.Spawned||thing.holdingOwner!=map.spawnedThings))return; // Map.spawnedThings is the native 1.7 map owner; other holders retain authority.
        var at=requested??thing.Position;
        if(RecoveryLanding.HasSupport(owner,thing,at)){Remember(thing,map);return;}
        if(!RecoveryCompatibility.SupportedKind(thing))
        {
            if(thing is Building or Blueprint or Frame)Warn(thing,"UnknownBuilding");
            return;
        }
        if(byId.ContainsKey(thing.thingIDNumber))return;
        if(!owner.TryDecode(at,out _))reason=at.InBounds(map)?"Isolation":"InvalidCoordinates";
        var record=new RecoveryRecord{Thing=thing,MapId=map.uniqueID,MapName=map.Parent.Label,Origin=at,Reason=reason,
            Stage=RecoveryStage.Discovered,NextAttempt=(Find.TickManager?.TicksGame??0)+1};
        if(locations.TryGetValue(thing,out var location)){record.LastMapId=location.MapId;record.LastValid=location.Safe;}
        records.Add(record);byId.Add(thing.thingIDNumber,record);record.Stage=RecoveryStage.Pending;
    }
    internal bool CaptureInvalidSpawn(Thing thing,Map map,IntVec3 at,Rot4 rotation)
    {
        if(Busy||!RimLayersMod.Settings.AutoRecovery||thing.Spawned||thing.holdingOwner!=null||
            LayerMapComponent.For(map) is not {} owner||RecoveryCompatibility.Excluded(thing,owner)||!RecoveryCompatibility.SupportedKind(thing))return false;
        thing.Rotation=rotation;Notice(thing,map,"Spawn",at);
        if(!byId.TryGetValue(thing.thingIDNumber,out var record))return false;
        Store(record);return true;
    }
    internal void StructuralFall(Thing thing,LayerMapComponent owner)
    {
        if(!thing.Spawned||thing.Destroyed||!RecoveryCompatibility.SupportedKind(thing)||thing is Pawn {Flying:true})return;
        // Collapse is the physical owner. Def exclusions and compatibility recovery preference do not disable it.
        if(!byId.TryGetValue(thing.thingIDNumber,out var record))
        {
            record=new(){Thing=thing,MapId=owner.map.uniqueID,MapName=owner.map.Parent.Label,Origin=thing.Position,Reason="Collapse",Stage=RecoveryStage.Pending};
            records.Add(record);byId.Add(thing.thingIDNumber,record);
        }
        if(!record.StructuralFall&&owner.TryDecode(record.Origin,out var source))
            record.FallLevels=Math.Max(0,owner.Elevation(source.LayerId)-RecoveryLanding.ContactElevation(owner,record.Origin));
        record.StructuralFall=true;
        Store(record); // One owner before structural callbacks continue or this cell is processed again.
    }
    internal void QueueMap(Map map)
    {
        // A rotating scan starts at the first object on this map; no whole-map/cell scan on load.
        cursors[map.uniqueID]=(0,0,0);
    }
    public override void GameComponentTick()
    {
        if(Verse.Current.ProgramState!=ProgramState.Playing||RecoveryCompatibility.TransactionActive)return;
        long started=Stopwatch.GetTimestamp();
        if(RimLayersMod.Settings.AutoRecovery)Inspect();
        long inspected=Stopwatch.GetTimestamp();InspectionMilliseconds+=Stopwatch.GetElapsedTime(started,inspected).TotalMilliseconds;
        int budget=512,examined=0;
        while(records.Count>0&&budget>0&&examined++<4)
        {
            recordCursor%=records.Count;var record=records[recordCursor++];
            if(record.ManualOnly||record.NextAttempt>Find.TickManager.TicksGame)continue;
            try { Process(record,ref budget); }
            catch(Exception ex)
            {
                record.Search?.Dispose();record.Search=null;record.ManualOnly=true;
                // A failed native spawn may have removed the object from its owner before throwing.
                if(record.Thing is {Spawned:false,Destroyed:false,holdingOwner:null} orphan)held.TryAdd(orphan,false);
                Warn(record.Thing,"Failure");
                Log.Warning($"[RimLayers.Recovery] id={record.Thing?.ThingID} def={record.Thing?.def.defName} from={record.Origin} reason={record.Reason} result=manual-review error={ex}");
            }
        }
        long ended=Stopwatch.GetTimestamp();RecoveryMilliseconds+=Stopwatch.GetElapsedTime(inspected,ended).TotalMilliseconds;
        MaximumTickMilliseconds=Math.Max(MaximumTickMilliseconds,Stopwatch.GetElapsedTime(started,ended).TotalMilliseconds);
        int now=Find.TickManager.TicksGame;
        if(warningCount>0&&now>=nextWarning)
        {
            Messages.Message("RL_RecoveryWarning".Translate(warningCount),MessageTypeDefOf.CautionInput,false);
            warningCount=0;nextWarning=now+1800;
        }
        if(now%6000==0){history.RemoveAll(h=>now-h.Tick>6000);foreach(var id in warnings.Where(p=>now-p.Value>6000).Select(p=>p.Key).ToArray())warnings.Remove(id);}
    }
    private void Inspect()
    {
        var maps=Find.Maps;if(maps.Count==0)return;
        // These totals are shared by all maps, not multiplied by the number of colonies.
        for(int i=0;i<24;i++)
        {
            mapCursor%=maps.Count;var map=maps[mapCursor];
            // Visit each map with all three roles; a map count divisible by three must not starve Pawn inspection.
            if(i%3==2)mapCursor++;
            var owner=LayerMapComponent.For(map);if(owner==null)continue;
            var cursor=cursors.GetValueOrDefault(map.uniqueID);Thing? thing=null;
            if(i%3==0)
            {
                var pawns=map.mapPawns.AllPawnsSpawned;if(pawns.Count>0){cursor.Pawn%=pawns.Count;thing=pawns[cursor.Pawn++];}
            }
            else if(i%3==1)
            {
                // Geology can add hundreds of thousands of rocks. Items/corpses have their own rotating view.
                var things=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
                if(things.Count>0){cursor.Thing%=things.Count;thing=things[cursor.Thing++];}
            }
            else
            {
                var things=map.listerThings.AllThings;if(things.Count>0){cursor.Audit%=things.Count;thing=things[cursor.Audit++];}
            }
            cursors[map.uniqueID]=cursor;if(thing==null||!thing.Spawned)continue;
            Inspected++;
            try { Notice(thing,map); }
            catch(Exception ex){Warn(thing,"Failure");Log.Warning($"[RimLayers.Recovery] inspection id={thing.ThingID} def={thing.def.defName} from={thing.Position} result=failed {ex.Message}");}
        }
    }
    private void Process(RecoveryRecord record,ref int budget)
    {
        var thing=record.Thing;
        if(thing==null||thing.Destroyed){Complete(record,"Gone");return;}
        var map=OriginalMap(record);var owner=LayerMapComponent.For(map);
        if(owner==null){record.NextAttempt=Find.TickManager.TicksGame+600;return;}
        if(!held.Contains(thing))
        {
            if(!thing.Spawned||thing.Map!=map||RecoveryCompatibility.Excluded(thing,owner))
            {Complete(record,"ExternalOwner");return;}
            if(RecoveryLanding.HasSupport(owner,thing,thing.Position))
            {Remember(thing,map!);Complete(record,"AlreadySafe");return;}
        }
        if(!record.StructuralFall&&history.Any(h=>h.Id==thing.thingIDNumber&&Find.TickManager.TicksGame-h.Tick<=600&&h.Count>=3))
        {Store(record);record.ManualOnly=true;record.Reason="RepeatedMovement";Warn(thing,record.Reason);return;}
        record.Search??=RecoveryLanding.Candidates(owner,thing,record).GetEnumerator();
        while(budget>0)
        {
            if(!record.Search.MoveNext())
            {
                record.Search.Dispose();record.Search=null;Store(record);record.NextAttempt=Find.TickManager.TicksGame+600;return;
            }
            budget--;CandidateCells++;
            var target=record.Search.Current;
            if(!RecoveryLanding.Legal(owner,thing,target.Cell))continue;
            record.Stage=target.Stage;
            if(Move(record,owner,target.Cell))return;
        }
        // Broad search continues next tick, with the entity suspended safely meanwhile.
        Store(record);
    }
    private void RestoreRegisteredPosition(Thing thing,Map map)
    {
        if(thing.Position.InBounds(map)&&thing.Position.GetThingList(map).Contains(thing))return;
        if(locations.TryGetValue(thing,out var known)&&known.MapId==map.uniqueID&&known.Registered.InBounds(map)&&known.Registered.GetThingList(map).Contains(thing))
        {RawPosition(thing)=known.Registered;return;}
        throw new InvalidOperationException("Entity registration was changed outside native movement; cannot safely detach an unknown grid registration.");
    }
    private void Store(RecoveryRecord record)
    {
        var thing=record.Thing!;if(held.Contains(thing)){record.Stage=RecoveryStage.Stored;return;}
        if(thing.holdingOwner!=null&&!thing.Spawned)throw new InvalidOperationException("Another holder owns this entity.");
        var previousPawn=TransferringPawn;TransferringPawn=thing as Pawn;Busy=true;
        try
        {
            if(thing.Spawned){RestoreRegisteredPosition(thing,thing.Map);thing.DeSpawn();}
            if(!held.TryAdd(thing,false))throw new InvalidOperationException("Recovery container rejected the original entity.");
            record.Stage=RecoveryStage.Stored;
            LogOutcome(record,IntVec3.Invalid,"Stored");Warn(thing,"Stored");
        }
        finally{Busy=false;TransferringPawn=previousPawn;}
    }
    private bool Move(RecoveryRecord record,LayerMapComponent owner,IntVec3 target)=>Move(record,owner.map,target);
    private bool Move(RecoveryRecord record,Map map,IntVec3 target)
    {
        var thing=record.Thing!;
        if(!RecoveryLanding.Legal(map,thing,target))return false;
        var previousPawn=TransferringPawn;TransferringPawn=thing as Pawn;Busy=true;
        try
        {
            if(thing.Spawned)
            {
                RestoreRegisteredPosition(thing,thing.Map);thing.Position=target;
                if(thing is Pawn pawn)pawn.Notify_Teleported(endCurrentJob:JobInvalid(pawn,map));
            }
            else
            {
                if(!held.Contains(thing))return false;
                var result=GenSpawn.Spawn(thing,target,map,thing.Rotation,WipeMode.Vanish);
                if(result!=thing||!thing.Spawned)return false;
            }
            if(thing.holdingOwner!=map.spawnedThings||thing.Map!=map||thing.Position!=target)throw new InvalidOperationException("Recovery placement did not establish one map owner.");
            Remember(thing,map);Corrected++;
            if(!record.StructuralFall)
            {
                var previous=history.FirstOrDefault(h=>h.Id==thing.thingIDNumber);
                if(previous==null)history.Add(new(){Id=thing.thingIDNumber,Tick=Find.TickManager.TicksGame,Count=1});
                else {previous.Count=Find.TickManager.TicksGame-previous.Tick<=600?previous.Count+1:1;previous.Tick=Find.TickManager.TicksGame;}
            }
            LogOutcome(record,target,record.Stage.ToString());Complete(record);
            if(record.StructuralFall&&!record.FallDamageApplied&&thing is Pawn fallen&&record.FallLevels>0)
            {
                record.FallDamageApplied=true;
                fallen.TakeDamage(new DamageInfo(DamageDefOf.Blunt,8f*record.FallLevels,0));
            }
            return true;
        }
        finally{Busy=false;TransferringPawn=previousPawn;}
    }
    private static bool JobInvalid(Pawn pawn,Map map)
    {
        if(pawn.CurJob is not {} job)return false;
        foreach(var target in new[]{job.targetA,job.targetB,job.targetC})
        {
            if(target.HasThing&&(target.Thing.Destroyed||target.Thing.MapHeld!=null&&target.Thing.MapHeld!=map))return true;
            if(!target.HasThing&&target.IsValid&&(LayerMapComponent.For(map) is {} owner?!owner.TryDecode(target.Cell,out _):!target.Cell.InBounds(map)))return true;
        }
        return false; // Native drivers revalidate reachability; do not clear unrelated reservations.
    }
    private void Complete(RecoveryRecord record,string? outcome=null)
    {
        if(outcome!=null)LogOutcome(record,record.Thing?.Position??IntVec3.Invalid,outcome);
        record.Search?.Dispose();record.Search=null;record.Stage=RecoveryStage.Completed;
        records.Remove(record);if(record.Thing!=null)byId.Remove(record.Thing.thingIDNumber);
    }
    private static void LogOutcome(RecoveryRecord r,IntVec3 target,string result)=>Log.Message($"[RimLayers.Recovery] id={r.Thing?.ThingID} def={r.Thing?.def.defName} map={r.MapId} from={r.Origin} to={target} reason={r.Reason} result={result}");
    private void Warn(Thing? thing,string reason)
    {
        int id=thing?.thingIDNumber??0,now=Find.TickManager?.TicksGame??0;
        if(warnings.TryGetValue(id,out int prior)&&now-prior<1800)return;
        warnings[id]=now;warningCount++;
        if(reason=="UnknownBuilding")Log.Warning($"[RimLayers.Recovery] id={thing?.ThingID} def={thing?.def.defName} from={thing?.Position} reason={reason} result=not-moved");
    }
    public void Retry(RecoveryRecord record)
    {
        if(!records.Contains(record))return;
        record.ManualOnly=false;record.NextAttempt=0;record.Search?.Dispose();record.Search=null;
        history.RemoveAll(h=>h.Id==record.Thing?.thingIDNumber);
    }
    public AcceptanceReport CanRelease(RecoveryRecord record,Map map,IntVec3 target,bool confirmedCrossMap=false)
    {
        if(!records.Contains(record)||record.Thing==null||!held.Contains(record.Thing))return "RL_RecoveryNotStored".Translate();
        if(map.uniqueID!=record.MapId&&(OriginalMap(record)!=null||!confirmedCrossMap))return "RL_RecoveryCrossMapConfirm".Translate();
        return RecoveryLanding.Legal(map,record.Thing,target)?true:"RL_RecoveryIllegalLanding".Translate();
    }
    public bool TryRelease(RecoveryRecord record,Map map,IntVec3 target,bool confirmedCrossMap=false)
    {
        var report=CanRelease(record,map,target,confirmedCrossMap);
        if(!report.Accepted){Messages.Message(report.Reason,MessageTypeDefOf.RejectInput,false);return false;}
        record.Stage=RecoveryStage.Resetting;
        try{return Move(record,map,target);}
        catch(Exception ex){record.ManualOnly=true;if(record.Thing is {Spawned:false,Destroyed:false,holdingOwner:null} orphan)held.TryAdd(orphan,false);Warn(record.Thing,"Failure");Log.Warning($"[RimLayers.Recovery] manual placement failed: {ex}");return false;}
    }
}
