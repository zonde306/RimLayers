using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

public sealed class FireLinks
{
    private readonly LayerMapComponent owner;
    public long Attempts { get; private set; }
    public FireLinks(LayerMapComponent owner)=>this.owner=owner;
    public IEnumerable<IntVec3> Targets(Fire source,int direction)
    {
        if(!source.Spawned || !owner.TryDecode(source.Position,out var here))yield break;
        if(!owner.Layout!.TryGetAdjacentLayer(here.LayerId,direction,out int layer))yield break;
        IntVec3 mouth=owner.ToStorage(new LayerCell(layer,here.X,here.Z));
        IntVec3 lower=direction>0 ? source.Position : mouth,upper=direction>0 ? mouth : source.Position;
        if(!VolumeLinks.Open(owner,lower,upper,LayerChangeKind.Fire))yield break;
        if(owner.Structure.HasSurface(mouth)) { yield return mouth;yield break; }
        // A hot plume exits an open shaft before spreading onto its neighbouring floor.
        foreach(IntVec3 offset in GenAdj.AdjacentCells)
        {
            IntVec3 cell=mouth+offset;
            if(owner.TryDecode(cell,out var to) && to.LayerId==layer && owner.Structure.HasSurface(cell) &&
                GenSight.LineOfSight(mouth,cell,owner.map,skipFirstCell:true))yield return cell;
        }
    }
    public void Spread(Fire source)
    {
        Attempts++;
        foreach(int direction in new[]{1,-1})
        {
            var candidates=Targets(source,direction).Where(c=>FireUtility.ChanceToStartFireIn(c,owner.map)>0).ToList();
            if(candidates.Count==0)continue;
            IntVec3 cell=candidates.RandomElement();
            float chance=Math.Clamp(FireUtility.ChanceToStartFireIn(cell,owner.map),0,1)*(direction>0?0.2f:0.05f);
            if(Rand.Chance(chance))FireUtility.TryStartFireIn(cell,owner.map,0.1f,source.instigator);
        }
    }
}
