using System.Text.Json;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static Building_Elevator liftSingle=null!;
    private static Building_Elevator[] liftShaft=null!;
    private static int liftPhase;
    private static Job liftJob=null!;
    private static bool liftRetained=true;
    private static Thing liftReturnBlocker=null!;
    private static int ExpectedLiftCost(int baseline,bool powered)=>SettingsMath.TravelTicks(baseline,powered?RimLayersMod.Settings.PoweredLiftTime:RimLayersMod.Settings.EmergencyLiftTime);
    private static void BeginElevators()
    {
        if(GenCommandLine.CommandLineArgPassed("rimlayerstravelmultipliers"))
        {
            RimLayersMod.Settings.StairTime=2;RimLayersMod.Settings.PoweredLiftTime=.5f;RimLayersMod.Settings.EmergencyLiftTime=3;
            SettingsMenu.SettingsChanged();LoadedModManager.GetMod<RimLayersMod>().WriteSettings();
        }
        liftSingle=(Building_Elevator)UtilityBuilding("RL_LiftSingle",0,54,52);
        liftShaft=Enumerable.Range(0,3).Select(l=>(Building_Elevator)UtilityBuilding("RL_LiftVertical",l,58,58)).ToArray();
        liftShaft[1].StopEnabled=false;
        for(int layer=0;layer<3;layer++)UtilityBuilding("WoodFiredGenerator",layer,59,54).GetComp<CompRefuelable>().Refuel(50);
        foreach(var c in new[]{new IntVec3(68,0,51),new IntVec3(68,0,57),new IntVec3(68,0,63)})GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),c,map);
        foreach(var c in new CellRect(65,50,6,15)){map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);map.fogGrid.Unfog(c);}
        map.powerNetManager.UpdatePowerNetsAndConnections_First();pawn.drafter.Drafted=true;
        liftPhase=0;stage=360;ticks=0;
    }
    private static bool RouteUses(IntVec3 start,IntVec3 goal,Building_Stairs connector)
    {using var p=map.pathFinder.FindPathNow(start,goal,pawn);return p.Found&&LayerRouter.HasMetadata(p,out var tag)&&tag.Steps.Values.Any(e=>e.Stairs==connector);}
    private static void ElevatorStep()
    {
        if(liftPhase==0)
        {
            if(ticks<100||!liftSingle.Powered||liftShaft.Any(s=>!s.Powered)){if(ticks>2000)throw new Exception("Native generators did not power lifts.");return;}
            var edges=owner.Connectors.MovementEdges(TraverseParms.For(pawn));
            Check("stair-route-has-current-independent-multiplier",edges.Any(e=>e.Kind==ConnectorKind.Stairs&&e.Ticks==SettingsMath.TravelTicks(90,RimLayersMod.Settings.StairTime)));
            Check("single-lift-uses-native-power-and-scaled-edge",edges.Any(e=>e.Stairs==liftSingle&&e.Ticks==ExpectedLiftCost(25,true)));
            Check("three-floor-shaft-scales-route-and-skips-disabled-stop",edges.Any(e=>e.From==liftShaft[0].Position&&e.To==liftShaft[2].Position&&e.Ticks==ExpectedLiftCost(120,true))&&!edges.Any(e=>e.From==liftShaft[1].Position||e.To==liftShaft[1].Position));
            Check("shorter-total-route-selects-fast-lift-not-nearest-stair",RouteUses(At(0,54,51),At(1,54,53),liftSingle));
            liftSingle.GetComp<CompPowerTrader>().PowerOn=false;
            Check("outage-uses-independent-emergency-cost-and-can-prefer-stairs",owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.Stairs==liftSingle&&e.Ticks==ExpectedLiftCost(180,false))&&!RouteUses(At(0,54,51),At(1,54,53),liftSingle));
            liftSingle.GetComp<CompPowerTrader>().PowerOn=true;
            // Roof-only obstruction, independently of any upper structural surface.
            var hole=At(1,54,55);map.terrainGrid.SetTerrain(hole,RimLayersDefOf.RL_OpenAir);LayerView.Switch(owner,1);
            Check("roof-hides-lower-actor-and-target-without-upper-floor",!LowerFloorBackdrop.Exposed(owner,At(0,54,55)));
            map.roofGrid.SetRoof(At(0,54,55),null);
            Check("removing-roof-reveals-open-column",LowerFloorBackdrop.Exposed(owner,At(0,54,55)));
            map.roofGrid.SetRoof(At(0,54,55),RoofDefOf.RoofConstructed);
            Check("elevators-do-not-create-air-or-power-riser-links",!owner.Connectors.HasTransferEdge(liftSingle.Position,liftSingle.UpperCell,RimLayers.Core.LayerChangeKind.Air)&&liftSingle.TryGetComp<RimLayers.Environment.CompPowerRiser>()==null);
            CheckFacilities();
            pawn.Position=liftSingle.Position;pawn.Notify_Teleported();destination=liftSingle.UpperCell;
            liftJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(liftJob);
            liftPhase=1;ticks=0;return;
        }
        if(liftPhase==1)
        {
            if(pawn.Position==destination)
            {Check("native-pawn-completes-powered-single-lift-with-same-job",liftRetained);pawn.Position=liftShaft[0].Position;pawn.Notify_Teleported();destination=liftShaft[2].Position;liftJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(liftJob);liftPhase=2;ticks=0;return;}
            liftRetained&=pawn.CurJob==liftJob;
            if(ticks>1000)throw new Exception("Single lift did not complete Goto.");
        }
        if(liftPhase==2)
        {
            if(pawn.pather.nextCell==destination&&pawn.pather.nextCellCostTotal==ExpectedLiftCost(120,true))
            {
                Check("vertical-lift-execution-uses-same-scaled-cost-as-router",true);
                liftShaft[1].GetComp<CompFlickable>().DoFlick();liftShaft[1].GetComp<CompPowerTrader>().PowerOn=false;
                var old=owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Single(e=>e.From==liftShaft[0].Position&&e.To==destination);
                Check("middle-shaft-outage-keeps-scaled-emergency-route",old.Ticks==ExpectedLiftCost(330,false));
                liftShaft[1].DeSpawn();
                Check("missing-middle-shaft-removes-direct-edge",!owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==liftShaft[0].Position&&e.To==destination));
                liftPhase=3;ticks=0;return;
            }
            if(ticks>1000)throw new Exception("Vertical direct step not selected.");
        }
        if(liftPhase==3)
        {
            if(pawn.Position!=destination){liftRetained&=pawn.CurJob==liftJob;if(ticks>3000)throw new Exception("Shaft removal did not replan.");return;}
            Check("shaft-removal-replans-same-job-to-original-target",liftRetained);
            GenSpawn.Spawn(liftShaft[1],At(1,58,58),map);liftShaft[1].StopEnabled=false;
            var returnCell=At(1,10,10);foreach(var t in At(0,10,10).GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,10,10),map);map.roofGrid.SetRoof(At(0,10,10),RoofDefOf.RoofConstructed);map.terrainGrid.SetTerrain(returnCell,RimLayersDefOf.RL_StructuralFloor);
            liftReturnBlocker=ThingMaker.MakeThing(ThingDefOf.Steel);liftReturnBlocker.stackCount=ThingDefOf.Steel.stackLimit;GenSpawn.Spawn(liftReturnBlocker,returnCell,map);
            var refund=ThingMaker.MakeThing(ThingDefOf.Steel);refund.stackCount=4;owner.Returns.Place(refund,returnCell);
            Check("full-platform-keeps-generated-refund-pending-without-wiping-original-stack",owner.Returns.PendingCount==1&&liftReturnBlocker.Spawned&&liftReturnBlocker.stackCount==ThingDefOf.Steel.stackLimit);
            File.WriteAllText(Path.Combine(directory,"elevators-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,single=liftSingle.ThingID,shaft=liftShaft.Select(s=>s.ThingID).ToArray(),blocker=liftReturnBlocker.ThingID}));
            GameDataSaveLoader.SaveGame("RimLayers_V1Elevators");
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,56,55).ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"roof-and-elevator.png");screenshotHold=3;stage=11;ticks=0;
        }
    }
    private static void RestoreElevators()
    {
        using var d=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"elevators-baseline.json")));var b=d.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        liftSingle=map.listerThings.AllThings.OfType<Building_Elevator>().Single(t=>t.ThingID==b.GetProperty("single").GetString());
        liftShaft=b.GetProperty("shaft").EnumerateArray().Select(id=>map.listerThings.AllThings.OfType<Building_Elevator>().Single(t=>t.ThingID==id.GetString())).ToArray();
        Check("lift-stop-mode-and-flick-state-reload",!liftShaft[1].StopEnabled&&!liftShaft[1].GetComp<CompFlickable>().SwitchIsOn,$"stop={liftShaft[1].StopEnabled},flick={liftShaft[1].GetComp<CompFlickable>().SwitchIsOn}");
        Check("reload-rebuilds-contiguous-emergency-shaft-edge",owner.Connectors.MovementEdges(TraverseParms.For(pawn)).Any(e=>e.From==liftShaft[0].Position&&e.To==liftShaft[2].Position&&e.Ticks==ExpectedLiftCost(330,false)));
        Check("reloaded-pawn-can-reach-original-ground-floor",pawn.CanReach(At(0,54,54),PathEndMode.OnCell,Danger.Deadly));
        liftReturnBlocker=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("blocker").GetString());
        Check("pending-material-refund-reloads-with-original-blocking-stack",owner.Returns.PendingCount==1&&liftReturnBlocker.stackCount==ThingDefOf.Steel.stackLimit);
        liftReturnBlocker.Position=At(0,10,10);stage=361;ticks=0;
    }
    private static void ElevatorReturnsLoadStep()
    {
        if(owner.Returns.PendingCount!=0){if(ticks>120)throw new Exception("Queued material refund did not retry.");return;}
        Check("saved-refund-retries-once-when-platform-is-cleared",At(1,10,10).GetThingList(map).Where(t=>t.def==ThingDefOf.Steel).Sum(t=>t.stackCount)==4&&liftReturnBlocker.Spawned&&liftReturnBlocker.stackCount==ThingDefOf.Steel.stackLimit);
        LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,56,55).ToVector3Shifted(),12);pendingScreenshot=Path.Combine(directory,"roof-and-elevator-reloaded.png");screenshotHold=3;stage=11;ticks=0;
    }
}
