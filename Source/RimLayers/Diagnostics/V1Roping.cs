using System.Text.Json;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly List<Pawn> ropeAnimals=new();
    private static Thing ropePenThing=null!;
    private static CompAnimalPenMarker RopePen=>ropePenThing.TryGetComp<CompAnimalPenMarker>();
    private static JobDriver ropeDriver=null!;
    private static bool ropeRetained=true,ropeSaved,ropesBothSeen,ropeFollowSeen,ropeSplitSeen;
    private static int ropeDeliveredAt=-1,ropeRecoveryCase,ropeRecoveryTriggeredAt;
    private static int interruptedRopeJobId;
    private static readonly HashSet<int> roperLayers=new(),animalLayers=new();
    private static void BeginRoping()
    {
        pawn=map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Handling")))??pawn;
        pawn.jobs.StopAll();pawn.drafter.Drafted=false;pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        if(pawn.needs.food!=null)pawn.needs.food.CurLevelPercentage=1;
        if(pawn.needs.rest!=null)pawn.needs.rest.CurLevelPercentage=1;
        pawn.needs.mood.CurLevel=1;
        foreach(var c in new CellRect(56,56,9,9).EdgeCells)
        {
            foreach(Thing t in At(2,c.x,c.z).GetThingList(map).ToList())
                if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(2,c.x,c.z),map);
        }
        ropePenThing=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("PenMarker"),ThingDefOf.WoodLog);
        GenSpawn.Spawn(ropePenThing,At(2,61,61),map);ropePenThing.SetFaction(Faction.OfPlayer);
        RopePen.AnimalFilter.SetAllow(DefDatabase<ThingDef>.GetNamed("Muffalo"),true);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("third-floor-native-pen-is-enclosed",RopePen.PenState.Enclosed);
        // A distant valid ground pen is geometrically "nearer" in raw storage.
        // It must lose to the shorter real route through both stairs.
        foreach(var c in new CellRect(91,91,7,7))
        {
            foreach(var t in At(0,c.x,c.z).GetThingList(map).ToList())
                if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(At(0,c.x,c.z),TerrainDefOf.Concrete);
        }
        foreach(var c in new CellRect(91,91,7,7).EdgeCells)
            GenSpawn.Spawn(ThingMaker.MakeThing(c==new IntVec3(91,0,94)?ThingDefOf.Door:ThingDefOf.Wall,ThingDefOf.Steel),At(0,c.x,c.z),map);
        Thing distantPen=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("PenMarker"),ThingDefOf.WoodLog);
        GenSpawn.Spawn(distantPen,At(0,94,94),map);distantPen.SetFaction(Faction.OfPlayer);
        distantPen.TryGetComp<CompAnimalPenMarker>().AnimalFilter.SetAllow(DefDatabase<ThingDef>.GetNamed("Muffalo"),true);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        ropeAnimals.Clear();
        for(int i=0;i<2;i++)
        {
            Pawn animal=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Muffalo"),Faction.OfPlayer);
            GenSpawn.Spawn(animal,At(0,54+i,56),map);ropeAnimals.Add(animal);
        }
        Check("pen-inspection-recognizes-real-stair-access",RopePen.PenState.HasOutsideAccess);
        Check("unroped-roamers-cannot-escape-pen-through-stairs",ropeAnimals.All(p=>p.FenceBlocked&&!p.CanReach(ropePenThing,PathEndMode.Touch,Danger.Deadly)));
        var prospective=TraverseParms.For(TraverseMode.PassDoors).WithFenceblockedOf(ropeAnimals[0]);
        using(var upperPath=map.pathFinder.FindPathNow(ropeAnimals[0].Position,ropePenThing,prospective,null,PathEndMode.Touch))
        using(var groundPath=map.pathFinder.FindPathNow(ropeAnimals[0].Position,distantPen,prospective,null,PathEndMode.Touch))
            Check("pen-ranking-fixture-has-two-real-routes-and-closer-upper-pen",upperPath.Found&&groundPath.Found&&upperPath.TotalCost<groundPath.TotalCost,
                $"upper={upperPath.TotalCost},ground={groundPath.TotalCost}");
        Check("native-closest-pen-uses-real-led-route-not-storage-distance",AnimalPenUtility.ClosestSuitablePen(ropeAnimals[0],false)==RopePen);
        var giver=(WorkGiver_TakeToPen)DefDatabase<WorkGiverDef>.GetNamed("TakeToPen").Worker;
        expectedJob=giver.JobOnThing(pawn,ropeAnimals[0]);
        Check("native-work-giver-selects-actual-third-floor-pen",expectedJob?.def==JobDefOf.RopeToPen&&expectedJob.targetC.Thing==ropePenThing);
        if(expectedJob==null){Finish();return;}
        pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;ropeDriver=pawn.jobs.curDriver;
        Check("real-native-rope-driver-started",pawn.CurJob==expectedJob&&ropeDriver is JobDriver_RopeToDestination);
        stage=220;ticks=0;
    }
    private static void RestoreRoping()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"roping-baseline.json")));
        var b=baseline.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("roper").GetString());
        ropePenThing=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("pen").GetString());
        ropeAnimals.Clear();foreach(var id in b.GetProperty("animals").EnumerateArray())ropeAnimals.Add(map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==id.GetString()));
        expectedJob=pawn.CurJob;expectedJobId=b.GetProperty("job").GetInt32();ropeDriver=pawn.jobs.curDriver;
        Check("restart-keeps-native-roper-job-and-final-pen",expectedJob?.loadID==expectedJobId&&expectedJob.targetC.Thing==ropePenThing&&ropeDriver is JobDriver_RopeToDestination);
        Check("restart-keeps-both-native-rope-references",pawn.roping.Ropees.Count==2&&ropeAnimals.All(p=>p.roping.RopedByPawn==pawn));
        Check("restart-keeps-roper-in-middle-floor",owner.TryDecode(pawn.Position,out var c)&&c.LayerId==1);
        ropesBothSeen=true;ropeSaved=true;stage=220;ticks=0;
    }
    private static bool AnimalsInPen()=>ropeAnimals.All(p=>p.Spawned&&RopePen.PenState.ContainsConnectedRegion(p.GetRegion()));
    private static void RopingStep()
    {
        owner.TryDecode(pawn.Position,out var roperCell);roperLayers.Add(roperCell.LayerId);
        foreach(Pawn animal in ropeAnimals)
        {
            owner.TryDecode(animal.Position,out var c);animalLayers.Add(c.LayerId);
            ropeFollowSeen|=animal.jobs.curDriver is JobDriver_FollowRoper;
            ropeSplitSeen|=c.LayerId!=roperCell.LayerId&&animal.roping.RopedByPawn==pawn;
        }
        ropesBothSeen|=pawn.roping.Ropees.Count==2&&ropeAnimals.All(p=>p.roping.RopedByPawn==pawn);
        bool arrived=AnimalsInPen();
        if(ropeDeliveredAt<0&&!arrived&&(pawn.CurJob!=expectedJob||pawn.jobs.curDriver!=ropeDriver))ropeRetained=false;
        if(!ropeSaved&&roperCell.LayerId==1&&pawn.roping.Ropees.Count==2)
        {
            GameDataSaveLoader.SaveGame("RimLayers_V1Roping");
            File.WriteAllText(Path.Combine(directory,"roping-baseline.json"),JsonSerializer.Serialize(new {roper=pawn.ThingID,pen=ropePenThing.ThingID,animals=ropeAnimals.Select(p=>p.ThingID).ToArray(),job=expectedJobId}));
            ropeSaved=true;Check("saved-paired-native-ropes-on-middle-floor",true);
        }
        if(arrived&&pawn.roping.Ropees.Count==0&&ropeDeliveredAt<0)ropeDeliveredAt=ticks;
        // Native ProcessRopeesThatHaveArrived drops references in a pre-tick action;
        // Job cleanup releases reservations on its subsequent end, not that same instant.
        if(ropeDeliveredAt>=0&&(pawn.CurJob!=expectedJob||ticks-ropeDeliveredAt>120))
        {
            Check("both-real-animals-enter-third-floor-native-pen",ropeAnimals.All(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId==2));
            Check("native-job-and-driver-survive-until-delivery",ropeRetained);
            Check("both-animals-were-really-attached",ropesBothSeen);
            Check("animals-use-native-follow-roper-driver",ropeFollowSeen);
            Check("rope-survives-real-cross-floor-separation",ropeSplitSeen);
            Check("actual-route-visits-middle-and-top-floor",roperLayers.Contains(1)&&animalLayers.Contains(1)&&animalLayers.Contains(2));
            Check("native-delivery-releases-ropes-and-animal-reservations",pawn.CurJob!=expectedJob&&ropeAnimals.All(p=>!p.roping.IsRoped&&!map.reservationManager.IsReservedByAnyoneOf(p,Faction.OfPlayer)),$"cleanupDelay={ticks-ropeDeliveredAt},job={pawn.CurJob}");
            Check("native-rope-save-checkpoint-was-reached",ropeSaved);
            Check("delivered-roamers-stay-contained-without-rope",ropeAnimals.All(p=>p.FenceBlocked&&!p.CanReach(At(0,54,54),PathEndMode.OnCell,Danger.Deadly)));
            LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.Selector.Select(ropePenThing);
            Find.CameraDriver.SetRootPosAndSize(At(2,60,60).ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,mode+"-roping.png");screenshotHold=3;
            stage=222;ticks=0;
        }
        else if(ticks>4500)
        {
            Check("native-rope-party-delivers-both-animals",false,$"roper={pawn.Position},job={pawn.CurJob},toil={pawn.jobs.curDriver?.CurToilIndex},ropes={pawn.roping.Ropees.Count},animals="+
                string.Join(";",ropeAnimals.Select(p=>$"{p.Position}/{p.CurJob}/{p.pather.nextCell}/roped={p.roping.IsRoped}")));
            Finish();
        }
    }
    private static void BeginRopeRecovery(int which)
    {
        ropeRecoveryCase=which;ropeRecoveryTriggeredAt=-1;
        pawn.jobs.StopAll();pawn.drafter.Drafted=false;
        pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        for(int i=0;i<ropeAnimals.Count;i++)
        {
            var animal=ropeAnimals[i];animal.jobs.StopAll();
            animal.Position=At(0,54+i,56);animal.Notify_Teleported();
        }
        expectedJob=JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("RopeToPen"),ropeAnimals[0],At(2,62,62),ropePenThing);
        pawn.jobs.TryTakeOrderedJob(expectedJob);interruptedRopeJobId=expectedJob.loadID;
        stage=221;ticks=0;
    }
    private static void RopeRecoveryStep()
    {
        if(ropeRecoveryTriggeredAt<0)
        {
            owner.TryDecode(pawn.Position,out var leader);
            bool split=pawn.roping.Ropees.Count==2&&ropeAnimals.Any(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId!=leader.LayerId);
            if(split&&leader.LayerId==(ropeRecoveryCase==2?2:1))
            {
                Check($"roping-recovery-{ropeRecoveryCase}-starts-with-real-split-party",true);
                if(ropeRecoveryCase==0)pawn.drafter.Drafted=true;
                else if(ropeRecoveryCase==1)
                {
                    pawn.jobs.StopAll();
                    Check("cancel-releases-real-ropes-and-reservations",pawn.roping.Ropees.Count==0&&
                        ropeAnimals.All(p=>!p.roping.IsRoped&&!map.reservationManager.IsReservedByAnyoneOf(p,Faction.OfPlayer)));
                }
                else
                {
                    var stair=map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<RimLayers.Structure.Building_Stairs>()
                        .Single(s=>owner.TryDecode(s.Position,out var c)&&c.LayerId==1);
                    stair.Destroy(DestroyMode.Vanish);
                    Check("pen-inspection-loses-stair-access-after-disconnection",!RopePen.PenState.HasOutsideAccess);
                    Check("removing-upper-stair-really-separates-roper-and-ropee",ropeAnimals.Any(p=>!p.CanReach(pawn,PathEndMode.Touch,Danger.Deadly)));
                }
                ropeRecoveryTriggeredAt=ticks;
            }
        }
        else if(ticks-ropeRecoveryTriggeredAt>=180)
        {
            string label=ropeRecoveryCase==0?"draft":ropeRecoveryCase==1?"cancel":"disconnect";
            // After cancellation the normal work tree may legally issue a NEW rope job.
            // Its reservations are not a leak from the cancelled job.
            if(ropeRecoveryCase!=1)Check(label+"-releases-real-ropes-and-reservations",pawn.roping.Ropees.Count==0&&
                ropeAnimals.All(p=>!p.roping.IsRoped&&!map.reservationManager.IsReservedByAnyoneOf(p,Faction.OfPlayer)));
            Check(label+"-does-not-resurrect-interrupted-rope-job",pawn.CurJob?.loadID!=interruptedRopeJobId);
            if(ropeRecoveryCase<2)BeginRopeRecovery(ropeRecoveryCase+1);
            else {stage=11;ticks=0;}
            return;
        }
        if(ticks>4500)
        {
            Check("roping-recovery-reaches-and-settles-native-boundary",false,$"case={ropeRecoveryCase},trigger={ropeRecoveryTriggeredAt},roper={pawn.Position}/{pawn.CurJob},animals="+
                string.Join(";",ropeAnimals.Select(p=>$"{p.Position}/{p.CurJob}/roped={p.roping.IsRoped}")));
            Finish();
        }
    }

}
