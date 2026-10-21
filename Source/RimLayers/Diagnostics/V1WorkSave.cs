using System.IO;
using System.Text.Json;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static bool workSaved;
    private static void SaveBillIfCrossing(Thing? carry)
    {
        if(scenario!="work-save" || mode!="generate" || workSaved || carry==null || !pawn.pather.Moving ||
            !owner.TryDecode(pawn.Position,out var here) || !owner.TryDecode(pawn.pather.nextCell,out var next) ||
            here.LayerId==next.LayerId || pawn.pather.nextCellCostLeft>=pawn.pather.nextCellCostTotal)return;
        GameDataSaveLoader.SaveGame("RimLayers_V1Work");
        File.WriteAllText(Path.Combine(directory,"work-baseline.json"),JsonSerializer.Serialize(new{
            pawn=pawn.ThingID,job=expectedJobId,table=pawn.CurJob.targetA.Thing.ThingID,
            carry=carry.ThingID,carryCount=carry.stackCount,queue=pawn.CurJob.targetQueueB.Select(t=>t.Thing.ThingID).ToArray(),
            counts=pawn.CurJob.countQueue.ToArray(),ingredients=workIngredients.Select(t=>t.ThingID).ToArray(),
            layers=visited.ToArray(),pickups,driver=pawn.jobs.curDriver.GetType().FullName}));
        Check("bill-saved-with-real-carry-mid-stair-and-remaining-queue",carry.stackCount==5 && pawn.CurJob.targetQueueB.Count==1);
        workSaved=true;
    }
    private static void RestoreBill()
    {
        using var b=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"work-baseline.json")));var d=b.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==d.GetProperty("pawn").GetString());
        var table=(Building_WorkTable)map.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty("table").GetString());
        bill=table.BillStack.Bills.OfType<Bill_Production>().Single();
        expectedJob=pawn.CurJob;expectedJobId=d.GetProperty("job").GetInt32();
        Thing? carry=pawn.carryTracker.CarriedThing;
        workIngredients.Clear();
        foreach(var id in d.GetProperty("ingredients").EnumerateArray().Select(x=>x.GetString()))
            workIngredients.Add(carry!=null && carry.ThingID==id?carry:map.listerThings.AllThings.Single(t=>t.ThingID==id));
        visited.Clear();foreach(var l in d.GetProperty("layers").EnumerateArray())visited.Add(l.GetInt32());
        pickups=d.GetProperty("pickups").GetInt32();lastCarry=carry;retainedJob=true;workSaved=true;
        Check("restart-keeps-bill-job-driver-and-table",ExpectedJobMatches() && expectedJob?.def==JobDefOf.DoBill &&
            expectedJob.targetA.Thing==table && pawn.jobs.curDriver.GetType().FullName==d.GetProperty("driver").GetString());
        Check("restart-carry-has-one-native-owner",carry!=null && carry.ThingID==d.GetProperty("carry").GetString() &&
            carry.stackCount==d.GetProperty("carryCount").GetInt32() && !carry.Spawned && carry.ParentHolder==pawn.carryTracker);
        Check("restart-retains-material-queue-and-counts",expectedJob!.targetQueueB.Select(t=>t.Thing.ThingID).SequenceEqual(
            d.GetProperty("queue").EnumerateArray().Select(v=>v.GetString())) && expectedJob.countQueue.SequenceEqual(d.GetProperty("counts").EnumerateArray().Select(v=>v.GetInt32())));
        Check("restart-retains-native-material-and-table-reservations",map.reservationManager.ReservedBy(table,pawn,expectedJob) &&
            expectedJob.targetQueueB.All(t=>map.reservationManager.ReservedBy(t,pawn,expectedJob)));
        stage=10;ticks=0;
    }
}
