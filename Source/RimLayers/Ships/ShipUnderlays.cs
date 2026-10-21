using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
namespace RimLayers.Ships;

/// <summary>The hidden colony surface stays on its Map; never becomes part of a Gravship.</summary>
public sealed class ShipUnderlays
{
    private readonly LayerMapComponent owner;
    private List<ShipUnderlayCell> cells=new();
    private List<ShipSupportRoofUnderlay> roofs=new();
    internal bool Restoring;
    internal void RebindCore(int oldId,int newId){foreach(var r in roofs.Where(r=>r.engineId==oldId))r.engineId=newId;}
    internal int SupportRoofOwner(IntVec3 cell)=>roofs.FirstOrDefault(r=>r.cell==cell)?.engineId??0;
    internal void CoverSupportRoof(IntVec3 cell,int engineId)
    {
        if(roofs.Any(r=>r.cell==cell))return;
        roofs.Add(new(){cell=cell,engineId=engineId,original=owner.map.roofGrid.RoofAt(cell)});
    }
    internal bool ReleaseSupportRoof(IntVec3 cell,int engineId)
    {
        var saved=roofs.FirstOrDefault(r=>r.cell==cell&&r.engineId==engineId);if(saved==null)return false;
        owner.map.roofGrid.SetRoof(cell,saved.original);roofs.Remove(saved);return true;
    }
    internal ShipUnderlays(LayerMapComponent owner)=>this.owner=owner;
    internal bool Has(IntVec3 c)=>cells.Any(x=>x.cell==c);
    internal bool CanHost(IntVec3 c)=>owner.TryDecode(c,out var l)&&owner.Elevation(l.LayerId)>0&&
        owner.StructuralCells.Contains(c)&&!owner.Mountains.IsNaturalFloor(c)&&
        owner.map.terrainGrid.FoundationAt(c)?.IsSubstructure!=true&&owner.Structure.HasSurface(c);
    internal void Cover(IntVec3 c)
    {
        if(Restoring||Has(c)||!CanHost(c))return;
        var map=owner.map;var g=map.terrainGrid;
        var saved=new ShipUnderlayCell{cell=c,top=g.TopTerrainAt(c),under=g.UnderTerrainAt(c),temp=g.TempTerrainAt(c),
            foundation=g.FoundationAt(c),color=g.ColorAt(c),structural=owner.StructuralCells.Contains(c),
            zones=map.zoneManager.AllZones.Where(z=>z.Cells.Contains(c)).ToList(),
            areas=map.areaManager.AllAreas.Where(a=>a[c]).ToList(),
            designations=map.designationManager.AllDesignationsAt(c).Where(d=>!d.target.HasThing).ToList()};
        cells.Add(saved);
        foreach(var zone in saved.zones)
        {
            zone.RemoveCell(c);
            // Empty host zones remain the same native registered objects while covered.
            if(!map.zoneManager.AllZones.Contains(zone))map.zoneManager.RegisterZone(zone);
        }
        foreach(var area in saved.areas)area[c]=false;
        foreach(var designation in saved.designations)map.designationManager.RemoveDesignation(designation);
        if(g.FoundationAt(c)!=null)g.RemoveFoundation(c,false);
        g.SetTerrain(c,RimLayersDefOf.RL_OpenAir);
    }
    internal bool Release(IntVec3 c)
    {
        var saved=cells.FirstOrDefault(x=>x.cell==c);if(saved==null)return false;
        var g=owner.map.terrainGrid;Restoring=true;
        try
        {
            if(g.FoundationAt(c)!=null)g.RemoveFoundation(c,false);
            g.SetTerrain(c,RimLayersDefOf.RL_OpenAir);
            if(saved.foundation!=null)g.SetFoundation(c,saved.foundation);
            g.SetTerrain(c,saved.top);
            if(saved.under!=null)g.SetUnderTerrain(c,saved.under);
            if(saved.temp!=null)g.SetTempTerrain(c,saved.temp);
            g.SetTerrainColor(c,saved.color);
            if(saved.structural)owner.StructuralCells.Add(c);else owner.StructuralCells.Remove(c);
            foreach(var zone in saved.zones.Where(z=>z!=null&&owner.map.zoneManager.AllZones.Contains(z)))
                if(!zone.Cells.Contains(c)&&owner.map.zoneManager.ZoneAt(c)==null)zone.AddCell(c);
            foreach(var area in saved.areas.Where(a=>a!=null&&owner.map.areaManager.AllAreas.Contains(a)))area[c]=true;
            foreach(var designation in saved.designations)
                if(owner.map.designationManager.DesignationAt(c,designation.def)==null)owner.map.designationManager.AddDesignation(designation);
            cells.Remove(saved);owner.Ships.Forget(c);
            owner.map.pathing.RecalculatePerceivedPathCostAt(c);owner.Structure.RoofChanged(c);
            owner.Invalidate(c,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Light);
            return true;
        }
        finally{Restoring=false;}
    }
    internal void ExposeData()
    {
        Scribe_Collections.Look(ref cells,"shipColonyUnderlays",LookMode.Deep);
        Scribe_Collections.Look(ref roofs,"shipSupportRoofUnderlays",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit){cells??=new();roofs??=new();}
    }
}
public sealed class ShipSupportRoofUnderlay:IExposable
{
    public IntVec3 cell;public int engineId;public RoofDef? original;
    public void ExposeData(){Scribe_Values.Look(ref cell,"cell");Scribe_Values.Look(ref engineId,"engineId");Scribe_Defs.Look(ref original,"original");}
}
public sealed class ShipUnderlayCell:IExposable
{
    public IntVec3 cell;public TerrainDef top=null!;public TerrainDef? under,temp,foundation;
    public ColorDef? color;public bool structural;
    public List<Zone> zones=new();public List<Area> areas=new();public List<Designation> designations=new();
    public void ExposeData()
    {
        Scribe_Values.Look(ref cell,"cell");Scribe_Values.Look(ref structural,"structural");
        Scribe_Defs.Look(ref top,"top");Scribe_Defs.Look(ref under,"under");Scribe_Defs.Look(ref temp,"temp");
        Scribe_Defs.Look(ref foundation,"foundation");Scribe_Defs.Look(ref color,"color");
        Scribe_Collections.Look(ref designations,"designations",LookMode.Deep);
        Scribe_Collections.Look(ref zones,"zones",LookMode.Reference);Scribe_Collections.Look(ref areas,"areas",LookMode.Reference);
        if(Scribe.mode==LoadSaveMode.PostLoadInit){zones??=new();areas??=new();designations??=new();}
    }
}
[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.SetFoundation))]
internal static class ShipBaseCover
{
    static void Prefix(IntVec3 c,TerrainDef newTerr,Map ___map)
    {
        var o=LayerMapComponent.For(___map);
        if(newTerr==TerrainDefOf.Substructure&&o!=null&&!o.Underlays.Restoring)o.Underlays.Cover(c);
    }
}
[HarmonyPatch(typeof(TerrainGrid),nameof(TerrainGrid.RemoveFoundation))]
internal static class ShipBaseRelease
{
    static void Postfix(IntVec3 c,bool doLeavings,Map ___map)
    {
        var o=LayerMapComponent.For(___map);
        if(o==null||o.Underlays.Restoring)return;
        if(doLeavings&&___map.terrainGrid.FoundationAt(c)==null)o.Underlays.Release(c);
        o.Ships.MarkDirty();
    }
}
