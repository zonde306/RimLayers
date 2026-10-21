using System.Text.Json;
using RimLayers.Core;
using RimLayers.Mountains;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Building_Elevator shaftSource=null!;
    private static Mineable shaftRock=null!;
    private static int shaftPhase,shaftHp;
    private static bool shaftRetained;
    private static Dictionary<ThingDef,int> shaftStocks=new();
    private static int ShaftStock(ThingDef def)=>map.listerThings.ThingsOfDef(def).Sum(t=>t.stackCount)+(pawn.carryTracker.CarriedThing?.def==def?pawn.carryTracker.CarriedThing.stackCount:0);
    private static readonly IntVec3 shaftOrigin=new(60,0,60);
    private static ThingDef ShaftDef=>DefDatabase<ThingDef>.GetNamed("RL_LiftVertical");
    private static IntVec3 ShaftAt(int elevation)=>owner.AtElevation(elevation,shaftOrigin.x,shaftOrigin.z);
    private static IEnumerable<Building_Elevator> ShaftStations()=>map.listerThings.ThingsOfDef(ShaftDef).OfType<Building_Elevator>();
    private static void ShaftOrder(IntVec3 goal)
    {destination=goal;expectedJob=JobMaker.MakeJob(JobDefOf.Goto,goal);pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;shaftRetained=true;ticks=0;}
    private static void BeginShaftExpansion()
    {
        Check("vertical-station-starts-on-unexpanded-map",owner==null);
        foreach(var p in map.mapPawns.FreeColonistsSpawned){p.jobs.StopAll();p.drafter.Drafted=true;p.Position=new(30,0,30);p.Notify_Teleported();}
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Mining"))&&!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Construction"))&&!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Hauling")));
        pawn.skills.GetSkill(SkillDefOf.Mining).Level=20;pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        foreach(var c in new CellRect(45,45,32,32))
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Soil);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        shaftSource=(Building_Elevator)ThingMaker.MakeThing(ShaftDef);shaftSource.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(shaftSource,shaftOrigin,map);
        Check("building-station-alone-does-not-create-floors",LayerMapComponent.For(map)==null);
        Check("upward-extension-requires-real-roof",!shaftSource.CanExpandShaft(1).Accepted);
        var biome=map.TileInfo.PrimaryBiome;
        foreach(string name in new[]{"IceSheet","SeaIce"})
        {map.TileInfo.PrimaryBiome=DefDatabase<BiomeDef>.GetNamed(name);Check(name+"-blocks-vertical-lift-dig-action",!shaftSource.CanExpandShaft(-1).Accepted&&!shaftSource.StartExtension(-1)&&LayerMapComponent.For(map)==null);}
        map.TileInfo.PrimaryBiome=biome;
        map.terrainGrid.SetTerrain(shaftOrigin,TerrainDefOf.WaterShallow);
        Check("water-cannot-start-vertical-shaft",!shaftSource.CanExpandShaft(-1).Accepted);map.terrainGrid.SetTerrain(shaftOrigin,TerrainDefOf.Soil);
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),shaftOrigin+new IntVec3(2,0,0),map);
        foreach(var c in new CellRect(58,58,5,5))map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
        RimLayersMod.Settings.Caves=false;
        var down=shaftSource.GetGizmos().OfType<Command_Action>().Single(g=>g.defaultLabel=="RL_ExpandShaftDown".Translate().ToString());down.action();
        owner=LayerMapComponent.For(map)!;
        Check("down-action-generates-B1-and-preserves-station",owner.Layout!.LayerCount==2&&shaftSource.Position==shaftOrigin&&shaftSource.Expanding(-1));
        shaftRock=(Mineable)ShaftAt(-1).GetEdifice(map);shaftHp=shaftRock.HitPoints;
        var up=shaftSource.GetGizmos().OfType<Command_Action>().Single(g=>g.defaultLabel=="RL_ExpandShaftUp".Translate().ToString());up.action();
        Check("same-ground-station-requests-both-directions",shaftSource.Expanding(1)&&shaftSource.Expanding(-1)&&owner.Layout!.Elevations.SequenceEqual(new[]{0,-1,1}));
        Check("upper-exit-gets-native-costed-blueprint-not-free-station",ShaftAt(1).GetThingList(map).OfType<Blueprint_Build>().Any(b=>b.def.entityDefToBuild==ShaftDef)&&ShaftStations().Count()==1&&ShaftDef.costList.Sum(c=>c.count)==194);
        Check("uncleared-underground-rock-blocks-shaft-route",!owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.To==ShaftAt(-1)));
        pawn.drafter.Drafted=false;pawn.Position=shaftOrigin+IntVec3.West;pawn.Notify_Teleported();
        foreach(var w in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(w,0);
        pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;pawn.needs.joy.CurLevelPercentage=1;
        var job=UndergroundMiner.JobOnThing(pawn,shaftSource,true);Check("vertical-station-offers-real-mining-job",job?.targetA.Thing==shaftRock);
        pawn.jobs.TryTakeOrderedJob(job);shaftPhase=0;stage=490;ticks=0;
    }
    private static void ShaftExpansionStep()
    {
        if(shaftPhase==0)
        {
            if(shaftRock.HitPoints==shaftHp){if(ticks>6000)throw new Exception("Shaft mining did not start: "+DescribePawn());return;}
            Check("station-miner-damages-real-rock-from-ground",shaftRock.HitPoints>0&&shaftRock.HitPoints<shaftHp&&owner.IsGroundCell(pawn.Position));
            File.WriteAllText(Path.Combine(directory,"shaft-baseline.json"),JsonSerializer.Serialize(new{source=shaftSource.ThingID,pawn=pawn.ThingID,rock=shaftRock.ThingID,hp=shaftRock.HitPoints,job=pawn.CurJob!.loadID}));
            GameDataSaveLoader.SaveGame("RimLayers_V1ShaftExpansion");
            LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.Selector.Select(shaftSource);CameraJumper.TryJump(shaftSource,CameraJumper.MovementMode.Cut);
            pendingScreenshot=Path.Combine(directory,"ground-dual-extension.png");screenshotHold=3;stage=11;ticks=0;return;
        }
        if(shaftPhase==1)
        {
            shaftSource.TickExtensions();
            if(!ShaftAt(-1).GetThingList(map).OfType<Blueprint_Build>().Any())
            {if(ticks>12000)throw new Exception("Shaft/working-cell mining failed after reload: "+DescribePawn());return;}
            pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Mining"),0);pawn.jobs.StopAll();
            Check("cleared-B1-receives-normal-station-blueprint",ShaftAt(-1).GetThingList(map).OfType<Blueprint_Build>().Any());
            Check("construction-access-is-bidirectional-and-emergency-only",new[]{-1,1}.All(d=>owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.ConstructionAccess&&e.From==shaftOrigin&&e.To==ShaftAt(d)&&e.Ticks==SettingsMath.TravelTicks(210,RimLayersMod.Settings.EmergencyLiftTime))));
            foreach(var cost in ShaftDef.costList)
            {
                int remaining=cost.count*2;
                while(remaining>0){var t=ThingMaker.MakeThing(cost.thingDef);t.stackCount=Math.Min(t.def.stackLimit,remaining);remaining-=t.stackCount;GenPlace.TryPlaceThing(t,shaftOrigin+new IntVec3(-2,0,-2),map,ThingPlaceMode.Near);}
            }
            shaftStocks=ShaftDef.costList.ToDictionary(c=>c.thingDef,c=>ShaftStock(c.thingDef));
            pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Construction"),1);pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Hauling"),2);
            shaftPhase=2;ticks=0;return;
        }
        if(shaftPhase==2)
        {
            if(ticks==2)
            {
                var deliver=(WorkGiver_ConstructDeliverResourcesToBlueprints)DefDatabase<WorkGiverDef>.GetNamed("ConstructDeliverResourcesToBlueprints").Worker;
                var probes=map.listerThings.AllThings.OfType<Blueprint_Build>().Where(b=>b.def.entityDefToBuild==ShaftDef).Select(b=>new {id=b.ThingID,position=b.Position.ToString(),touch=GenConstruct.CanTouchTargetFromValidCell(b,pawn),reach=pawn.CanReach(b,PathEndMode.Touch,Danger.Deadly),construct=GenConstruct.CanConstruct(b,pawn),job=deliver.JobOnThing(pawn,b)?.ToString(),reason=JobFailReason.Reason}).ToArray();
                File.WriteAllText(Path.Combine(directory,"shaft-construction-probes.json"),JsonSerializer.Serialize(probes));
            }
            if(ShaftStations().Count()!=3){if(ticks>16000)throw new Exception("Native hauling/construction did not finish both stations: "+DescribePawn()+"; plans="+string.Join(",",map.listerThings.AllThings.Where(t=>t.def.entityDefToBuild==ShaftDef).Select(t=>$"{t}:{t.Position}")));return;}
            shaftSource.TickExtensions();
            Check("two-stations-consume-exact-native-material-costs",ShaftDef.costList.All(c=>ShaftStock(c.thingDef)==shaftStocks[c.thingDef]-2*c.count));
            Check("native-hauling-and-construction-complete-both-costed-stations",new[]{-1,0,1}.All(e=>ShaftAt(e).GetEdifice(map) is Building_Elevator)&&!shaftSource.Expanding(1)&&!shaftSource.Expanding(-1));
            Check("finished-shaft-removes-temporary-construction-edges",!owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.ConstructionAccess));
            Check("one-ground-stop-has-upward-and-downward-routes",new[]{-1,1}.All(e=>owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(edge=>edge.From==shaftOrigin&&edge.To==ShaftAt(e))));
            pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Construction"),0);pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Hauling"),0);
            shaftSource.StopEnabled=false;owner.Connectors.Invalidate();
            var edges=owner.Connectors.MovementEdges(TraverseParms.For(pawn));
            Check("disabled-ground-stop-still-allows-B1-to-upper-through-shaft",edges.Any(e=>e.From==ShaftAt(-1)&&e.To==ShaftAt(1))&&!edges.Any(e=>e.From==shaftOrigin||e.To==shaftOrigin));
            shaftSource.StopEnabled=true;owner.Connectors.Invalidate();
            ShaftOrder(ShaftAt(1));shaftPhase=3;return;
        }
        if(shaftPhase is 3 or 4 or 5)
        {
            shaftRetained&=ExpectedJobMatches()||pawn.Position==destination;
            if(pawn.Position!=destination){if(ticks>6000)throw new Exception("Bidirectional lift journey failed: "+DescribePawn());return;}
            Check(shaftPhase==3?"same-job-travels-ground-to-upper":shaftPhase==4?"same-job-travels-upper-through-ground-to-B1":"same-job-returns-B1-to-ground",shaftRetained);
            if(shaftPhase==3){ShaftOrder(ShaftAt(-1));shaftPhase=4;return;}
            if(shaftPhase==4){ShaftOrder(shaftOrigin);shaftPhase=5;return;}
            var upper=(Building_Elevator)ShaftAt(1).GetEdifice(map);upper.DeSpawn();
            Check("existing-floor-can-reopen-construction-without-generating-another-layer",shaftSource.StartExtension(1));
            var bp=ShaftAt(1).GetThingList(map).OfType<Blueprint_Build>().Single();bp.Destroy(DestroyMode.Cancel);
            var partial=(Frame)ThingMaker.MakeThing(ShaftDef.frameDef);partial.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(partial,ShaftAt(1),map);
            var material=ThingMaker.MakeThing(ThingDefOf.Steel);material.stackCount=17;partial.resourceContainer.TryAdd(material);
            int before=ShaftStock(ThingDefOf.Steel);shaftSource.CancelExtension(1);shaftSource.TickExtensions();shaftSource.TickExtensions();
            Check("cancel-refunds-only-real-frame-materials-once",partial.Destroyed&&ShaftStock(ThingDefOf.Steel)==before+17&&!shaftSource.Expanding(1)&&!ShaftAt(1).GetThingList(map).Any(t=>t is Blueprint||t is Frame));
            Check("cancel-keeps-floor-and-other-direction-route",owner.Structure.HasSurface(ShaftAt(1))&&owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==shaftOrigin&&e.To==ShaftAt(-1)));
            GenSpawn.Spawn(upper,ShaftAt(1),map);
            var lower=(Building_Elevator)ShaftAt(-1).GetEdifice(map);
            Check("B1-station-can-start-B2-extension",lower.StartExtension(-1)&&owner.Layout!.TryGetLayerAtElevation(-2,out _));
            var rock=VerticalMining.Rock(lower);int hp=rock?.HitPoints??0;lower.CancelExtension(-1);
            Check("cancel-stops-B2-mining-without-deleting-rock",!lower.Expanding(-1)&&VerticalMining.Rock(lower)==null&&rock!=null&&rock.Spawned&&rock.HitPoints==hp);
            shaftSource.DeSpawn();
            Check("missing-ground-shaft-segment-breaks-direct-route",!owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==ShaftAt(-1)&&e.To==ShaftAt(1)));
            GenSpawn.Spawn(shaftSource,shaftOrigin,map);
            GameDataSaveLoader.SaveGame("RimLayers_V1ShaftCompleted");
            LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.Selector.Select(shaftSource);CameraJumper.TryJump(shaftSource,CameraJumper.MovementMode.Cut);
            pendingScreenshot=Path.Combine(directory,"ground-completed-dual-shaft.png");screenshotHold=3;stage=11;ticks=0;return;
        }
    }
    private static void RestoreShaftExpansion()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"shaft-baseline.json")));var b=doc.RootElement;
        pawn=map.mapPawns.FreeColonistsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        shaftSource=ShaftStations().Single(s=>s.ThingID==b.GetProperty("source").GetString());
        if(GenCommandLine.CommandLineArgPassed("rimlayersshaftcompletedreload"))
        {
            Check("completed-reload-keeps-three-native-stations-and-mixed-elevations",ShaftStations().Count()==3&&owner.Layout!.Elevations.SequenceEqual(new[]{0,-1,1,-2}));
            Check("completed-reload-has-no-stale-intent-or-construction-edge",ShaftStations().All(s=>!s.Expanding(1)&&!s.Expanding(-1))&&!owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.ConstructionAccess));
            Check("completed-reload-ground-station-still-serves-both-directions",new[]{-1,1}.All(e=>owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(edge=>edge.From==shaftOrigin&&edge.To==ShaftAt(e))));
            ShaftOrder(ShaftAt(1));shaftPhase=3;stage=490;return;
        }
        shaftRock=map.listerThings.AllThings.OfType<Mineable>().Single(s=>s.ThingID==b.GetProperty("rock").GetString());
        Check("reload-preserves-both-independent-shaft-requests",shaftSource.Expanding(1)&&shaftSource.Expanding(-1)&&owner.Layout!.Elevations.SequenceEqual(new[]{0,-1,1}));
        Check("reload-preserves-real-rock-progress-and-native-job",shaftRock.HitPoints==b.GetProperty("hp").GetInt32()&&pawn.CurJob?.loadID==b.GetProperty("job").GetInt32());
        pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Mining"),1);
        shaftPhase=1;stage=490;ticks=0;
    }
}
