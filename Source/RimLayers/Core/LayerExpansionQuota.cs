using RimLayers.Layout;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Core;

public sealed class LayerReservation : IExposable
{
    public int EngineId;
    public PlanetTile Destination;
    public int Top;
    public void ExposeData()
    {
        Scribe_Values.Look(ref EngineId,"engineId");
        Scribe_Values.Look(ref Destination,"destination");
        Scribe_Values.Look(ref Top,"top");
    }
}

/// <summary>Save-wide promises; actual occupancy is always derived from loaded map layouts.</summary>
public sealed class LayerExpansionQuota : GameComponent
{
    private List<LayerReservation> reservations=new();
    [ThreadStatic] internal static int ActiveCommitment;
    public LayerExpansionQuota(Game game) { }
    public static LayerExpansionQuota? Current=>Verse.Current.Game?.GetComponent<LayerExpansionQuota>();
    public IReadOnlyList<LayerReservation> Reservations=>reservations;
    public int Generated=>Find.Maps.Sum(m=>(LayerMapComponent.For(m)?.Layout?.LayerCount??1)-1);
    public int Reserved=>reservations.GroupBy(r=>r.Destination).Sum(g=>Math.Max(0,g.Max(r=>r.Top)-Highest(MapAt(g.Key))));
    private static Map? MapAt(PlanetTile tile)=>Find.Maps.FirstOrDefault(m=>m.Tile==tile&&!m.IsPocketMap&&m.generatorDef?.isUnderground!=true);
    private static int Highest(Map? map)=>LayerMapComponent.For(map)?.Layout?.HighestElevation??0;
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref reservations,"rimLayersExpansionReservations",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)reservations??=new();
    }
    public override void GameComponentTick()
    {
        // Native Odyssey has one active gravship. Pre-launch callbacks are synchronous;
        // after a cancelled/failed cutscene there is no outstanding transport to protect.
        if(Find.TickManager.TicksGame%60!=0||WorldComponent_GravshipController.CutsceneInProgress)return;
        int active=Find.CurrentGravship?.Engine?.thingIDNumber??0;
        reservations.RemoveAll(r=>r.EngineId!=active);
    }
    public AcceptanceReport Check(Map map,int elevation,int engineId=0)=>Check(map,map.Tile,elevation,engineId);
    private AcceptanceReport Check(Map? map,PlanetTile tile,int elevation,int engineId)
    {
        var settings=RimLayersMod.Settings;
        var layout=LayerMapComponent.For(map)?.Layout;
        int highest=layout?.HighestElevation??0;
        int promised=reservations.FirstOrDefault(r=>r.EngineId==engineId&&r.Destination==tile)?.Top??-1;
        int covered=elevation>0?Math.Max(0,Math.Min(elevation,reservations.Where(r=>r.Destination==tile).Select(r=>r.Top).DefaultIfEmpty(0).Max())-highest):0;
        var decision=ExpansionPolicy.Check(layout?.LowestElevation??0,highest,elevation,settings.UpperLimit,settings.UndergroundLimit,
            tile.Layer.Def.isSpace,settings.SpaceExpansion,Generated,Reserved,settings.GlobalLimit,promised,covered);
        return decision.Allowed?true:("RL_Quota"+decision.Denial).Translate();
    }
    public AcceptanceReport Preview(PlanetTile destination,int top,int engineId)=>Check(MapAt(destination),destination,top,engineId);
    public AcceptanceReport Reserve(int engineId,PlanetTile destination,int top)
    {
        var report=Preview(destination,top,engineId);
        if(!report.Accepted)return report;
        var entry=reservations.FirstOrDefault(r=>r.EngineId==engineId);
        if(entry==null)reservations.Add(new(){EngineId=engineId,Destination=destination,Top=top});
        else {entry.Top=entry.Destination==destination?Math.Max(entry.Top,top):top;entry.Destination=destination;}
        return true;
    }
    // A ship already in flight must be recoverable, including saves predating quota accounting.
    internal void PreserveInFlight(int engineId,PlanetTile destination,int top)
    {
        if(reservations.Any(r=>r.EngineId==engineId&&r.Destination==destination))return;
        Release(engineId);reservations.Add(new(){EngineId=engineId,Destination=destination,Top=top});
    }
    public void Release(int engineId)=>reservations.RemoveAll(r=>r.EngineId==engineId);
    internal void Allocated(Map map)
    {
        int highest=Highest(map);
        reservations.RemoveAll(r=>r.Destination==map.Tile&&r.Top<=highest);
    }
    internal static AcceptanceReport For(Map map,int elevation)=>Current?.Check(map,elevation,ActiveCommitment)??true;
}
