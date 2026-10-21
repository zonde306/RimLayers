using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Recovery;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private sealed record RecoveryPerfSample(double TickMs,double InspectionMs,double RecoveryMs,long Inspected,long Candidates,long Corrected);
    private static readonly List<Pawn> perfActors=new();
    private static readonly List<Thing> perfItems=new();
    private static readonly List<RecoveryPerfSample> perfSamples=new();
    private static readonly List<object> perfResults=new();
    private static Map perfMain=null!,perfOther=null!;
    private static int perfPhase,perfTicks;
    private static long perfStamp,perfInspected,perfCandidates,perfCorrected;
    private static double perfInspectMs,perfRecoverMs;
    private static void BeginSettingsPerf()
    {
        perfMain=map;var settings=RimLayersMod.Settings;settings.UpperLimit=5;settings.UndergroundLimit=5;settings.GlobalLimit=20;
        LiveMapExpansion.EnsureElevation(map,5);LiveMapExpansion.EnsureElevation(map,-5);
        owner=LayerMapComponent.For(map)!;
        ClearPerfColumn(map);
        foreach(var existing in map.mapPawns.FreeColonistsSpawned.Take(60).ToArray())perfActors.Add(existing);
        while(perfActors.Count<60)perfActors.Add(PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer));
        for(int i=0;i<perfActors.Count;i++)
        {
            var p=perfActors[i];p.jobs.StopAll();
            var at=new IntVec3(80+i%10,0,80+i/10);
            if(p.Spawned){p.Position=at;p.Notify_Teleported();}else GenSpawn.Spawn(p,at,map);
            p.drafter.Drafted=true;
        }
        for(int i=0;i<30;i++){var t=ThingMaker.MakeThing(ThingDefOf.Silver);t.stackCount=i+1;GenSpawn.Spawn(t,new IntVec3(80+i%10,0,90+i/10),map);perfItems.Add(t);}
        Check("perf-single-map-is-250x250-and-eleven-real-floors",owner.Layout!.Width==250&&owner.Layout.Height==250&&owner.Layout.LayerCount==11&&LayerExpansionQuota.Current!.Generated==10);
        Check("perf-native-colonist-cohort-has-sixty-unique-originals",perfActors.Count==60&&perfActors.Distinct().Count()==60&&perfActors.All(p=>p.Spawned&&!p.Dead));
        perfPhase=0;perfTicks=0;stage=460;
        owner.SetViewedLayer(owner.Layout.LayerAt(5));
        var view=owner.AtElevation(5,85,85);CameraJumper.TryJump(view,map,CameraJumper.MovementMode.Cut);
        Find.CameraDriver.SetRootPosAndSize(view.ToVector3Shifted(),24);
        pendingScreenshot=Path.Combine(directory,"eleven-floor-upper-view.png");screenshotHold=4;
    }
    private static void ClearPerfColumn(Map target)=>ClearRecoveryColumn(target,new CellRect(75,75,30,30));
    private static void SettingsPerfBeforeTick()
    {
        var s=RecoveryService;perfInspected=s.Inspected;perfCandidates=s.CandidateCells;perfCorrected=s.Corrected;
        perfInspectMs=s.InspectionMilliseconds;perfRecoverMs=s.RecoveryMilliseconds;perfStamp=Stopwatch.GetTimestamp();
    }
    private static void SettingsPerfAfterTick()
    {
        if(perfPhase==0||finished)return;
        var s=RecoveryService;
        perfSamples.Add(new(Stopwatch.GetElapsedTime(perfStamp).TotalMilliseconds,s.InspectionMilliseconds-perfInspectMs,s.RecoveryMilliseconds-perfRecoverMs,s.Inspected-perfInspected,s.CandidateCells-perfCandidates,s.Corrected-perfCorrected));
    }
    private static void SettingsPerfStep()
    {
        perfTicks++;
        if(perfPhase==0&&perfTicks>=120){perfPhase=1;perfTicks=0;return;}
        if(perfPhase==1&&perfTicks>=1200){FinishPerfPhase("single-map-idle");InjectPerfAir();perfPhase=2;perfTicks=0;return;}
        if(perfPhase==2&&perfTicks>=240)
        {
            CheckPerfRecovered();FinishPerfPhase("single-map-recovery-burst");
            var tile=Find.WorldGrid.Tiles.First(t=>t.tile!=perfMain.Tile&&t.PrimaryBiome==BiomeDefOf.TemperateForest&&TileFinder.IsValidTileForNewSettlement(t.tile)).tile;
            perfOther=MakeUnmanagedMap(tile,250,250);perfOther.Parent.SetFaction(Faction.OfPlayer);
            Current.Game.CurrentMap=perfMain;map=perfMain;owner=LayerMapComponent.For(map)!;
            LiveMapExpansion.EnsureElevation(perfOther,5);LiveMapExpansion.EnsureElevation(perfOther,-5);
            ClearPerfColumn(perfOther);
            for(int i=30;i<60;i++){var p=perfActors[i];p.DeSpawn();GenSpawn.Spawn(p,new IntVec3(80+i%10,0,80+(i-30)/10),perfOther);p.drafter.Drafted=true;}
            for(int i=15;i<30;i++){var t=perfItems[i];t.DeSpawn();GenSpawn.Spawn(t,new IntVec3(80+i%10,0,90+(i-15)/10),perfOther);}
            Check("perf-two-real-maps-total-twenty-extra-floors",LayerExpansionQuota.Current!.Generated==20&&LayerMapComponent.For(perfOther)!.Layout!.LayerCount==11);
            perfPhase=3;perfTicks=0;return;
        }
        if(perfPhase==3&&perfTicks>=1200){FinishPerfPhase("two-map-idle");InjectPerfAir();perfPhase=4;perfTicks=0;return;}
        if(perfPhase==4&&perfTicks>=240){CheckPerfRecovered();FinishPerfPhase("two-map-recovery-burst");WriteSettingsPerf();Finish();return;}
    }
    private static void InjectPerfAir()
    {
        foreach(var t in perfActors.Cast<Thing>().Concat(perfItems))
        {
            var o=LayerMapComponent.For(t.Map)!;o.TryDecode(t.Position,out var at);
            t.Position=o.ToStorage(new(o.Layout!.LayerAt(5),at.X,at.Z));
            if(t is Pawn p)p.Notify_Teleported(endCurrentJob:false);
        }
    }
    private static void CheckPerfRecovered()
    {
        Check("perf-burst-keeps-every-original-on-supported-ground",perfActors.Cast<Thing>().Concat(perfItems).All(t=>t.Spawned&&!t.Destroyed&&LayerMapComponent.For(t.Map) is {} o&&o.IsGroundCell(t.Position)&&RecoveryLanding.HasSupport(o,t,t.Position)),string.Join(";",perfActors.Cast<Thing>().Concat(perfItems).Where(t=>!t.Spawned||!LayerMapComponent.For(t.Map)!.IsGroundCell(t.Position)).Select(t=>$"{t.ThingID}:{t.Position}:spawned={t.Spawned}")));
        Check("perf-burst-leaves-no-held-or-duplicate-actor",perfActors.All(p=>!p.Dead&&p.holdingOwner==p.Map.spawnedThings&&p.Map.mapPawns.AllPawnsSpawned.Count(q=>q==p)==1)&&!RecoveryService.Records.Any(r=>r.Thing!=null&&(r.Thing is Pawn p&&perfActors.Contains(p)||perfItems.Contains(r.Thing))));
    }
    private static void FinishPerfPhase(string name)
    {
        Check("perf-shared-inspection-and-search-budget-"+name,perfSamples.All(s=>s.Inspected<=24&&s.Candidates<=512));
        perfResults.Add(new{name,samples=perfSamples.Count,inspection=ScaleStats(perfSamples.Select(s=>s.InspectionMs).ToList()),recovery=ScaleStats(perfSamples.Select(s=>s.RecoveryMs).ToList()),nativeTick=ScaleStats(perfSamples.Select(s=>s.TickMs).ToList()),inspected=perfSamples.Sum(s=>s.Inspected),candidates=perfSamples.Sum(s=>s.Candidates),corrected=perfSamples.Sum(s=>s.Corrected),maps=Find.Maps.Select(m=>new{m.uniqueID,storage=m.Size.ToString(),layers=LayerMapComponent.For(m)?.Layout?.LayerCount,things=m.listerThings.AllThings.Count,pawns=m.mapPawns.AllPawnsSpawned.Count}).ToArray(),raw=perfSamples.ToArray()});
        perfSamples.Clear();WriteSettingsPerf();
    }
    private static void WriteSettingsPerf()=>File.WriteAllText(Path.Combine(directory,"settings-performance.json"),JsonSerializer.Serialize(new{method="Native 250x250 maps, actual +5/-5 appended geology,60 drafted native colonists,30 real item stacks;120warm-up ticks,1200idle/240recovery ticks per phase. First one map, then two maps sharing20extras. Service Stopwatch counter deltas plus native DoSingleTick CPU time; not GPU/frame rate. Fixtures/generation excluded. Raw samples retained.",phases=perfResults,memory=ScaleMemory()},new JsonSerializerOptions{WriteIndented=true}));
}
