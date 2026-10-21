using HarmonyLib;
using RimLayers.Ships;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void CheckShipBoarding()
    {
        var behavior=new RitualBehaviorWorker_GravshipLaunch(DefDatabase<RitualBehaviorDef>.GetNamed("GravshipLaunch"));
        var precept=new Precept_GravshipLaunch{behavior=behavior};
        var target=new TargetInfo(shipConsole.parent);
        var assignments=new RitualRoleAssignments(precept,target);
        AccessTools.Field(typeof(RitualRoleAssignments),"assignedRoles").SetValue(assignments,new Dictionary<string,List<Pawn>>{{"pilot",new(){shipPawn}}});
        AccessTools.Field(typeof(RitualRoleAssignments),"allRoles").SetValue(assignments,behavior.def.roles.ToList());
        var crew=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        GenSpawn.Spawn(crew,At(1,78,78),map);crew.drafter.Drafted=true;
        assignments.SpectatorsForReading.Add(crew);
        var ritual=new LordJob_Ritual{selectedTarget=target,assignments=assignments};
        AccessTools.Field(typeof(LordJob_Ritual),"ritual").SetValue(ritual,precept);
        var trigger=new StageEndTrigger_RoleArrivedAndSpectatorsOnSubstructure{tag=StageEndTrigger_RolesArrived.ArrivalTag};
        var arrived=AccessTools.Method(typeof(StageEndTrigger_RoleArrivedAndSpectatorsOnSubstructure),"ArrivedCheck");
        bool Ready()=> (bool)arrived.Invoke(trigger,new object[]{"pilot",ritual})!;
        // Exercise the native 60-tick cadence, not an always-true replacement.
        while(Find.TickManager.TicksGame%60!=0)Find.TickManager.DoSingleTick();
        shipEngine.pawnsToBoard=new(){crew};shipEngine.pawnsToLeave=new();
        Check("launch-still-waits-for-native-pilot-arrival-tag",!Ready());
        ritual.AddTagForPawn(shipPawn,StageEndTrigger_RolesArrived.ArrivalTag);
        foreach(int layer in new[]{0,1,2})
        {
            crew.Position=At(layer,78,78);crew.Notify_Teleported();
            Check("native-launch-stage-accepts-boarded-crew-deck-"+layer,Ready());
        }
        var outside=shipOutside.Position;crew.Position=outside;crew.Notify_Teleported();
        Check("native-launch-stage-waits-for-crew-outside-owned-ship",!Ready());
        crew.Position=At(2,78,78);crew.Notify_Teleported();
        assignments.SpectatorsForReading.Clear();shipEngine.pawnsToBoard.Clear();shipEngine.pawnsToLeave.Add(crew);
        Check("native-launch-stage-waits-for-upper-deck-visitor-to-leave",!Ready());
        crew.Position=outside;crew.Notify_Teleported();Check("native-launch-stage-releases-after-visitor-left",Ready());
        shipEngine.pawnsToLeave.Clear();
        // Actual native GotoShip driver finishes on an upper deck without removing
        // this Pawn from the required-to-board set as a false failure.
        crew.Position=At(2,78,78);crew.Notify_Teleported();crew.drafter.Drafted=false;
        shipEngine.pawnsToBoard.Add(crew);crew.jobs.StartJob(JobMaker.MakeJob(JobDefOf.GotoShip,crew.Position),JobCondition.InterruptForced);
        var driver=crew.jobs.curDriver;
        for(int i=0;i<12;i++)Find.TickManager.DoSingleTick();
        Check("native-upper-deck-gotoship-finish-keeps-required-passenger",driver is JobDriver_BoardOrLeaveGravship&&
            (bool)AccessTools.Field(typeof(JobDriver_BoardOrLeaveGravship),"hasChecked").GetValue(driver)!&&shipEngine.pawnsToBoard.Contains(crew));
        // The original native region graph has no path between disconnected floor
        // regions: upper colony platform -> lower ship must use a connector.
        crew.jobs.StopAll();crew.drafter.Drafted=true;
        bool off=GravshipUtility.TryFindSpotOffGravship(crew,shipEngine,out var exit);
        Check("leaving-spot-search-finds-reachable-other-deck-exit",off&&!shipEngine.ValidSubstructureAt(exit)&&crew.CanReach(exit,PathEndMode.OnCell,Danger.Deadly),$"exit={exit}");
        foreach(var c in new CellRect(100,73,5,5))
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);map.fogGrid.Unfog(c);
        }
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),new IntVec3(102,0,75),map).SetFaction(Faction.OfPlayer);
        crew.Position=At(1,101,75);crew.Notify_Teleported();
        var stair=GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,new IntVec3(102,0,75),map);stair.SetFaction(Faction.OfPlayer);
        map.pathing.RecalculateAllPerceivedPathCosts();map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        bool on=GravshipUtility.TryFindSpotOnGravship(crew,shipEngine,out var spot);
        Check("boarding-spot-search-crosses-floor-region-boundary",on&&shipEngine.ValidSubstructureAt(spot)&&crew.CanReach(spot,PathEndMode.OnCell,Danger.Deadly),$"spot={spot}");
        string reason;Check("native-crew-role-admission-can-reach-ship-across-floors",behavior.PawnCanFillRole(crew,null!,out reason,target),reason);
        stair.Destroy(DestroyMode.Vanish);owner.Connectors.Invalidate();
        Check("boarding-spot-search-does-not-invent-route-after-stair-removal",!GravshipUtility.TryFindSpotOnGravship(crew,shipEngine,out _));
        shipEngine.pawnsToBoard=null;shipEngine.pawnsToLeave=null;crew.Destroy(DestroyMode.Vanish);
    }
}
