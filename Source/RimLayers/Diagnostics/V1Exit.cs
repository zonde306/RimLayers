using System.Text.Json;
using HarmonyLib;
using RimLayers.Navigation;
using RimLayers.Structure;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn exitNpc=null!,exitColonist=null!;
    private static Lord exitLord=null!;
    private static JobDriver exitDriver=null!;
    private static Thing exitSilver=null!,exitLoot=null!;
    private static bool exitLootSeen;
    private static bool exitSaved,exitKept=true;
    private static int exitPhase,exitCalls;
    private static Lord? caravanLord;
    private static bool caravanLeaveObserved;
    private static IntVec3 caravanExitCell;
    private static bool caravanSendObserved;
    private static readonly List<string> exitCommits=new();
    private static readonly HashSet<int> exitFloors=new();
    private static void ObserveWorldExit(Pawn __instance,Rot4 exitDir)
    {
        if(__instance!=exitNpc&&__instance!=exitColonist)return;
        exitCalls++;
        bool valid=exitPhase!=1 ? __instance.Spawned&&__instance.Map==map&&owner.IsGroundCell(__instance.Position)&&
            __instance.Position.z==owner.Layout!.Height-1&&exitDir==Rot4.North : caravanSendObserved;
        if(exitPhase==3)exitLootSeen=__instance.carryTracker.CarriedThing==exitLoot&&exitLoot.stackCount==1;
        exitCommits.Add($"{__instance.ThingID}/{__instance.Position}/{exitDir}/valid={valid}");
        Check("native-world-exit-commits-on-logical-north-ground-"+exitPhase,valid,exitCommits[^1]);
    }
    private static void ObserveCaravanSend(IEnumerable<Pawn> pawns)
    {
        if(exitPhase!=1||!pawns.Contains(exitColonist))return;
        caravanSendObserved=exitColonist.Spawned&&exitColonist.Map==map&&owner.IsGroundCell(exitColonist.Position)&&
            exitColonist.Position.InHorDistOf(caravanExitCell,12)&&caravanLord?.CurLordToil is LordToil_PrepareCaravan_Leave;
        Check("native-caravan-send-is-triggered-only-after-ground-arrival",caravanSendObserved,$"position={exitColonist.Position},exit={caravanExitCell}");
    }
    private static Job? NativeExitJob(Pawn p)=>new JobGiver_ExitMapBest().TryIssueJobPackage(p,default).Job;
    private static void BeginWorldExit()
    {
        foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList()){p.jobs.StopAll();p.drafter.Drafted=true;}
        exitColonist=pawn;exitColonist.drafter.Drafted=true;
        // Only the logical north edge is walkable. It is NOT a physical Map edge.
        foreach(var cell in owner.LayerRect(0).EdgeCells)
        {
            foreach(var t in cell.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);
            if(cell.z!=owner.Layout!.Height-1)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),cell,map);
        }
        for(int x=49;x<=64;x++)for(int z=65;z<owner.Layout!.Height;z++)
        {
            var cell=At(0,x,z);
            foreach(var t in cell.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);
        }
        exitNpc=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("AncientSoldier"),Faction.OfAncients);
        GenSpawn.Spawn(exitNpc,At(2,60,62),map);
        exitLord=LordMaker.MakeNewLord(Faction.OfAncients,new LordJob_Travel(At(0,60,100)),map,new[]{exitNpc});
        exitSilver=ThingMaker.MakeThing(ThingDefOf.Silver);exitSilver.stackCount=9;exitNpc.inventory.innerContainer.TryAdd(exitSilver);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("home-map-retains-native-no-exit-grid-policy",!map.exitMapGrid.MapUsesExitGrid);
        Check("upper-pawn-can-reach-logical-world-edge",exitNpc.CanReachMapEdge());
        Check("best-world-exit-target-is-logical-ground-north",RCellFinder.TryFindBestExitSpot(exitNpc,out var best,canBash:false)&&owner.IsGroundCell(best)&&best.z==119,best.ToString());
        Check("random-world-exit-target-is-logical-ground-north",RCellFinder.TryFindRandomExitSpot(exitNpc,out var random)&&owner.IsGroundCell(random)&&random.z==119,random.ToString());
        var nearFacts=new List<string>();
        bool nearCorrect=true;
        for(int attempt=0;attempt<20;attempt++)
        {
            bool found=RCellFinder.TryFindExitSpotNear(exitNpc,At(2,60,118),5,out var near);
            bool correct=found&&owner.IsGroundCell(near)&&near.z==119;nearCorrect&=correct;
            if(!correct)nearFacts.Add($"attempt={attempt},found={found},spot={near}; candidates="+string.Join(";",Enumerable.Range(56,9).Select(x=>$"{x}:{exitNpc.CanReach(At(0,x,119),PathEndMode.OnCell,Danger.Deadly)}")));
        }
        Check("near-world-exit-projects-upper-local-position",nearCorrect,string.Join(" | ",nearFacts));
        for(int dir=0;dir<4;dir++)
        {
            var edge=CellFinder.RandomEdgeCell(new Rot4(dir),map);
            Check("native-directional-edge-finder-uses-ground-"+dir,WorldBoundary.OnEdge(edge,map)&&owner.IsGroundCell(edge));
        }
        var stair=map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<Building_Stairs>().Single(s=>owner.TryDecode(s.Position,out var c)&&c.LayerId==1);
        var position=stair.Position;stair.DeSpawn();
        Check("isolated-upper-floor-cannot-reach-world-edge",!exitNpc.CanReachMapEdge());
        Check("native-exit-giver-does-not-invent-isolated-route",NativeExitJob(exitNpc)==null);
        GenSpawn.Spawn(stair,position,map);
        pawn=exitNpc;expectedJob=NativeExitJob(pawn);
        Check("native-exit-giver-creates-real-goto-job",expectedJob?.def==JobDefOf.Goto&&expectedJob.exitMapOnArrival&&WorldBoundary.OnEdge(expectedJob.targetA.Cell,map));
        if(expectedJob==null){Finish();return;}
        pawn.jobs.StartJob(expectedJob,JobCondition.InterruptForced);expectedJobId=expectedJob.loadID;exitDriver=pawn.jobs.curDriver;
        exitFloors.Add(2);stage=270;ticks=0;
    }
    private static void RestoreWorldExit()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"exit-baseline.json")));
        var b=doc.RootElement;
        exitNpc=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        exitColonist=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("colonist").GetString());
        exitLord=map.lordManager.lords.Single(l=>l.loadID==b.GetProperty("lord").GetInt32());
        exitSilver=exitNpc.inventory.innerContainer.Single(t=>t.ThingID==b.GetProperty("silver").GetString());
        pawn=exitNpc;expectedJob=pawn.CurJob;expectedJobId=b.GetProperty("job").GetInt32();exitDriver=pawn.jobs.curDriver;exitSaved=true;
        Check("restart-preserves-exit-job-target-and-native-lord",expectedJob?.loadID==expectedJobId&&expectedJob.exitMapOnArrival&&
            WorldBoundary.OnEdge(expectedJob.targetA.Cell,map)&&exitNpc.GetLord()==exitLord);
        Check("restart-resumes-real-walking-exit-on-middle-floor",owner.TryDecode(pawn.Position,out var c)&&c.LayerId==1);
        exitFloors.Add(2);stage=270;ticks=0;
    }
    private static void BeginOtherWalkingExit(int phase)
    {
        exitPhase=phase;
        exitNpc=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("AncientSoldier"),Faction.OfAncients);
        GenSpawn.Spawn(exitNpc,At(2,60,62),map);pawn=exitNpc;
        exitLord=LordMaker.MakeNewLord(Faction.OfAncients,new LordJob_Travel(At(0,60,100)),map,new[]{pawn});
        if(phase==2)
        {
            expectedJob=JobMaker.MakeJob(JobDefOf.Flee,At(0,60,119));expectedJob.exitMapOnArrival=true;
        }
        else
        {
            exitLoot=ThingMaker.MakeThing(ThingDefOf.Gold);GenSpawn.Spawn(exitLoot,At(2,59,62),map);
            expectedJob=JobMaker.MakeJob(JobDefOf.Steal,exitLoot,At(0,60,119));expectedJob.count=1;
        }
        pawn.jobs.StartJob(expectedJob,JobCondition.InterruptForced);expectedJobId=expectedJob.loadID;exitDriver=pawn.jobs.curDriver;
        Check("native-walking-exit-driver-installed-"+phase,phase==2?exitDriver is JobDriver_Flee:exitDriver is JobDriver_TakeAndExitMap);
        exitFloors.Clear();exitFloors.Add(2);exitKept=true;ticks=0;
    }
    private static void WorldExitStep()
    {
        if(pawn.Spawned)
        {
            if(exitPhase!=1)exitKept&=ExpectedJobMatches()&&pawn.jobs.curDriver==exitDriver;
            else if(caravanLord?.CurLordToil is LordToil_PrepareCaravan_Leave && pawn.CurJob?.def==JobDefOf.Goto)
            {
                if(!caravanLeaveObserved)
                {
                    expectedJob=pawn.CurJob;expectedJobId=expectedJob.loadID;exitDriver=pawn.jobs.curDriver;caravanLeaveObserved=true;
                    Check("native-caravan-leave-ai-starts-real-cross-layer-goto",owner.TryDecode(pawn.Position,out var from)&&from.LayerId==2);
                }
                if(owner.TryDecode(pawn.Position,out var before)&&before.LayerId>0)exitKept&=ExpectedJobMatches()&&pawn.jobs.curDriver==exitDriver;
            }
            if(owner.TryDecode(pawn.Position,out var c))exitFloors.Add(c.LayerId);
            if(exitPhase==0&&!exitSaved&&owner.TryDecode(pawn.Position,out var mid)&&mid.LayerId==1)
            {
                GameDataSaveLoader.SaveGame("RimLayers_V1Exit");
                File.WriteAllText(Path.Combine(directory,"exit-baseline.json"),JsonSerializer.Serialize(new {pawn=exitNpc.ThingID,colonist=exitColonist.ThingID,lord=exitLord.loadID,silver=exitSilver.ThingID,job=expectedJobId}));
                exitSaved=true;Check("saved-native-world-exit-between-stairs",true);
            }
        }
        else
        {
            Check("native-exit-keeps-same-job-and-driver-through-three-floors-"+exitPhase,exitKept&&exitFloors.SetEquals(new[]{0,1,2}));
            if(exitPhase==0)
            {
                Check("native-npc-exit-commits-once-and-retains-inventory",exitCalls==1&&exitNpc.IsWorldPawn()&&!exitSilver.Destroyed&&exitSilver.stackCount==9&&exitNpc.inventory.innerContainer.Contains(exitSilver));
                Check("native-lord-removes-departed-member-without-violent-loss",!exitLord.ownedPawns.Contains(exitNpc)&&exitLord.numPawnsLostViolently==0);
                Check("world-exit-middle-checkpoint-was-reached",exitSaved);
                pawn=exitColonist;pawn.jobs.StopAll();pawn.drafter.Drafted=false;pawn.Position=At(2,60,62);pawn.Notify_Teleported();
                pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;
                Check("home-ordinary-goto-does-not-bypass-native-caravan-policy",!CaravanExitMapUtility.CanExitMapAndJoinOrCreateCaravanNow(pawn));
                if(!RCellFinder.TryFindBestExitSpot(pawn,out var caravanExit,canBash:false)){Check("caravan-ground-exit-available",false);Finish();return;}
                caravanExitCell=caravanExit;
                CaravanFormingUtility.StartFormingCaravan(new(){pawn},new(),Faction.OfPlayer,new(),pawn.Position,caravanExit,map.Tile,PlanetTile.Invalid);
                caravanLord=pawn.GetLord();
                Check("native-caravan-formation-owns-player-with-ground-exit",caravanLord?.LordJob is LordJob_FormAndSendCaravan form&&WorldBoundary.OnEdge(form.ExitSpot,map));
                exitPhase=1;exitFloors.Clear();exitFloors.Add(2);exitKept=true;ticks=0;return;
            }
            if(exitPhase==1)
            {
            Check("native-colonist-exit-creates-real-world-caravan-once",caravanLeaveObserved&&exitCalls==2&&exitColonist.IsCaravanMember()&&exitColonist.GetCaravan()!=null&&
                exitColonist.GetCaravan().PawnsListForReading.Count(p=>p==exitColonist)==1);
            Check("other-colonists-and-map-remain-active",map.mapPawns.FreeColonistsSpawned.Count>=2&&Find.Maps.Contains(map));
            BeginOtherWalkingExit(2);return;
            }
            Check("native-other-walking-driver-exits-once-"+exitPhase,exitCalls==exitPhase+1&&exitNpc.IsWorldPawn());
            if(exitPhase==2){BeginOtherWalkingExit(3);return;}
            Check("native-steal-carries-the-real-target-through-both-stairs",exitLootSeen);
            Finish();return;
        }
        if(ticks>6500){Check("native-world-exit-settles",false,$"phase={exitPhase},calls={exitCalls},kept={exitKept};"+DescribePawn());Finish();}
    }
}
