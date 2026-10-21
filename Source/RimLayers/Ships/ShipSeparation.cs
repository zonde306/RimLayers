using RimLayers.Core;
using RimWorld;
using Verse;
namespace RimLayers.Ships;

/// <summary>Actual native contents survive demolition as the same held Things, never recipe copies.</summary>
public sealed class ShipRecovery:IThingHolder
{
    private readonly LayerMapComponent owner;
    private ThingOwner<Thing> held;
    private List<ShipRecoverySpot> spots=new();
    internal ShipRecovery(LayerMapComponent owner){this.owner=owner;held=new(this);}
    public IThingHolder ParentHolder=>owner.map;
    public ThingOwner GetDirectlyHeldThings()=>held;
    public void GetChildHolders(List<IThingHolder> result)=>ThingOwnerUtility.AppendThingHoldersFromThings(result,held);
    internal void TakeContents(IThingHolder source,IntVec3 origin)
    {
        var container=source.GetDirectlyHeldThings();
        if(container!=null)foreach(var item in container.ToList())
        {
            if(container.TryTransferToContainer(item,held,item.stackCount)>0)spots.Add(new(){id=item.thingIDNumber,cell=origin});
        }
        var children=new List<IThingHolder>();source.GetChildHolders(children);
        foreach(var child in children)if(!ReferenceEquals(child,source))TakeContents(child,origin);
    }
    internal void Flush(ISet<IntVec3>? leaving=null)
    {
        foreach(var item in held.ToList())
        {
            var preferred=spots.FirstOrDefault(s=>s.id==item.thingIDNumber)?.cell??IntVec3.Invalid;
            if(!owner.TryDecode(preferred,out var from))continue;
            var target=owner.LayerRect(from.LayerId).OrderBy(c=>(c-preferred).LengthHorizontalSquared).FirstOrDefault(c=>
                leaving?.Contains(c)!=true&&owner.map.terrainGrid.FoundationAt(c)?.IsSubstructure!=true&&owner.Structure.HasSurface(c)&&c.Standable(owner.map)&&
                (c.GetThingList(owner.map).All(t=>t.def.category!=ThingCategory.Item)||item.def.stackLimit>1),IntVec3.Invalid);
            if(!target.IsValid)continue;
            held.TryDrop(item,target,owner.map,ThingPlaceMode.Near,out _,null,
                c=>owner.TryDecode(c,out var at)&&owner.TryDecode(target,out var center)&&at.LayerId==center.LayerId&&
                    leaving?.Contains(c)!=true&&owner.map.terrainGrid.FoundationAt(c)?.IsSubstructure!=true&&owner.Structure.HasSurface(c)&&c.Walkable(owner.map));
            if(!held.Contains(item))spots.RemoveAll(s=>s.id==item.thingIDNumber);
        }
    }
    internal void Tick(){if(held.Count>0&&Find.TickManager.TicksGame%60==0)Flush();}
    internal void ExposeData()
    {
        Scribe_Deep.Look(ref held,"shipDemolitionRecovery",this);Scribe_Collections.Look(ref spots,"shipRecoveryPositions",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit){held??=new(this);spots??=new();}
    }
}
public sealed class ShipRecoverySpot:IExposable
{
    public int id;public IntVec3 cell;
    public void ExposeData(){Scribe_Values.Look(ref id,"id");Scribe_Values.Look(ref cell,"cell");}
}
internal static class ShipSeparation
{
    internal static AcceptanceReport CheckClearance(ShipCapture c)
    {
        var owner=c.Owner;var map=owner.map;
        foreach(var start in c.Cells)
        {
            owner.TryDecode(start,out var p);
            foreach(int layer in new[]{p.LayerId}.Concat(owner.Layout!.Above(p.LayerId)))
            {
                var at=owner.ToStorage(new(layer,p.X,p.Z));if(c.Cells.Contains(at))continue;
                if(owner.StructuralCells.Contains(at)||owner.Mountains.IsNaturalFloor(at)||map.roofGrid.RoofAt(at)!=null&&!c.HollowRoofs.Contains(at)||
                    at.GetThingList(map).Any(t=>t is Building or Blueprint or Frame&&!c.Straddlers.Contains(t)&&
                        !(t.def.building?.isAttachment==true&&GenConstruct.GetWallAttachedTo(t) is {} wall&&c.Cells.Contains(wall.Position))))
                    return "RL_ShipVerticalBlocked".Translate(at);
            }
        }
        return true;
    }
    internal static void Confirm(Building_GravEngine engine,Action action)
    {
        var c=ShipLayers.Prepare(engine,out var report);
        if(!report.Accepted){Messages.Message(report.Reason,MessageTypeDefOf.RejectInput,false);return;}
        if(c==null||c.Straddlers.Count==0){action();return;}
        var text="RL_ShipDemolitionConfirm".Translate(c.Straddlers.Count,string.Join("\n",c.Straddlers.OrderBy(t=>t.thingIDNumber).Select(t=>t.LabelCap.ToString())));
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text,action,destructive:true));
    }
    internal static void Commit(ShipCapture capture)
    {
        // Called only after all read-only capture checks, immediately before the native payload constructor.
        foreach(var thing in capture.Straddlers.OrderBy(t=>t.thingIDNumber))
        {
            if(!thing.Spawned||thing.Destroyed)continue;
            if(thing is IThingHolder holder)capture.Owner.Recovery.TakeContents(holder,thing.Position);
            // Vanish deliberately avoids demolition refunds and blast/kill damage to the stationary floor.
            thing.Destroy(DestroyMode.Vanish);
        }
        capture.Owner.Recovery.Flush(capture.Cells);
        capture.Owner.Ships.MarkDirty();capture.Owner.Ships.Tick();
    }
}
