using System.Text.Json;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Blueprint_Build homeBlueprint=null!;
    private static Thing homeStool=null!,homePin=null!;
    private static readonly List<Thing> homeWood=new();
    private static readonly HashSet<int> homePickupFloors=new();
    private static Thing? homeCarry;
    private static bool homeFrameSeen,homeFinishSeen,homeSaved,homeJoyRetained=true;
    private static readonly List<Pawn> homePlayers=new();
    private static readonly List<Job> homeJoyJobs=new();
    private static readonly List<JobDriver> homeJoyDrivers=new();
    private static readonly List<int> homeJoyIds=new();
    private static void BeginHome()
    {
        var construction=DefDatabase<WorkTypeDef>.GetNamed("Construction");
        foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList()){p.jobs.StopAll();p.drafter.Drafted=true;}
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(construction));
        pawn.drafter.Drafted=false;pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        pawn.skills.GetSkill(SkillDefOf.Construction).Level=20;
        pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;pawn.needs.joy.CurLevelPercentage=1;
        foreach(var work in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(work,0);
        pawn.workSettings.SetPriority(construction,1);
        foreach(var wood in map.listerThings.ThingsOfDef(ThingDefOf.WoodLog))wood.SetForbidden(true);
        int need=ThingDefOf.Stool.CostListAdjusted(ThingDefOf.WoodLog).Sum(c=>c.count);
        for(int i=0;i<2;i++)
        {
            var wood=ThingMaker.MakeThing(ThingDefOf.WoodLog);wood.stackCount=i==0?need/2:need-need/2;
            GenSpawn.Spawn(wood,At(i,55+i,57),map);homeWood.Add(wood);
        }
        var site=At(2,58,58);map.areaManager.Home[site]=true;
        Check("native-blueprint-entry-accepts-supported-furniture",GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Stool,site,Rot4.North,map,stuffDef:ThingDefOf.WoodLog).Accepted);
        map.roofGrid.SetRoof(At(1,58,58),null);
        Check("native-blueprint-entry-rejects-missing-lower-roof",!GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Stool,site,Rot4.North,map,stuffDef:ThingDefOf.WoodLog).Accepted);
        map.roofGrid.SetRoof(At(1,58,58),RoofDefOf.RoofConstructed);
        homeBlueprint=(Blueprint_Build)GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Stool,site,map,Rot4.North,Faction.OfPlayer,ThingDefOf.WoodLog);
        var deliver=(WorkGiver_ConstructDeliverResourcesToBlueprints)DefDatabase<WorkGiverDef>.GetNamed("ConstructDeliverResourcesToBlueprints").Worker;
        map.roofGrid.SetRoof(At(1,58,58),null);
        Check("native-supply-work-rejects-unsupported-blueprint",deliver.JobOnThing(pawn,homeBlueprint)==null);
        map.roofGrid.SetRoof(At(1,58,58),RoofDefOf.RoofConstructed);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var result=new JobGiver_Work().TryIssueJobPackage(pawn,default);
        Check("native-work-scheduler-finds-upper-blueprint-supply",result.Job!=null&&
            new[]{result.Job.targetA.Thing,result.Job.targetB.Thing,result.Job.targetC.Thing}.Contains(homeBlueprint),result.Job?.ToString()??JobFailReason.Reason);
        if(result.Job==null){Finish();return;}
        pawn.jobs.StartJob(result.Job,JobCondition.InterruptForced);stage=250;ticks=0;
    }
    private static void HomeBuildStep()
    {
        var carry=pawn.carryTracker.CarriedThing;
        if(carry?.def==ThingDefOf.WoodLog&&carry!=homeCarry&&owner.TryDecode(pawn.Position,out var c))homePickupFloors.Add(c.LayerId);
        homeCarry=carry;
        homeFrameSeen|=At(2,58,58).GetThingList(map).Any(t=>t is Frame);
        homeFinishSeen|=pawn.jobs.curDriver is JobDriver_ConstructFinishFrame;
        var built=At(2,58,58).GetEdifice(map);
        if(built?.def==ThingDefOf.Stool)
        {
            homeStool=built;
            Check("native-blueprint-frame-furniture-pipeline-completes",homeBlueprint.Destroyed&&homeFrameSeen&&homeFinishSeen);
            Check("native-supply-picks-real-materials-on-two-floors",homePickupFloors.Contains(0)&&homePickupFloors.Contains(1));
            Check("native-construction-consumes-exact-test-stacks-once",homeWood.All(t=>t.Destroyed)&&homeStool.Stuff==ThingDefOf.WoodLog&&
                At(2,58,58).GetThingList(map).Count(t=>t.def==ThingDefOf.Stool)==1);
            BeginHomeJoy();return;
        }
        if(ticks>6500){Check("native-upper-furniture-construction-completes",false,DescribePawn());Finish();}
    }
    private static void BeginHomeJoy()
    {
        pawn.jobs.StopAll();pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Construction"),0);
        homePin=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("HorseshoesPin"),ThingDefOf.WoodLog);
        GenSpawn.Spawn(homePin,At(2,59,60),map);homePin.SetFaction(Faction.OfPlayer);
        homePlayers.AddRange(map.mapPawns.FreeColonistsSpawned.Take(3));
        var giver=DefDatabase<JoyGiverDef>.GetNamed("Play_Horseshoes").Worker;
        for(int i=0;i<homePlayers.Count;i++)
        {
            var p=homePlayers[i];p.jobs.StopAll();p.drafter.Drafted=false;
            p.Position=At(0,54+i,54);p.Notify_Teleported();
            p.needs.food.CurLevelPercentage=1;p.needs.rest.CurLevelPercentage=1;p.needs.joy.CurLevelPercentage=.1f;
            var job=giver.TryGiveJob(p);
            Check("native-joy-candidate-selects-third-floor-pin-"+i,job?.targetA.Thing==homePin&&job.def.defName=="Play_Horseshoes");
            if(job==null){Finish();return;}
            p.jobs.TryTakeOrderedJob(job);homeJoyJobs.Add(job);homeJoyIds.Add(job.loadID);homeJoyDrivers.Add(p.jobs.curDriver);
        }
        var fourth=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("SpaceRefugee_Clothed"),Faction.OfPlayer);
        GenSpawn.Spawn(fourth,At(0,54,56),map);
        Check("native-joy-capacity-rejects-fourth-participant",!fourth.CanReserve(homePin,maxPawns:3)&&giver.TryGiveJob(fourth)==null);
        fourth.drafter.Drafted=true;
        pawn=homePlayers[0];stage=251;ticks=0;
    }
    private static void RestoreHome()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"home-baseline.json")));
        var b=baseline.RootElement;
        homeStool=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("stool").GetString());
        homePin=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("pin").GetString());
        foreach(var entry in b.GetProperty("players").EnumerateArray())
        {
            var p=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==entry.GetProperty("pawn").GetString());
            homePlayers.Add(p);homeJoyJobs.Add(p.CurJob);homeJoyDrivers.Add(p.jobs.curDriver);homeJoyIds.Add(entry.GetProperty("job").GetInt32());
        }
        Check("restart-keeps-built-furniture-and-all-native-joy-jobs",homeStool.def==ThingDefOf.Stool&&homePlayers.Count==3&&
            homePlayers.Select((p,i)=>p.CurJob?.loadID==homeJoyIds[i]&&p.CurJob.targetA.Thing==homePin).All(v=>v));
        Check("restart-resumes-joy-trip-in-middle-floor",owner.TryDecode(homePlayers[0].Position,out var c)&&c.LayerId==1);
        pawn=homePlayers[0];homeSaved=true;stage=251;ticks=0;
    }
    private static void HomeJoyStep()
    {
        homeJoyRetained&=homePlayers.Select((p,i)=>p.CurJob==homeJoyJobs[i]&&p.CurJob?.loadID==homeJoyIds[i]&&p.jobs.curDriver==homeJoyDrivers[i]).All(v=>v);
        if(!homeSaved&&owner.TryDecode(pawn.Position,out var c)&&c.LayerId==1)
        {
            GameDataSaveLoader.SaveGame("RimLayers_V1Home");
            File.WriteAllText(Path.Combine(directory,"home-baseline.json"),JsonSerializer.Serialize(new {stool=homeStool.ThingID,pin=homePin.ThingID,
                players=homePlayers.Select((p,i)=>new {pawn=p.ThingID,job=homeJoyIds[i]}).ToArray()}));
            homeSaved=true;Check("saved-native-joy-party-between-floors",true);
        }
        if(homePlayers.All(p=>p.needs.joy.CurLevelPercentage>=.14f))
        {
            Check("native-joy-party-really-plays-on-third-floor",homePlayers.All(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId==2&&p.jobs.curDriver is JobDriver_PlayHorseshoes));
            Check("native-joy-jobs-and-drivers-survive-multiple-stairs",homeJoyRetained);
            Check("native-joy-gain-is-real-and-save-checkpoint-reached",homeSaved);
            LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.Selector.Select(homePin);
            Find.CameraDriver.SetRootPosAndSize(homePin.Position.ToVector3Shifted(),14);
            pendingScreenshot=Path.Combine(directory,mode+"-home.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(ticks>6500){Check("native-upper-joy-game-starts-and-gains-joy",false,string.Join(";",homePlayers.Select(p=>$"{p.Position}/{p.CurJob}/joy={p.needs.joy.CurLevel}")));Finish();}
    }
}
