using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Structure;

/// <summary>Incoming objects stop at the first structural plane; native holders own all payloads.</summary>
public sealed class SkyfallService
{
    private readonly LayerMapComponent owner;
    private readonly HashSet<int> warned=new();
    public int ImpactCommits { get; internal set; }
    public SkyfallService(LayerMapComponent owner)=>this.owner=owner;
    internal static bool Incoming(Skyfaller faller)=>!faller.def.skyfaller.reversed && faller is not Skyfaller_FlyingPawn;

    public bool Trace(Skyfaller faller,IntVec3 requested,Rot4 rotation,out IntVec3 hit)
    {
        hit=requested;
        if(!owner.TryDecode(requested,out var center))return false;
        var footprint=new HashSet<IntVec3>(GenAdj.OccupiedRect(requested,rotation,faller.def.Size));
        foreach(Thing payload in faller.GetDirectlyHeldThings())
            footprint.UnionWith(GenAdj.OccupiedRect(requested,payload.Rotation,payload.def.Size));
        if(footprint.Any(c=>!owner.TryDecode(c,out var local) || local.LayerId!=center.LayerId))return false;
        foreach(int layer in owner.Layout!.OrderedLayers.Reverse().Where(l=>owner.Elevation(l)>=0))
            foreach(IntVec3 c in footprint)
            {
                owner.TryDecode(c,out var local);
                if(owner.Structure.HasSurface(owner.ToStorage(new LayerCell(layer,local.X,local.Z))))
                {hit=owner.ToStorage(new LayerCell(layer,center.X,center.Z));return true;}
            }
        return false;
    }

    public bool ValidFootprint(ThingDef def,IntVec3 at,Rot4 rot,int layer)=>GenAdj.OccupiedRect(at,rot,def.Size).All(c=>
        owner.TryDecode(c,out var local) && local.LayerId==layer && owner.Structure.HasSurface(c));

    public bool CanArrive(IntVec3 c,bool punch)
    {
        if(!owner.TryDecode(c,out var cell) || owner.Elevation(cell.LayerId)<0 || !owner.Structure.HasSurface(c))return false;
        foreach(int layer in new[]{cell.LayerId}.Concat(owner.Layout!.Above(cell.LayerId)))
        {
            IntVec3 at=owner.ToStorage(new LayerCell(layer,cell.X,cell.Z));
            if(owner.Elevation(layer)>owner.Elevation(cell.LayerId) && owner.Structure.HasSurface(at))return false;
            if(owner.map.roofGrid.RoofAt(at) is { } roof && (!punch || roof.isThickRoof))return false;
        }
        return true;
    }

    internal bool BeforeTick(Skyfaller faller)
    {
        if(SkyfallAccess.Impacted(faller))
        {
            if((Find.TickManager.TicksGame+faller.thingIDNumber)%30==0)
            {
                RetryPayload(faller);
                if(faller.GetDirectlyHeldThings().Count==0){Recovered(faller,faller.Position);faller.Destroy();}
                else Waiting(faller);
            }
            return false;
        }
        if(!Trace(faller,faller.Position,faller.Rotation,out var hit)){Waiting(faller);return false;}
        if(hit!=faller.Position)faller.Position=hit;
        return true;
    }

    private static void RetryPayload(Skyfaller faller)
    {
        // Retry placement only; shuttle pre-commit callbacks wait until its payload fits.
        if(faller is DropPodIncoming incoming && incoming.Contents.spawnWipeMode is { } wipe)
        {
            var previous=SkyPayload.Enter(faller);
            try
            {
                var held=faller.GetDirectlyHeldThings();
                for(int i=held.Count-1;i>=0;i--)GenSpawn.Spawn(held[i],faller.Position,faller.Map,wipe);
            }
            finally{SkyPayload.Current=previous;}
        }
        else if(faller is ShuttleIncoming)SkyfallAccess.SpawnVirtual(faller);
        else SkyfallAccess.SpawnBase(faller);
    }
    internal bool IsWaiting(Thing parent)=>warned.Contains(parent.thingIDNumber);
    internal void Recovered(Thing parent,IntVec3 at)
    {
        if(warned.Remove(parent.thingIDNumber))Messages.Message("RL_SkyfallRecovered".Translate(parent.LabelShortCap),new TargetInfo(at,owner.map),MessageTypeDefOf.PositiveEvent,false);
    }
    internal void PunchUpperRoofs(Skyfaller faller)
    {
        if(!faller.def.skyfaller.hitRoof || !owner.TryDecode(faller.Position,out var center))return;
        var logical=faller.OccupiedRect().Select(c=>{owner.TryDecode(c,out var local);return local;}).ToList();
        foreach(int layer in owner.Layout!.Above(center.LayerId).Reverse())
        {
            var cells=logical.Select(c=>owner.ToStorage(new LayerCell(layer,c.X,c.Z)))
                .Where(c=>owner.map.roofGrid.RoofAt(c)!=null).ToList();
            if(cells.Count>0)RoofCollapserImmediate.DropRoofInCells(cells,owner.map);
        }
    }
    internal void Waiting(Thing parent)
    {
        if(warned.Add(parent.thingIDNumber))Messages.Message("RL_SkyfallWaiting".Translate(parent.LabelShortCap),parent,MessageTypeDefOf.CautionInput,false);
    }
}

internal static class SkyfallAccess
{
    internal static readonly AccessTools.FieldRef<Skyfaller,bool> Impacted=AccessTools.FieldRefAccess<Skyfaller,bool>("hasImpacted");
    internal static readonly Action<Skyfaller> SpawnVirtual=AccessTools.MethodDelegate<Action<Skyfaller>>(AccessTools.Method(typeof(Skyfaller),"SpawnThings"));
    internal static readonly Action<Skyfaller> SpawnBase=AccessTools.MethodDelegate<Action<Skyfaller>>(AccessTools.Method(typeof(Skyfaller),"SpawnThings"),virtualCall:false);
    internal delegate bool FindSpot(IntVec3 center,Rot4 rotation,Map map,Thing thing,bool allowStacking,out IntVec3 spot,Predicate<IntVec3> validator);
    internal static readonly FindSpot FindNear=AccessTools.MethodDelegate<FindSpot>(AccessTools.Method(typeof(GenPlace),"TryFindPlaceSpotNear"));
}

internal sealed record SkyPayloadContext(Thing Parent,Map Map,IntVec3 Position,int Layer);
internal static class SkyPayload
{
    [ThreadStatic] internal static SkyPayloadContext? Current;
    [ThreadStatic] internal static ActiveTransporter? RecoveringPod;
    internal static SkyPayloadContext? Enter(Thing parent)
    {
        var previous=Current;
        var owner=LayerMapComponent.For(parent.Map);
        Current=owner!=null && owner.TryDecode(parent.Position,out var local)?new(parent,parent.Map,parent.Position,local.LayerId):null;
        return previous;
    }
    internal static bool Allowed(ThingDef def,IntVec3 at,Rot4 rotation,Map map)=>Current is not { } scope || scope.Map!=map ||
        LayerMapComponent.For(map)!.Skyfall.ValidFootprint(def,at,rotation,scope.Layer);
    internal static bool ReadyForShuttle(Skyfaller faller)
    {
        if(LayerMapComponent.For(faller.Map)==null)return true;
        var previous=Enter(faller);
        try
        {
            var used=new HashSet<IntVec3>();
            foreach(Thing payload in faller.GetDirectlyHeldThings())
            {
                bool Valid(IntVec3 c)=>SafeNear(payload.def,c,payload.Rotation,faller.Map) &&
                    !GenAdj.OccupiedRect(c,payload.Rotation,payload.def.Size).Any(used.Contains);
                if(!SkyfallAccess.FindNear(faller.Position,payload.Rotation,faller.Map,payload,false,out var at,Valid))return false;
                used.UnionWith(GenAdj.OccupiedRect(at,payload.Rotation,payload.def.Size));
            }
            return true;
        }
        finally{Current=previous;}
    }
    internal static bool SafeNear(ThingDef def,IntVec3 at,Rot4 rotation,Map map)
    {
        if(!Allowed(def,at,rotation,map))return false;
        if((def.category==ThingCategory.Item || def.category==ThingCategory.Pawn) &&
            GenAdj.OccupiedRect(at,rotation,def.Size).Any(c=>!c.Walkable(map)))return false;
        var parent=Current?.Parent;
        if(GenAdj.OccupiedRect(at,rotation,def.Size).Any(c=>c.GetThingList(map).Any(t=>t!=parent &&
            (t is Pawn && def.passability==Traversability.Impassable ||
             (t is Building || t is Frame) && GenSpawn.SpawningWipes(def,t.def)))))return false;
        // Filth/plants may be cleared by native spawning. Real items need a complete safe move plan.
        return def.category!=ThingCategory.Building || def.surfaceType!=SurfaceType.None ||
            def.passability==Traversability.Standable || MoveItemsSafely(at,rotation,def,map,commit:false);
    }
    internal static bool MoveItemsSafely(IntVec3 at,Rot4 rotation,ThingDef def,Map map,bool commit=true)
    {
        if(Current is not { } scope || scope.Map!=map)return true;
        if(!Allowed(def,at,rotation,map))return false;
        var occupied=GenAdj.OccupiedRect(at,rotation,def.Size);
        var plan=new List<(Thing Item,IntVec3 Spot)>();
        foreach(Thing item in occupied.SelectMany(c=>c.GetThingList(map)).Where(t=>t.def.category==ThingCategory.Item && t!=scope.Parent).Distinct())
        {
            bool Valid(IntVec3 c)=>!occupied.Contains(c) && !plan.Any(p=>p.Spot==c) &&
                SafeNear(item.def,c,item.Rotation,map) && c.GetItemCount(map)<c.GetMaxItemsAllowedInCell(map);
            if(!SkyfallAccess.FindNear(item.Position,item.Rotation,map,item,false,out var spot,Valid))return false;
            plan.Add((item,spot));
        }
        if(commit)foreach(var move in plan)move.Item.Position=move.Spot;
        return true;
    }
    internal static bool PrepareSpawn(Thing thing,IntVec3 at,Rot4 rotation,Map map)
    {
        if(!Allowed(thing.def,at,rotation,map))return false;
        if(Current is not { } scope || scope.Map!=map)return true;
        if((thing is Pawn || thing.def.category==ThingCategory.Item) && GenAdj.OccupiedRect(at,rotation,thing.def.Size).Any(c=>!c.Walkable(map)))return false;
        // A pod is itself an Item. Temporarily remove its shell before an exact-cell unload;
        // native finalization destroys it on success, or our scoped finalizer restores the holder.
        if(scope.Parent is ActiveTransporter pod && pod.Spawned && thing!=pod && GenAdj.OccupiedRect(at,rotation,thing.def.Size).Contains(pod.Position))pod.DeSpawn();
        bool full=thing.def.category==ThingCategory.Item && at.GetItemCount(map)>=at.GetMaxItemsAllowedInCell(map);
        bool clearsItems=thing.def.category==ThingCategory.Building && thing.def.surfaceType==SurfaceType.None && thing.def.passability!=Traversability.Standable;
        return !full && !clearsItems || MoveItemsSafely(at,rotation,thing.def,map);
    }
    internal static bool ClearIfPlaced(ThingOwner held,DestroyMode mode)
    {
        bool protectedHolder=held.Owner is Skyfaller faller && SkyfallService.Incoming(faller) && LayerMapComponent.For(faller.Map)!=null ||
            Current?.Parent is ActiveTransporter pod && held==pod.Contents.innerContainer;
        if(protectedHolder && held.Count>0)return false;
        held.ClearAndDestroyContents(mode);return true;
    }
    internal static void KeepUnplaced(ThingOwner held,DestroyMode mode)=>ClearIfPlaced(held,mode);
    internal static bool SpawnImpactEffect(ThingDef def,IntVec3 at,Map map,out Thing? thing,WipeMode wipe,bool canWipeEdifices)
    {
        var owner=LayerMapComponent.For(map);
        if(owner!=null && (!owner.TryDecode(at,out var local) || !owner.Skyfall.ValidFootprint(def,at,def.defaultPlacingRot,local.LayerId)))
        {thing=null;return false;}
        return GenSpawn.TrySpawn(def,at,map,out thing,wipe,canWipeEdifices);
    }
    internal static void FinishImpact(Thing source,DestroyMode mode)
    {
        if(source is Skyfaller faller && SkyfallService.Incoming(faller) && LayerMapComponent.For(faller.Map) is { } owner && faller.GetDirectlyHeldThings().Count>0)
        {owner.Skyfall.Waiting(faller);return;}
        source.Destroy(mode);
    }
}
