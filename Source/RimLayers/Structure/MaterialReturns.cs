using RimLayers.Core;
using Verse;
namespace RimLayers.Structure;

/// <summary>Only generated demolition/collapse returns, never a copy of a native held item.</summary>
public sealed class MaterialReturns
{
    private readonly LayerMapComponent owner;
    private List<PendingReturn> pending=new();
    public MaterialReturns(LayerMapComponent owner)=>this.owner=owner;
    internal int PendingCount=>pending.Count;
    private bool TryPlace(Thing item,IntVec3 center)
    {
        if(!owner.TryDecode(center,out var layer))return false;
        return GenPlace.TryPlaceThing(item,center,owner.map,ThingPlaceMode.Near,out _,null,
            c=>owner.TryDecode(c,out var to)&&to.LayerId==layer.LayerId&&owner.Structure.HasSurface(c)&&c.Walkable(owner.map));
    }
    public void Place(Thing item,IntVec3 center)
    {
        if(TryPlace(item,center)||item.Destroyed||item.Spawned)return;
        // A failed native Near placement may already have merged part of a stack.
        pending.Add(new PendingReturn{Cell=center,Def=item.def,Count=item.stackCount});item.Destroy(DestroyMode.Vanish);
        Log.Warning($"[RimLayers] Material return has no space on map {owner.map.uniqueID}; remaining quantity is queued and saved.");
    }
    public void Tick()
    {
        if(pending.Count==0||Find.TickManager.TicksGame%60!=0)return;
        foreach(var entry in pending.Take(4).ToArray())
        {
            var item=ThingMaker.MakeThing(entry.Def);item.stackCount=entry.Count;
            if(TryPlace(item,entry.Cell)||item.Destroyed||item.Spawned)pending.Remove(entry);
            else{entry.Count=item.stackCount;item.Destroy(DestroyMode.Vanish);}
        }
    }
    public void ExposeData(){Scribe_Collections.Look(ref pending,"pendingMaterialReturns",LookMode.Deep);if(Scribe.mode==LoadSaveMode.PostLoadInit)pending??=new();}
}
public sealed class PendingReturn:IExposable
{
    public IntVec3 Cell;public ThingDef Def=null!;public int Count;
    public void ExposeData(){Scribe_Values.Look(ref Cell,"cell");Scribe_Defs.Look(ref Def,"def");Scribe_Values.Look(ref Count,"count");}
}
