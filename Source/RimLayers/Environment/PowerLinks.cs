using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

/// <summary>Derived vertical adjacency. Native transmitters, batteries and PowerNet own all energy.</summary>
public sealed class PowerLinks
{
    private readonly LayerMapComponent owner;
    private readonly Dictionary<(int X,int Z),HashSet<CompPowerRiser>> columns=new();
    private readonly HashSet<(int X,int Z)> dirty=new();
    private bool flushing,initialized;
    private readonly HashSet<CompPower> unsupported=new();
    private readonly Dictionary<CompFlickable,bool> restoreSwitches=new();
    public long Revision { get; private set; }
    public PowerLinks(LayerMapComponent owner)
    {
        this.owner=owner;
        owner.Changed+=Changed;
    }
    public void Register(CompPowerRiser riser,bool notify=true)
    {
        if(!owner.TryDecode(riser.parent.Position,out var cell)) return;
        var key=(cell.X,cell.Z);
        if(!columns.TryGetValue(key,out var set)) columns[key]=set=new();
        if(set.Add(riser) && notify) Mark(riser.parent.Position);
    }
    public void Remove(CompPowerRiser riser)
    {
        if(!owner.TryDecode(riser.parent.Position,out var cell)) return;
        var key=(cell.X,cell.Z);
        if(columns.TryGetValue(key,out var set))
        {
            set.Remove(riser);
            if(set.Count==0) columns.Remove(key);
        }
        dirty.Add(key);Revision++;
    }
    public void Mark(IntVec3 storage)
    {
        if(flushing || !owner.TryDecode(storage,out var cell)) return;
        dirty.Add((cell.X,cell.Z));Revision++;
    }
    public void Rebuild()
    {
        columns.Clear();dirty.Clear();unsupported.Clear();restoreSwitches.Clear();
        foreach(var thing in owner.map.listerThings.ThingsOfDef(RimLayersDefOf.RL_PowerRiser))
            if(thing.TryGetComp<CompPowerRiser>() is {} port) Register(port,false);
        initialized=true;
    }
    public void Flush()
    {
        if(!initialized) Rebuild();
        if(dirty.Count==0) return;
        var pending=dirty.ToArray();dirty.Clear();flushing=true;
        try
        {
            // Queue the remote endpoints too. The native first phase tears down OLD networks
            // before its second phase floods the new graph; no manually merged/stale PowerNets.
            foreach(var key in pending)
                if(columns.TryGetValue(key,out var ports))
                    foreach(var port in ports)
                        if(port.parent.Spawned && port.parent.Map==owner.map)
                            owner.map.powerNetManager.Notfiy_TransmitterTransmitsPowerNowChanged(port.parent.GetComp<CompPower>());
        }
        finally { flushing=false; }
    }
    public IEnumerable<IntVec3> VerticalNeighbors(Thing thing)
    {
        if(thing is not Building building || thing.TryGetComp<CompPowerRiser>()==null ||
            !thing.Spawned || !building.TransmitsPowerNow || !owner.TryDecode(thing.Position,out var cell) ||
            !columns.TryGetValue((cell.X,cell.Z),out var peers)) yield break;
        foreach(var peer in peers)
            if(peer.parent is Building target && target!=thing && target.Spawned && target.Map==owner.map &&
                owner.TryDecode(target.Position,out var destination) && owner.Layout!.Distance(destination.LayerId,cell.LayerId)==1 && target.TransmitsPowerNow)
                yield return target.Position;
    }
    private void Changed(LayerCell cell,LayerChangeKind kind)
    {
        if((kind&LayerChangeKind.Structure)==0) return;
        foreach(var storage in owner.Column(cell.X,cell.Z))
        {
            if(!storage.InBounds(owner.map) || storage.GetTransmitter(owner.map)?.PowerComp is not {} comp) continue;
            bool supported=HasSurface(comp.parent);
            bool restored=supported && unsupported.Remove(comp);
            if(supported && !restored && (comp.transNet!=null || !comp.TransmitsPowerNow)) continue;
            if(!supported) unsupported.Add(comp);
            // A support transition is not a user request to turn an off generator on. Native
            // CompPowerTrader.ResetPowerVars resets its flickable, so retain that native setting.
            if(comp is CompPowerTrader && comp.parent.TryGetComp<CompFlickable>() is {} flick && !restoreSwitches.ContainsKey(flick))
                restoreSwitches.Add(flick,flick.SwitchIsOn);
            owner.map.powerNetManager.Notfiy_TransmitterTransmitsPowerNowChanged(comp);
        }
        if(columns.ContainsKey((cell.X,cell.Z))) Mark(owner.ToStorage(cell));
    }
    public void AfterUpdate()
    {
        foreach(var entry in restoreSwitches)
            if(entry.Key.parent.Spawned && entry.Key.parent.Map==owner.map) entry.Key.SwitchIsOn=entry.Value;
        restoreSwitches.Clear();
        unsupported.RemoveWhere(c=>!c.parent.Spawned || c.parent.Map!=owner.map);
    }
    internal static bool SameLayer(Map map,IntVec3 a,IntVec3 b)
    {
        var owner=LayerMapComponent.For(map);
        return owner==null || owner.TryDecode(a,out var first) && owner.TryDecode(b,out var second) && first.LayerId==second.LayerId;
    }
    internal static bool HasSurface(Thing thing)
    {
        var owner=LayerMapComponent.For(thing.Map);
        return owner==null || thing.OccupiedRect().Cells.All(owner.Structure.HasSurface);
    }
    public static IEnumerable<IntVec3> Neighbors(Thing thing)
    {
        var owner=LayerMapComponent.For(thing.Map);
        foreach(var cell in GenAdj.CellsAdjacentCardinal(thing))
            if(owner==null || SameLayer(owner.map,thing.Position,cell)) yield return cell;
        if(owner!=null)
            foreach(var cell in owner.Power.VerticalNeighbors(thing)) yield return cell;
    }
    internal CompPower? BestTransmitter(IntVec3 position,List<PowerNet>? disallowed)
    {
        if(!owner.TryDecode(position,out var cell)) return null;
        var rect=CellRect.SingleCell(position).ExpandedBy(6);
        CellRect bounds=owner.LayerRect(cell.LayerId);
        rect.minX=Math.Max(rect.minX,bounds.minX);rect.maxX=Math.Min(rect.maxX,bounds.maxX);
        rect.minZ=Math.Max(rect.minZ,bounds.minZ);rect.maxZ=Math.Min(rect.maxZ,bounds.maxZ);
        float best=float.PositiveInfinity;CompPower? result=null;
        foreach(var candidate in rect.Cells)
        {
            Building? building=candidate.GetTransmitter(owner.map);
            if(building==null || building.Destroyed) continue;
            CompPower? comp=building.PowerComp;
            if(comp==null || !comp.TransmitsPowerNow || building.def.building?.allowWireConnection==false ||
                disallowed?.Contains(comp.transNet)==true) continue;
            float distance=(building.Position-position).LengthHorizontalSquared;
            if(distance<best) { best=distance;result=comp; }
        }
        return result;
    }
}

public sealed class CompProperties_PowerRiser : CompProperties
{
    public CompProperties_PowerRiser()=>compClass=typeof(CompPowerRiser);
}
public sealed class CompPowerRiser : ThingComp
{
    public override void PostSpawnSetup(bool respawningAfterLoad)
    { base.PostSpawnSetup(respawningAfterLoad);LayerMapComponent.For(parent.Map)?.Power.Register(this,!respawningAfterLoad); }
    public override void PostDeSpawn(Map map,DestroyMode mode=DestroyMode.Vanish)
    { LayerMapComponent.For(map)?.Power.Remove(this);base.PostDeSpawn(map,mode); }
    public override string CompInspectStringExtra()
    {
        var owner=LayerMapComponent.For(parent.Map);
        if(owner==null) return "RL_RequiresLayerMap".Translate();
        var layers=owner.Power.VerticalNeighbors(parent).Select(c=>{owner.TryDecode(c,out var p);return owner.LevelName(p.LayerId);}).OrderBy(x=>x).ToList();
        return layers.Count==0 ? "RL_PowerNoPeer".Translate() : "RL_PowerPeers".Translate(string.Join(", ",layers));
    }
}
