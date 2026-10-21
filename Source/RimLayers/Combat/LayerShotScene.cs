using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Combat;

public sealed class LayerShotScene : IShotScene
{
    private readonly LayerMapComponent owner;
    private readonly Thing? ignored;
    private readonly bool includeCover;
    private readonly IntVec3? ignoredCell;
    public LayerShotScene(LayerMapComponent owner, Thing? ignored = null,IntVec3? ignoredCell=null,bool includeCover=false)
    { this.owner=owner; this.ignored=ignored;this.ignoredCell=ignoredCell;this.includeCover=includeCover; }
    public int Layers => owner.Layout!.LayerCount;
    public int MinLayer => owner.Layout!.LowestElevation;
    public int MaxLayer => owner.Layout!.HighestElevation;
    public double LayerHeight => 3;
    private bool Cell(int layer,int x,int z,out IntVec3 storage)
    {
        storage=IntVec3.Invalid;
        if (!owner.Layout!.TryGetLayerAtElevation(layer,out int id)||!owner.Layout.Contains(new LayerCell(id,x,z))) return false;
        storage=owner.ToStorage(new LayerCell(id,x,z));
        return true;
    }
    public bool Floor(int layer,int x,int z) => Cell(layer,x,z,out var c) && owner.Structure.HasSurface(c);
    public bool Roof(int layer,int x,int z) => Cell(layer,x,z,out var c) &&
        owner.map.roofGrid.RoofAt(c)!=null;
    public (double Height,int ThingKey) Solid(int layer,int x,int z)
    {
        if (!Cell(layer,x,z,out var c) || c==ignoredCell) return default;
        Building? building=c.GetEdifice(owner.map);
        if(includeCover)
        {
            Thing? cover=null;double height=0;
            foreach(var thing in c.GetThingList(owner.map))
            {
                if(thing==ignored || thing is Pawn || thing is Building_Door openDoor && openDoor.Open || thing.def.fillPercent<=0.2f)continue;
                double h=ShotProfiles.CoverHeight(thing);
                if(h>height){height=h;cover=thing;}
            }
            return cover==null?default:(height,cover.thingIDNumber);
        }
        if (building == null || building==ignored || building.def.Fillage!=FillCategory.Full) return default;
        if (building is Building_Door door && door.Open) return default;
        return (LayerHeight,building.thingIDNumber);
    }
    public ShotPoint Point(IntVec3 cell,double height=1.35)
    {
        if (!owner.TryDecode(cell,out var logical)) throw new ArgumentOutOfRangeException(nameof(cell));
        return new(logical.X+0.5,owner.Elevation(logical.LayerId)*LayerHeight+height,logical.Z+0.5);
    }
}
