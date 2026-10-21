using System.IO;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Thing integritySource=null!,integrityDestination=null!;
    private static Thing? integrityCarry;
    private static JobDriver integrityDriver=null!;
    private static Pawn integrityCompetitor=null!;
    private static bool integrityMoved,integritySplit;
    private static int integrityTotal,cancelledId;
    private static Job? hungerJob;

    private static void BeginHaulIntegrity()
    {
        foreach(WorkTypeDef type in DefDatabase<WorkTypeDef>.AllDefsListForReading)pawn.workSettings.SetPriority(type,0);
        if(pawn.needs.food!=null)pawn.needs.food.CurLevelPercentage=1;
        if(pawn.needs.rest!=null)pawn.needs.rest.CurLevelPercentage=1;
        integritySource=GenSpawn.Spawn(ThingDefOf.Silver,At(2,62,62),map);integritySource.stackCount=75;
        storageCell=At(2,55,62);
        if(GenCommandLine.CommandLineArgPassed("rimlayersshelf"))
        {
            var shelf=(Building_Storage)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ShelfSmall"),ThingDefOf.WoodLog),storageCell,map);
            shelf.SetFaction(Faction.OfPlayer);shelf.GetStoreSettings().filter.SetDisallowAll();
            shelf.GetStoreSettings().filter.SetAllow(ThingDefOf.Silver,true);shelf.GetStoreSettings().Priority=StoragePriority.Critical;
            Check("ordinary-shelf-keeps-native-storage-and-no-cargo-owner",shelf.GetType()==typeof(Building_Storage)&&RimLayers.Cargo.CargoNetwork.For(map).Ports.Count==0);
        }
        else
        {
        var zone=new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager);map.zoneManager.RegisterZone(zone);zone.AddCell(storageCell);
        zone.GetStoreSettings().filter.SetDisallowAll();zone.GetStoreSettings().filter.SetAllow(ThingDefOf.Silver,true);zone.GetStoreSettings().Priority=StoragePriority.Critical;
        }
        integrityDestination=GenSpawn.Spawn(ThingDefOf.Silver,storageCell,map);integrityDestination.stackCount=5;
        integrityCompetitor=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);
        integrityTotal=SilverCount();StartIntegrityHaul();
        Check("haul-native-reservation-excludes-competing-pawn",!integrityCompetitor.CanReserve(integritySource));
        integrityMoved=false;integritySplit=false;retainedJob=true;stage=130;ticks=0;
    }
    private static int SilverCount()=>map.listerThings.ThingsOfDef(ThingDefOf.Silver).Sum(t=>t.stackCount)+
        (pawn.carryTracker.CarriedThing is {def:var def} c && def==ThingDefOf.Silver?c.stackCount:0);
    private static void StartIntegrityHaul()
    {
        expectedJob=HaulAIUtility.HaulToCellStorageJob(pawn,integritySource,storageCell,true);expectedJob.count=10;
        pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;integrityDriver=pawn.jobs.curDriver;
    }
    private static bool CrossingNow()=>pawn.pather.Moving && owner.TryDecode(pawn.Position,out var here) &&
        owner.TryDecode(pawn.pather.nextCell,out var next) && here.LayerId!=next.LayerId && pawn.pather.nextCellCostLeft<pawn.pather.nextCellCostTotal;
    private static void HaulIntegrityStep()
    {
        if(stage==130)
        {
            if(!integrityMoved && owner.TryDecode(pawn.Position,out var here) && here.LayerId==1)
            {
                Check("source-still-reserved-before-real-move",map.reservationManager.ReservedBy(integritySource,pawn,expectedJob));
                integritySource.Position=At(1,58,58);integrityMoved=true;
            }
            if(!integritySplit && pawn.carryTracker.CarriedThing is { } carry)
            {
                integrityCarry=carry;integritySplit=true;
                Check("native-pickup-splits-exact-requested-count",carry!=integritySource && carry.def==ThingDefOf.Silver && carry.stackCount==10 && integritySource.stackCount==65);
                Check("moving-source-keeps-same-real-target-until-pickup",integrityMoved && integritySource.Spawned && integritySource.Position==At(1,58,58));
                Check("split-preserves-total-stock",SilverCount()==integrityTotal);
            }
            if(integrityDestination.stackCount==15)
            {
                Check("native-delivery-merges-into-existing-stack-once",integritySplit && integrityCarry!.Destroyed && integritySource.stackCount==65 && SilverCount()==integrityTotal);
                Check("moving-source-haul-keeps-one-job-and-driver",retainedJob);
                pawn.jobs.StopAll();StartIntegrityHaul();stage=131;ticks=0;return;
            }
            if(!ExpectedJobMatches() || !ReferenceEquals(integrityDriver,pawn.jobs.curDriver))retainedJob=false;
        }
        else if(stage==131 && CrossingNow())
        {
            cancelledId=expectedJobId;pawn.drafter.Drafted=true;
            Check("drafting-preempts-in-progress-stair-job",pawn.Drafted && pawn.CurJob?.loadID!=cancelledId);
            Check("drafting-releases-native-source-reservation",!map.reservationManager.IsReservedByAnyoneOf(integritySource,Faction.OfPlayer));
            stage=132;ticks=0;
        }
        else if(stage==132 && ticks>80)
        {
            Check("drafted-pawn-does-not-resume-old-job",pawn.CurJob?.loadID!=cancelledId && !pawn.jobs.jobQueue.Any(q=>q.job.loadID==cancelledId) && SilverCount()==integrityTotal);
            pawn.drafter.Drafted=false;StartIntegrityHaul();stage=133;ticks=0;
        }
        else if(stage==133 && CrossingNow())
        {
            cancelledId=expectedJobId;pawn.jobs.StopAll();
            Check("player-cancel-releases-native-source-reservation",!map.reservationManager.IsReservedByAnyoneOf(integritySource,Faction.OfPlayer));
            stage=134;ticks=0;
        }
        else if(stage==134 && ticks>120)
        {
            Check("cancelled-trip-does-not-resurrect",pawn.CurJob?.loadID!=cancelledId && !pawn.jobs.jobQueue.Any(q=>q.job.loadID==cancelledId));
            Check("cancellation-preserves-real-stock-and-completed-delivery",integritySource.stackCount==65 && integrityDestination.stackCount==15 && SilverCount()==integrityTotal);
            LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(storageCell.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"haul-integrity.png");screenshotHold=3;stage=11;ticks=0;
        }
        if(ticks>6500){Check("haul-integrity-stage-completes",false,$"stage={stage}, {DescribePawn()}");Finish();}
    }

    private static void HungerStep()
    {
        if(stage==140 && pawn.carryTracker.CarriedThing!=null && CrossingNow())
        {
            Check("automatic-bill-is-not-player-forced",ExpectedJobMatches() && !pawn.CurJob.playerForced);
            var meal=GenSpawn.Spawn(ThingDefOf.MealSimple,At(1,58,58),map);
            pawn.needs.food.CurLevelPercentage=0;
            pawn.jobs.CheckForJobOverride();hungerJob=pawn.CurJob;
            Check("native-hunger-arbitration-preempts-cross-layer-work",hungerJob?.def==JobDefOf.Ingest && !ExpectedJobMatches(),DescribePawn());
            Check("hunger-interruption-releases-table-reservation",!map.reservationManager.IsReservedByAnyoneOf(expectedJob!.targetA,Faction.OfPlayer));
            pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking"),0);
            stage=141;ticks=0;
        }
        else if(stage==141 && pawn.needs.food.CurLevelPercentage>=0.3f)
        {
            Check("pawn-actually-eats-after-hunger-preemption",true,pawn.needs.food.CurLevelPercentage.ToString());
            Check("interrupted-bill-is-not-silently-completed",bill!.repeatCount==1 && pawn.CurJob?.loadID!=expectedJobId &&
                !pawn.jobs.jobQueue.Any(q=>q.job.loadID==expectedJobId));
            LayerView.Switch(owner,owner.TryDecode(pawn.Position,out var here)?here.LayerId:0);
            Find.CameraDriver.SetRootPosAndSize(pawn.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"hunger.png");screenshotHold=3;stage=11;ticks=0;
        }
        if(ticks>6500){Check("native-hunger-scenario-completes",false,$"stage={stage}, {DescribePawn()}");Finish();}
    }
}
