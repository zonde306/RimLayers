using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Building_OutfitStand containerSource=null!;
    private static Building_WorkTable containerTable=null!;
    private static Thing containerItem=null!;
    private static Pawn containerCompetitor=null!;
    private static JobDriver containerDriver=null!;
    private static bool containerSaved,containerTaken;

    private static void BeginContainer()
    {
        Check("real-container-requires-odyssey",ModsConfig.OdysseyActive);
        containerSource=(Building_OutfitStand)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Building_OutfitStand"),ThingDefOf.WoodLog),At(0,55,56),map);
        containerSource.SetFaction(Faction.OfPlayer);
        containerItem=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_Duster"),ThingDefOf.Cloth);
        Check("fixture-uses-real-unspawned-container-item",containerSource.AddApparel((Apparel)containerItem) && !containerItem.Spawned &&
            containerItem.MapHeld==map && containerSource.HeldItems.Contains(containerItem));
        containerTable=(Building_WorkTable)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Campfire"),At(2,60,61),map);
        containerTable.SetFaction(Faction.OfPlayer);
        bill=new Bill_Production(DefDatabase<RecipeDef>.GetNamed("BurnApparel"),null){repeatMode=BillRepeatModeDefOf.RepeatCount,repeatCount=1,ingredientSearchRadius=12};
        bill.ingredientFilter.SetDisallowAll();bill.ingredientFilter.SetAllow(containerItem.def,true);containerTable.BillStack.AddBill(bill);
        var ingredient=new IngredientCount();ingredient.filter.SetDisallowAll();ingredient.filter.SetAllow(containerItem.def,true);ingredient.SetBaseCount(1);
        var chosen=new List<ThingCount>();
        bool Select(float radius)=>WorkGiver_DoBill.TryFindBestFixedIngredients(new(){ingredient},pawn,containerTable,chosen,radius);
        void Enable(bool enable)=>AccessTools.Method(typeof(Building_OutfitStand),"SetAllowHauling").Invoke(containerSource,new object[]{enable});
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Enable(false);Check("disabled-container-does-not-provide-ingredients",!Select(12));
        Enable(true);containerItem.SetForbidden(true);Check("forbidden-held-item-does-not-provide-ingredients",!Select(12));containerItem.SetForbidden(false);
        Check("container-radius-is-logical-and-still-enforced",!Select(6) && Select(12) && chosen.Count==1 && chosen[0].Thing==containerItem && chosen[0].Count==1);
        var giver=(WorkGiver_DoBill)DefDatabase<WorkGiverDef>.GetNamed("DoBillsHaulCampfire").Worker;
        expectedJob=giver.JobOnThing(pawn,containerTable,false);
        Check("native-bill-queues-real-held-item",expectedJob?.def==JobDefOf.DoBill && expectedJob.targetQueueB?.Single().Thing==containerItem);
        if(expectedJob==null){Finish();return;}
        pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;containerDriver=pawn.jobs.curDriver;
        containerCompetitor=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);
        Check("competing-pawn-cannot-reserve-same-table-or-held-item",!containerCompetitor.CanReserve(containerTable) && !containerCompetitor.CanReserve(containerItem));
        retainedJob=true;containerSaved=false;containerTaken=false;visited.Clear();stage=120;ticks=0;
    }

    private static void RestoreContainer()
    {
        using var b=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"container-baseline.json")));var d=b.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==d.GetProperty("pawn").GetString());
        containerCompetitor=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);
        containerSource=(Building_OutfitStand)map.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty("source").GetString());
        containerTable=(Building_WorkTable)map.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty("table").GetString());
        containerItem=pawn.carryTracker.CarriedThing;
        expectedJob=pawn.CurJob;expectedJobId=d.GetProperty("job").GetInt32();containerDriver=pawn.jobs.curDriver;
        bill=containerTable.BillStack.Bills.OfType<Bill_Production>().Single();
        Check("restart-keeps-native-container-bill-job",ExpectedJobMatches() && expectedJob?.def==JobDefOf.DoBill && expectedJob.targetA.Thing==containerTable);
        Check("restart-keeps-extracted-item-owned-once",containerItem!=null && containerItem.ThingID==d.GetProperty("item").GetString() &&
            containerItem.stackCount==1 && !containerItem.Spawned && containerItem.ParentHolder==pawn.carryTracker && containerSource.HeldItems.Count==0);
        Check("restart-retains-container-table-reservation",map.reservationManager.ReservedBy(containerTable,pawn,expectedJob) && !containerCompetitor.CanReserve(containerTable));
        visited.Clear();foreach(var l in d.GetProperty("layers").EnumerateArray())visited.Add(l.GetInt32());
        retainedJob=true;containerSaved=true;containerTaken=true;stage=120;ticks=0;
    }

    private static void ContainerStep()
    {
        if(stage==121)
        {
            if(ticks<80)return;
            Check("completed-container-bill-releases-reservations",containerCompetitor.CanReserve(containerTable) && !map.reservationManager.IsReservedByAnyoneOf(containerTable,Faction.OfPlayer));
            Check("completed-container-bill-does-not-resurrect-item",containerItem.Destroyed && containerSource.HeldItems.Count==0);
            LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(containerTable.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,mode+"-container.png");screenshotHold=3;stage=11;ticks=0;return;
        }
        if(owner.TryDecode(pawn.Position,out var here))visited.Add(here.LayerId);
        if(pawn.carryTracker.CarriedThing==containerItem)containerTaken=true;
        if(mode=="generate" && !containerSaved && pawn.carryTracker.CarriedThing==containerItem && pawn.pather.Moving &&
            owner.TryDecode(pawn.pather.nextCell,out var next) && here.LayerId!=next.LayerId && pawn.pather.nextCellCostLeft<pawn.pather.nextCellCostTotal)
        {
            GameDataSaveLoader.SaveGame("RimLayers_V1Container");
            File.WriteAllText(Path.Combine(directory,"container-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,job=expectedJobId,
                source=containerSource.ThingID,table=containerTable.ThingID,item=containerItem.ThingID,layers=visited.ToArray()}));
            Check("saved-while-native-bill-carries-extracted-item-across-stairs",containerSource.HeldItems.Count==0);containerSaved=true;
        }
        if(bill!.repeatCount==0)
        {
            Check("native-bill-extracts-and-consumes-container-item-once",containerTaken && containerItem.Destroyed && containerSource.HeldItems.Count==0);
            Check("container-bill-retains-one-job-and-driver",retainedJob);
            Check("container-trip-actually-visits-all-three-floors",visited.SetEquals(new[]{0,1,2}));
            Check("container-save-was-exercised",containerSaved);
            pawn.drafter.Drafted=true;stage=121;ticks=0;
        }
        else if(!ExpectedJobMatches() || !ReferenceEquals(containerDriver,pawn.jobs.curDriver))
        {retainedJob=false;Check("container-bill-not-interrupted",false,DescribePawn()+", toil="+pawn.jobs.curDriver?.CurToilIndex);Finish();}
        else if(ticks>6500){Check("container-bill-completes",false,DescribePawn());Finish();}
    }
}
