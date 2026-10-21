using RimLayers.Navigation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Building_Door navDoor=null!;
    private static Building_Stairs doorStair=null!,doorAlternate=null!;
    private static JobDriver doorDriver=null!;
    private static bool doorChanged,doorAlternateSeen,doorJobKept=true,doorOpenedSeen,doorReturnLandingSeen,doorScreenshot;
    private static int doorPhase;
    private static void BeginDoor()
    {
        destination=At(1,54,54);
        using(var path=map.pathFinder.FindPathNow(pawn.Position,destination,pawn))doorStair=FirstGroundStair(path)!;
        doorAlternate=map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<Building_Stairs>()
            .Single(s=>owner.IsGroundCell(s.Position)&&s!=doorStair);
        navDoor=(Building_Door)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Door,ThingDefOf.Steel),doorStair.UpperCell,map);
        navDoor.SetFaction(Faction.OfPlayer);map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var noClosed=TraverseParms.For(TraverseMode.NoPassClosedDoors);
        Check("closed-landing-door-exists-as-native-portal",!navDoor.Open&&doorStair.UpperCell.GetRegion(map)?.door==navDoor);
        Check("closed-landing-door-respects-no-pass-closed-doors",!owner.Connectors.MovementEdges(noClosed).Any(e=>e.From==doorStair.Position&&e.To==doorStair.UpperCell));
        navDoor.SetForbidden(true);
        Check("forbidden-landing-door-rejects-normal-pawn-route",!owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==doorStair.Position&&e.To==doorStair.UpperCell));
        Check("forbidden-door-still-allows-departure-from-occupied-landing",owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==doorStair.UpperCell&&e.To==doorStair.Position));
        Check("pass-doors-mode-retains-native-door-bypass",owner.Connectors.MovementEdges(TraverseParms.For(TraverseMode.PassDoors)).Any(e=>e.From==doorStair.Position&&e.To==doorStair.UpperCell));
        navDoor.SetForbidden(false);
        expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;
        doorDriver=pawn.jobs.curDriver;stage=260;ticks=0;
    }
    private static void DoorStep()
    {
        if(doorPhase==0&&!doorChanged&&CrossingNow())
        {
            Check("door-test-installed-path-uses-selected-landing",pawn.pather.nextCell==doorStair.UpperCell);
            navDoor.SetFaction(Faction.OfAncientsHostile);
            Check("landing-door-now-rejects-native-non-bashing-traversal",!doorStair.UpperCell.GetRegion(map)!.Allows(TraverseParms.For(pawn),false));
            using(var bash=map.pathFinder.FindPathNow(pawn.Position,destination,TraverseParms.For(pawn,canBashDoors:true),null,PathEndMode.OnCell))
                Check("caller-explicit-bashing-permission-survives-route-metadata",bash.Found&&LayerRouter.HasMetadata(bash,out var data)&&data.TraverseParms.canBashDoors);
            doorChanged=true;
        }
        if(pawn.Position!=destination)doorJobKept&=ExpectedJobMatches()&&pawn.jobs.curDriver==doorDriver;
        if(pawn.Position==doorAlternate.UpperCell)doorAlternateSeen=true;
        if(doorChanged&&!doorJobKept)
        {Check("changed-landing-door-replans-without-bashing-or-losing-job",false,DescribePawn());Finish();return;}
        if(doorPhase==1)
        {
            doorOpenedSeen|=navDoor.Open;doorReturnLandingSeen|=pawn.Position==doorStair.UpperCell;
            if(navDoor.Open&&navDoor.TicksTillFullyOpened==0&&!doorScreenshot)
            {
                RimLayers.Presentation.LayerView.Switch(owner,1);
                Find.CameraDriver.SetRootPosAndSize(navDoor.Position.ToVector3Shifted(),12);
                pendingScreenshot=Path.Combine(directory,"navigation-door-open.png");screenshotHold=3;doorScreenshot=true;
            }
        }
        if(pawn.Position==destination)
        {
            if(doorPhase==0)
            {
                Check("changed-landing-door-uses-legal-alternate-stair",doorChanged&&doorAlternateSeen);
                Check("changed-landing-door-keeps-real-job-and-driver",doorJobKept);
                Check("changed-landing-door-was-not-damaged",navDoor.HitPoints==navDoor.MaxHitPoints);
                navDoor.SetFaction(Faction.OfPlayer);destination=doorStair.Position;
                expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;
                doorDriver=pawn.jobs.curDriver;doorJobKept=true;doorPhase=1;ticks=0;return;
            }
            Check("friendly-closed-door-is-really-opened-and-crossed",doorOpenedSeen&&doorReturnLandingSeen);
            Check("native-door-wait-does-not-replace-normal-goto",doorJobKept);
            stage=11;ticks=0;return;
        }
        if(ticks>4500){Check("changed-landing-door-route-settles",false,DescribePawn());Finish();}
    }
}
