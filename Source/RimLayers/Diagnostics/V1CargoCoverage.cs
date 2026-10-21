using System.IO;
using HarmonyLib;
using RimLayers.Cargo;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static Thing cargoMeal=null!,cargoWeapon=null!,cargoApparel=null!;
    private static Building_WorkTable cargoStove=null!;
    private static readonly List<Thing> cargoIngredients=new();
    private static Building_CargoHoist cargoShaft=null!;
    private static Building_CargoPort cargoTop=null!,cargoMiddle=null!;
    private static float cargoPausedAt;
    private static Thing cargoCraftWood=null!;
    private static int cargoBowBefore;
    private static int cargoPauseTick;
    private static Thing PutCargo(string defName,int count=1,ThingDef? stuff=null)
    {
        var t=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(defName),stuff);t.stackCount=count;
        Check("cargo-fixture-owner-"+defName,cargoMain.Contents.TryAdd(t,false));
        return t;
    }
    private static void BeginCargoConsumers()
    {
        Check("cargo-pawn-never-used-other-floor",!cargoWrongLayer);
        pawn.skills.GetSkill(SkillDefOf.Cooking).Level=20;
        cargoStove=(Building_WorkTable)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("FueledStove")),At(1,60,61),map);
        cargoStove.SetFaction(Faction.OfPlayer);cargoStove.GetComp<CompRefuelable>().Refuel(30);
        bill=new Bill_Production(DefDatabase<RecipeDef>.GetNamed("CookMealSimple"),null){repeatMode=BillRepeatModeDefOf.RepeatCount,repeatCount=1,ingredientSearchRadius=12};
        bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor,null);cargoStove.BillStack.AddBill(bill);
        cargoIngredients.Clear();cargoIngredients.Add(PutCargo("RawPotatoes",3));cargoIngredients.Add(PutCargo("RawRice",7));
        // Force a real remote trip for consumers after the haul test.
        cargoMain.Motion.Position=0;
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var giver=(WorkGiver_DoBill)DefDatabase<WorkGiverDef>.GetNamed("DoBillsCook").Worker;
        expectedJob=giver.JobOnThing(pawn,cargoStove,true);
        Check("cargo-native-cook-selects-two-held-ingredients",expectedJob?.def==JobDefOf.DoBill&&expectedJob.targetQueueB?.Count==2&&expectedJob.countQueue?.Sum()==10,expectedJob?.ToString()??JobFailReason.Reason);
        if(expectedJob==null){Finish();return;}
        pawn.jobs.TryTakeOrderedJob(expectedJob);cargoPhase=2;ticks=0;
    }
    private static void CargoConsumersStep()
    {
        if(cargoPhase<20&&owner.TryDecode(pawn.Position,out var l))CheckCargoLayer(l.LayerId);
        if(cargoPhase==2&&bill!.repeatCount==0)
        {
            Check("cargo-cooking-consumes-exact-mixed-amount",cargoIngredients.All(t=>t.Destroyed));
            Check("cargo-cooking-completes-without-changing-floor",!cargoWrongLayer);
            pawn.jobs.StopAll();
            cargoWeapon=PutCargo("Gun_Autopistol");
            expectedJob=JobMaker.MakeJob(JobDefOf.Equip,cargoWeapon);pawn.jobs.TryTakeOrderedJob(expectedJob);
            cargoPhase=3;ticks=0;
        }
        if(cargoPhase==3&&pawn.equipment.Primary==cargoWeapon)
        {
            Check("cargo-native-equip-completes",!cargoMain.Contents.Contains(cargoWeapon));
            pawn.jobs.StopAll();cargoApparel=PutCargo("Apparel_Duster",1,ThingDefOf.Cloth);
            expectedJob=JobMaker.MakeJob(JobDefOf.Wear,cargoApparel);pawn.jobs.TryTakeOrderedJob(expectedJob);cargoPhase=4;ticks=0;
        }
        if(cargoPhase==4&&pawn.apparel.WornApparel.Contains(cargoApparel))
        {
            Check("cargo-native-wear-completes",!cargoMain.Contents.Contains(cargoApparel));
            pawn.jobs.StopAll();
            foreach(var food in map.listerThings.AllThings.Where(t=>t.def.category==ThingCategory.Item&&t.def.IsNutritionGivingIngestible).ToArray())food.SetForbidden(true);
            cargoMeal=PutCargo("MealSimple",2);pawn.needs.food.CurLevelPercentage=.1f;
            expectedJob=new JobGiver_GetFood().TryIssueJobPackage(pawn,default).Job;
            Check("cargo-native-hunger-selects-real-held-food",expectedJob?.def==JobDefOf.Ingest&&expectedJob.targetA.Thing==cargoMeal,expectedJob?.ToString()??"null");
            if(expectedJob==null){Finish();return;}
            pawn.jobs.TryTakeOrderedJob(expectedJob);cargoPhase=5;ticks=0;
        }
        if(cargoPhase==5&&pawn.needs.food.CurLevelPercentage>.5f)
        {
            Check("cargo-native-ingest-completes",cargoMain.Contents.TotalStackCountOfDef(ThingDefOf.MealSimple)==1&&!cargoWrongLayer);
            pawn.jobs.StopAll();
            foreach(var t in map.listerThings.ThingsOfDef(ThingDefOf.WoodLog))t.SetForbidden(true);
            int need=ThingDefOf.Stool.CostListAdjusted(ThingDefOf.WoodLog).Sum(c=>c.count);
            PutCargo("WoodLog",need);
            homeBlueprint=(Blueprint_Build)GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Stool,At(1,61,57),map,Rot4.North,Faction.OfPlayer,ThingDefOf.WoodLog);
            var giver=(WorkGiver_ConstructDeliverResourcesToBlueprints)DefDatabase<WorkGiverDef>.GetNamed("ConstructDeliverResourcesToBlueprints").Worker;
            expectedJob=giver.JobOnThing(pawn,homeBlueprint,true);
            Check("cargo-native-builder-selects-held-material",expectedJob!=null&&CargoNetwork.OwnerOf(expectedJob.targetA.Thing)==cargoMain,expectedJob?.ToString()??JobFailReason.Reason);
            if(expectedJob==null){Finish();return;}
            pawn.jobs.TryTakeOrderedJob(expectedJob);cargoPhase=6;ticks=0;
        }
        if(cargoPhase==6&&At(1,61,57).GetThingList(map).OfType<Frame>().FirstOrDefault() is {} frame&&frame.resourceContainer.TotalStackCountOfDef(ThingDefOf.WoodLog)>0)
        {
            Check("cargo-native-blueprint-supply-completes",frame.resourceContainer.TotalStackCountOfDef(ThingDefOf.WoodLog)==ThingDefOf.Stool.CostListAdjusted(ThingDefOf.WoodLog).Sum(c=>c.count)&&cargoMain.Contents.TotalStackCountOfDef(ThingDefOf.WoodLog)==0&&!cargoWrongLayer);
            pawn.jobs.StopAll();
            pawn.skills.GetSkill(SkillDefOf.Crafting).Level=20;
            pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Crafting"),1);
            pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;pawn.needs.joy.CurLevelPercentage=1;
            cargoCraftWood=PutCargo("WoodLog",30);
            var table=(Building_WorkTable)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("CraftingSpot")),At(1,60,54),map);table.SetFaction(Faction.OfPlayer);
            bill=(Bill_Production)DefDatabase<RecipeDef>.GetNamed("Make_Bow_Short").MakeNewBill();bill.repeatMode=BillRepeatModeDefOf.RepeatCount;bill.repeatCount=1;bill.ingredientSearchRadius=12;
            bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor,null);table.BillStack.AddBill(bill);
            cargoBowBefore=map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("Bow_Short")).Count;
            expectedJob=((WorkGiver_DoBill)DefDatabase<WorkGiverDef>.GetNamed("DoBillsUseCraftingSpot").Worker).JobOnThing(pawn,table,true);
            Check("cargo-native-manufacturing-selects-held-material",expectedJob?.def==JobDefOf.DoBill&&expectedJob.targetQueueB?.Any(t=>t.Thing==cargoCraftWood)==true);
            if(expectedJob==null){Finish();return;}
            cargoPhase=7;ticks=0;bool accepted=pawn.jobs.TryTakeOrderedJob(expectedJob);Check("cargo-manufacturing-job-started",accepted&&pawn.CurJob==expectedJob,$"accepted={accepted};current={pawn.CurJob};expected={expectedJob};toil={pawn.jobs.curDriver?.CurToilIndex}");
        }
        if(cargoPhase==7&&bill!.repeatCount==0)
        {
            Check("cargo-native-manufacturing-finishes-once",cargoCraftWood.Destroyed&&map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("Bow_Short")).Count==cargoBowBefore+1&&!cargoWrongLayer);
            pawn.jobs.StopAll();pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Crafting"),0);BeginCargoShaft();
        }
        if(cargoPhase==20)
        {
            var power=cargoShaft.GetComp<CompPowerTrader>();
            if(cargoPauseTick==0)
            {
                power.PowerOn=true;
                if(cargoShaft.Motion.Position>.3f){power.PowerOn=false;cargoPausedAt=cargoShaft.Motion.Position;cargoPauseTick=ticks;}
            }
            else if(ticks<cargoPauseTick+60)power.PowerOn=false;
            else if(ticks==cargoPauseTick+60)
            {
                Check("cargo-electric-outage-keeps-position",Math.Abs(cargoShaft.Motion.Position-cargoPausedAt)<.001f&&cargoShaft.Motion.Moving);
                pawn.jobs.StopAll();Check("cargo-cancel-removes-request",cargoShaft.Requests.Count==0);
                power.PowerOn=true;
            }
            else
            {
                power.PowerOn=true;
                if(!cargoShaft.Motion.Moving)
                {
                    Check("cargo-electric-cancel-finishes-current-trip",cargoShaft.AtPort(cargoTop)&&cargoShaft.Requests.Count==0);
                    BeginCargoMechanicalChecks();BeginCargoManualShaft();
                }
            }
        }
        if(cargoPhase==21)CargoManualShaftStep();
        if(cargoPhase==22)
        {
            cargoDownElectric.GetComp<CompPowerTrader>().PowerOn=true;
            if(!cargoDownElectric.Motion.Moving&&cargoDownElectric.Motion.Position==0)
            {
                Check("cargo-electric-adjacent-down-completes",ticks>=75);pawn.jobs.StopAll();cargoPhase=99;ticks=0;
                GenConstruct.PlaceBlueprintForBuild(DefDatabase<ThingDef>.GetNamed("RL_CargoElectricUp"),At(1,59,53),map,Rot4.North,Faction.OfPlayer,null);
                LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.Selector.Select(CargoNetwork.For(map).Ports.First(p=>p.Master==cargoMain&&p!=cargoMain));
                Find.CameraDriver.SetRootPosAndSize(At(1,57,57).ToVector3Shifted(),8);
                pendingScreenshot=Path.Combine(directory,mode+"-cargo.png");screenshotHold=3;
            }
        }
        if(cargoPhase==99&&ticks>50)Finish();
    }
    private static void BeginCargoShaft()
    {
        cargoShaft=(Building_CargoHoist)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoElectricShaft"));cargoShaft.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(cargoShaft,At(0,62,53),map);
        var def=DefDatabase<ThingDef>.GetNamed("RL_CargoShaftStation");
        Check("cargo-shaft-must-extend-adjacently",!CargoPlacement.Validate(def,At(2,62,53),map).Accepted);
        cargoMiddle=(Building_CargoPort)GenSpawn.Spawn(ThingMaker.MakeThing(def),At(1,62,53),map);cargoMiddle.SetFaction(Faction.OfPlayer);
        cargoTop=(Building_CargoPort)GenSpawn.Spawn(ThingMaker.MakeThing(def),At(2,62,53),map);cargoTop.SetFaction(Faction.OfPlayer);
        Check("cargo-shaft-three-stations-one-owner",cargoMiddle.Master==cargoShaft&&cargoTop.Master==cargoShaft);
        Check("cargo-shaft-rejects-second-master",!CargoPlacement.Validate(cargoShaft.def,At(2,62,53),map).Accepted);
        pawn.Position=At(2,61,53);pawn.Notify_Teleported();cargoShaft.GetComp<CompPowerTrader>().PowerOn=true;
        pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(CargoDefOf.RL_CallCargo,cargoTop));
        cargoPhase=20;ticks=0;cargoPauseTick=0;
    }
    private static void BeginCargoMechanicalChecks()
    {
        pawn.jobs.StopAll();
        cargoMiddle.Destroy(DestroyMode.Vanish);
        Check("cargo-cut-shaft-disconnects-top",!cargoTop.Available&&cargoShaft.Contents.Count==0);
        cargoMiddle=(Building_CargoPort)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoShaftStation")),At(1,62,53),map);cargoMiddle.SetFaction(Faction.OfPlayer);
        Check("cargo-repaired-shaft-rejoins-existing-owner",cargoMiddle.Master==cargoShaft&&cargoTop.Available&&cargoShaft.AtPort(cargoTop));
        cargoShaft.GetComp<CompPowerTrader>().PowerOn=false;
        Check("cargo-outage-permits-docked-access-only",cargoShaft.CanCall(cargoTop)&&!cargoShaft.CanCall(cargoShaft));
        var steel=ThingMaker.MakeThing(ThingDefOf.Steel);steel.stackCount=75;cargoShaft.Contents.TryAdd(steel,false);
        var other=ThingMaker.MakeThing(ThingDefOf.Steel);other.stackCount=75;cargoShaft.Contents.TryAdd(other,false);
        var competitor=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);competitor.jobs.StopAll();competitor.drafter.Drafted=false;
        foreach(var p in new[]{pawn,competitor}){var wait=JobMaker.MakeJob(JobDefOf.Wait);wait.expiryInterval=600;p.jobs.TryTakeOrderedJob(wait);}
        var incomingA=ThingMaker.MakeThing(ThingDefOf.WoodLog);incomingA.stackCount=50;
        var incomingB=ThingMaker.MakeThing(ThingDefOf.WoodLog);incomingB.stackCount=50;
        GenSpawn.Spawn(incomingA,At(2,60,52),map);GenSpawn.Spawn(incomingB,At(2,60,53),map);
        var r1=cargoShaft.Request(pawn,cargoTop,incomingA,50,true);
        var r2=cargoShaft.Request(competitor,cargoTop,incomingB,50,true);
        Check("cargo-competing-deposits-reserve-capacity-once",r1?.Count==50&&r2?.Count==25&&cargoShaft.SpaceFor(incomingA)==0);
        Check("cargo-fifo-request-order",cargoShaft.Requests.SequenceEqual(new[]{r1,r2}));
        pawn.jobs.StopAll();Check("cargo-cancel-immediately-releases-capacity",cargoShaft.SpaceFor(incomingA)==50);
        competitor.jobs.StopAll();
        foreach(var p in new[]{pawn,competitor}){var wait=JobMaker.MakeJob(JobDefOf.Wait);wait.expiryInterval=600;p.jobs.TryTakeOrderedJob(wait);}
        r1=cargoShaft.Request(pawn,cargoTop,steel,50,false);r2=cargoShaft.Request(competitor,cargoTop,steel,50,false);
        Check("cargo-competing-withdrawals-reserve-real-quantity-once",r1?.Count==50&&r2?.Count==25);
        pawn.jobs.StopAll();competitor.jobs.StopAll();competitor.drafter.Drafted=true;
        var wood=ThingMaker.MakeThing(ThingDefOf.WoodLog);wood.stackCount=75;cargoShaft.Contents.TryAdd(wood,false);
        Check("cargo-three-native-stacks-no-extra-capacity",cargoShaft.SpaceFor(steel)==0&&cargoShaft.Contents.Count==3);
        cargoShaft.GetStoreSettings().filter.SetDisallowAll();
        Check("cargo-filter-change-preserves-existing-cargo",cargoShaft.Contents.TotalStackCount==225&&!cargoShaft.Accepts(steel));
        int before=map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount);
        cargoShaft.Destroy(DestroyMode.KillFinalize);
        Check("cargo-destroy-releases-real-items-once",steel.Spawned&&other.Spawned&&wood.Spawned&&new[]{steel,other,wood}.All(t=>t.stackCount==75&&map.listerThings.AllThings.Count(x=>x==t)==1),$"steel={steel.Spawned}/{steel.Destroyed}/{steel.ParentHolder};other={other.Spawned}/{other.Destroyed}/{other.ParentHolder};wood={wood.Spawned}/{wood.Destroyed}/{wood.ParentHolder};delta={map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount)-before};recovery={owner.Recovery.GetDirectlyHeldThings().Count}");
        LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.Selector.Select(CargoNetwork.For(map).Ports.First(p=>p.Master==cargoMain&&p!=cargoMain));
        Find.CameraDriver.SetRootPosAndSize(At(1,57,57).ToVector3Shifted(),8);
    }
    private static Building_CargoHoist cargoManualShaft=null!,cargoDownElectric=null!;
    private static Building_CargoPort cargoManualTop=null!;
    private static int cargoManualPause;
    private static float cargoManualPosition;
    private static Thing cargoPerishable=null!;
    private static void BeginCargoManualShaft()
    {
        cargoManualShaft=(Building_CargoHoist)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoManualShaft")),At(0,53,53),map);cargoManualShaft.SetFaction(Faction.OfPlayer);
        foreach(int layer in new[]{1,2})GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoShaftStation")),At(layer,53,53),map).SetFaction(Faction.OfPlayer);
        cargoManualTop=CargoNetwork.For(map).Ports.Single(p=>p.Master==cargoManualShaft&&p.Elevation==2);
        cargoPerishable=ThingMaker.MakeThing(ThingDefOf.MealSimple);cargoManualShaft.Contents.TryAdd(cargoPerishable,false);
        pawn.Position=At(2,52,53);pawn.Notify_Teleported();
        pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(CargoDefOf.RL_CallCargo,cargoManualTop));
        cargoManualPause=0;cargoPhase=21;ticks=0;
    }
    private static void CargoManualShaftStep()
    {
        if(cargoManualPause==0&&cargoManualShaft.Motion.Position>.3f)
        {
            pawn.drafter.Drafted=true;cargoManualPosition=cargoManualShaft.Motion.Position;cargoManualPause=ticks;
            Check("cargo-drafting-cancels-manual-request",cargoManualShaft.Requests.Count==0);
        }
        else if(cargoManualPause>0&&ticks==cargoManualPause+60)
        {
            Check("cargo-manual-interruption-preserves-mechanical-progress",Math.Abs(cargoManualShaft.Motion.Position-cargoManualPosition)<.001f&&cargoManualShaft.Motion.Moving);
            pawn.drafter.Drafted=false;pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(CargoDefOf.RL_CallCargo,cargoManualTop));
        }
        else if(cargoManualPause>0&&!cargoManualShaft.Motion.Moving&&cargoManualShaft.AtPort(cargoManualTop))
        {
            Check("cargo-manual-three-floor-operation-resumes",ticks>=420);
            Check("cargo-held-food-ticks-and-rots",cargoPerishable.TryGetComp<CompRottable>().RotProgress>0,$"rot={cargoPerishable.TryGetComp<CompRottable>().RotProgress};temperature={cargoPerishable.AmbientTemperature}");
            Check("cargo-food-temperature-follows-basket-floor",Math.Abs(cargoPerishable.AmbientTemperature-GenTemperature.GetTemperatureForCell(cargoManualTop.Position,map))<.001f);
            pawn.jobs.StopAll();
            var down=(Building_CargoHoist)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoManualDown")),At(1,51,61),map);down.SetFaction(Faction.OfPlayer);
            Check("cargo-manual-down-builds-lower-port",CargoNetwork.For(map).Ports.Any(p=>p.Master==down&&p.Elevation==0));
            cargoDownElectric=(Building_CargoHoist)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RL_CargoElectricDown")),At(1,53,61),map);cargoDownElectric.SetFaction(Faction.OfPlayer);
            var remote=CargoNetwork.For(map).Ports.Single(p=>p.Master==cargoDownElectric&&p.Elevation==0);
            Check("cargo-electric-down-builds-lower-port",remote.Available);
            pawn.Position=At(0,52,61);pawn.Notify_Teleported();cargoDownElectric.GetComp<CompPowerTrader>().PowerOn=true;
            pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(CargoDefOf.RL_CallCargo,remote));cargoPhase=22;ticks=0;
        }
    }
}
