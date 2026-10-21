using System.IO;
using System.Text.Json;
using RimLayers.Cargo;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Building_CargoHoist cargoMain=null!;
    private static Thing cargoSteel=null!;
    private static JobDriver cargoDriver=null!;
    private static int cargoPhase;
    private static bool cargoSaved;
    private static bool cargoOriginalObserved;
    private static void BeginCargo()
    {
        foreach(var stair in map.listerThings.AllThings.OfType<RimLayers.Structure.Building_Stairs>().ToArray())stair.Destroy(DestroyMode.Vanish);
        foreach(var p in map.mapPawns.FreeColonistsSpawned){p.jobs.StopAll();p.drafter.Drafted=true;}
        // This scenario later forces a cooking bill as well as hauling. The actor must
        // support both jobs; a random colonist with Cooking disabled is not a valid fixture.
        pawn=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)&&!p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking")));
        pawn.drafter.Drafted=false;pawn.needs.food.CurLevelPercentage=1;pawn.needs.rest.CurLevelPercentage=1;
        foreach(var work in DefDatabase<WorkTypeDef>.AllDefs)pawn.workSettings.SetPriority(work,0);
        pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        foreach(int layer in new[]{0,1,2})foreach(var c in new CellRect(50,50,15,15))map.fogGrid.Unfog(At(layer,c.x,c.z));
        Check("cargo-no-person-connectors",!owner.Connectors.HasAny);
        var def=DefDatabase<ThingDef>.GetNamed("RL_CargoManualUp");
        Check("cargo-placement-existing-floor",CargoPlacement.Validate(def,At(0,57,57),map).Accepted);
        Check("cargo-placement-rejects-missing-fourth-floor",!CargoPlacement.Validate(def,At(2,57,57),map).Accepted);
        cargoMain=(Building_CargoHoist)ThingMaker.MakeThing(def);cargoMain.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(cargoMain,At(0,57,57),map);
        var ports=CargoNetwork.For(map).Ports.Where(p=>p.Master==cargoMain).ToArray();
        Check("cargo-two-linked-ports-one-basket",ports.Length==2&&ports.All(p=>p.Master==cargoMain));
        Check("cargo-shared-settings",ReferenceEquals(ports[0].GetStoreSettings(),ports[1].GetStoreSettings()));
        var empty=GenClosest.ClosestThingReachable(At(1,54,54),map,ThingRequest.ForDef(ThingDefOf.Steel),PathEndMode.ClosestTouch,TraverseParms.For(pawn));
        Check("cargo-empty-does-not-invent-stock",empty==null);
        cargoSteel=ThingMaker.MakeThing(ThingDefOf.Steel);cargoSteel.stackCount=20;GenSpawn.Spawn(cargoSteel,At(0,55,54),map);
        var job=HaulAIUtility.HaulToStorageJob(pawn,cargoSteel,false);
        Check("cargo-native-storage-selects-basket-for-hauling",job?.def==JobDefOf.HaulToContainer&&job.targetB.Thing==cargoMain);
        if(job==null){Finish();return;}
        pawn.jobs.TryTakeOrderedJob(job);
        cargoPhase=0;stage=530;ticks=0;cargoSaved=false;
        if(CargoCheckpoint=="queue")
        {
            cargoQueuePawn=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);
            cargoQueuePawn.drafter.Drafted=false;cargoQueuePawn.Position=At(1,56,57);cargoQueuePawn.Notify_Teleported();
            cargoQueuePawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(CargoDefOf.RL_CallCargo,ports.First(p=>p!=cargoMain)));
        }
    }
    private static void CargoStep()
    {
        ticks++;
        if(ticks>(cargoPhase==7?16000:6000)){Check("cargo-operation-completes",false,DescribePawn()+", phase="+cargoPhase+", basket="+cargoMain.Motion.Position+", requests="+cargoMain.Requests.Count+", toil="+pawn.jobs.curDriver?.CurToilIndex);Finish();return;}
        if(mode=="generate"&&!cargoSaved&&cargoPhase==0&&
            (CargoCheckpoint=="handling"&&cargoMain.Requests.Any(r=>r.Pawn==pawn&&r.Handled>=5)||
             CargoCheckpoint=="queue"&&cargoMain.Requests.Count>=2))SaveCargoCheckpoint();
        if(cargoPhase==0&&cargoMain.Contents.TotalStackCountOfDef(ThingDefOf.Steel)==20)
        {
            Check("cargo-native-haul-deposits-real-items",pawn.carryTracker.CarriedThing==null&&cargoMain.Contents.Contains(cargoSteel));
            pawn.jobs.StopAll();pawn.Position=At(1,54,54);pawn.Notify_Teleported();
            var found=GenClosest.ClosestThingReachable(pawn.Position,map,ThingRequest.ForDef(ThingDefOf.Steel),PathEndMode.ClosestTouch,TraverseParms.For(pawn));
            Check("cargo-held-stock-discoverable-without-stairs",found==cargoSteel);
            Check("cargo-item-reachable-port-not-people-path",pawn.CanReach(cargoSteel,PathEndMode.ClosestTouch,Danger.Deadly)&&
                !pawn.CanReach(At(0,54,54),PathEndMode.OnCell,Danger.Deadly));
            var local=ThingMaker.MakeThing(ThingDefOf.Steel);local.stackCount=1;GenSpawn.Spawn(local,At(1,55,54),map);
            Check("cargo-cheaper-local-source-wins",GenClosest.ClosestThingReachable(pawn.Position,map,ThingRequest.ForDef(ThingDefOf.Steel),PathEndMode.ClosestTouch,TraverseParms.For(pawn))==local);
            local.Destroy(DestroyMode.Vanish);
            var port=CargoNetwork.For(map).Ports.Single(p=>p.Master==cargoMain&&p!=cargoMain);
            port.AccessEnabled=false;
            Check("cargo-disabled-local-port-removes-remote-stock",GenClosest.ClosestThingReachable(pawn.Position,map,ThingRequest.ForDef(ThingDefOf.Steel),PathEndMode.ClosestTouch,TraverseParms.For(pawn))==null);
            port.AccessEnabled=true;
            var blockers=GenAdj.CardinalDirections.Select(d=>GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),port.Position+d,map)).ToArray();
            Check("cargo-blocked-operator-cells-disable-access",!port.Available);
            foreach(var blocker in blockers)blocker.Destroy(DestroyMode.Vanish);
            Check("cargo-clearing-operator-cells-restores-access",port.Available);
            destination=At(1,59,57);
            var zone=new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager);map.zoneManager.RegisterZone(zone);zone.AddCell(destination);zone.GetStoreSettings().Priority=StoragePriority.Critical;
            expectedJob=HaulAIUtility.HaulToStorageJob(pawn,cargoSteel,true);
            Check("cargo-native-storage-finds-local-unload-destination",expectedJob?.def==JobDefOf.HaulToCell&&expectedJob.targetB.Cell==destination,expectedJob?.ToString()??"null");
            if(expectedJob==null){Finish();return;}expectedJob.count=10;
            pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;cargoDriver=pawn.jobs.curDriver;
            cargoPhase=1;ticks=0;
        }
        if(cargoPhase==1)
        {
            if(cargoMain.Motion.Moving) cargoOriginalObserved|=ReferenceEquals(pawn.CurJob,expectedJob)&&ReferenceEquals(pawn.jobs.curDriver,cargoDriver)&&pawn.CurJob.loadID==expectedJobId;
            if(mode=="generate"&&!cargoSaved&&CargoCheckpoint=="travel"&&cargoMain.Motion.Moving&&cargoMain.Motion.Position>.2f)
            {
                SaveCargoCheckpoint();
            }
            if(owner.TryDecode(pawn.Position,out var loc))CheckCargoLayer(loc.LayerId);
            if(destination.GetThingList(map).Any(t=>t.def==ThingDefOf.Steel&&t.stackCount==10))
            {
                Check("cargo-withdraws-exact-reserved-quantity",cargoMain.Contents.TotalStackCountOfDef(ThingDefOf.Steel)==10);
                Check("cargo-original-haul-job-preserved",cargoOriginalObserved);
                Check("cargo-travel-and-handling-take-time",mode=="load"||ticks>=180);
                Check("cargo-save-checkpoint-exercised",cargoSaved);
                pawn.jobs.StopAll();cargoPhase=2;ticks=0;
                if(CargoCheckpoint!="travel"){Finish();return;}
                BeginCargoConsumers();return;
            }
        }
        if(cargoPhase>=2)CargoConsumersStep();
    }
    private static bool cargoWrongLayer;
    private static void CheckCargoLayer(int layer)
    {
        if(layer==1||cargoWrongLayer)return;cargoWrongLayer=true;Check("cargo-pawn-stays-on-access-floor",false,layer.ToString());
    }
    private static void RestoreCargo()
    {
        using var b=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"cargo-baseline.json")));var d=b.RootElement;
        cargoMain=map.listerThings.AllThings.OfType<Building_CargoHoist>().Single(h=>h.ThingID==d.GetProperty("main").GetString());
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==d.GetProperty("pawn").GetString());
        var items=cargoMain.Contents.Concat(pawn.carryTracker.innerContainer).Concat(map.listerThings.AllThings).Where(t=>t.ThingID==d.GetProperty("item").GetString()).ToArray();
        Check("cargo-reload-exactly-one-item-identity",items.Length==1);cargoSteel=items.Single();
        expectedJob=pawn.CurJob;expectedJobId=d.GetProperty("job").GetInt32();cargoDriver=pawn.jobs.curDriver;
        Check("cargo-reload-same-native-job",expectedJob?.loadID==expectedJobId);
        Check("cargo-reload-exact-motion",Math.Abs(cargoMain.Motion.Position-d.GetProperty("position").GetSingle())<.001f);
        cargoPhase=d.GetProperty("phase").GetInt32();
        Check("cargo-reload-single-owner-and-quantity",cargoSteel.stackCount==20&&(cargoPhase==1?cargoSteel.ParentHolder==cargoMain:cargoSteel.ParentHolder==pawn.carryTracker||cargoSteel.Spawned));
        Check("cargo-reload-request",cargoMain.Requests.Count==d.GetProperty("requests").GetInt32()&&cargoMain.Requests.All(r=>r.Valid));
        Check("cargo-reload-handling-progress",cargoMain.Requests.First(r=>r.Pawn==pawn).Handled==d.GetProperty("handled").GetInt32());
        foreach(var other in cargoMain.Requests.Where(r=>r.Pawn!=pawn).Select(r=>r.Pawn).Distinct().ToArray()){other.jobs.StopAll();other.drafter.Drafted=true;}
        Check("cargo-reload-cancel-releases-queued-request",cargoMain.Requests.Count==1);
        destination=At(1,59,57);cargoSaved=true;stage=530;ticks=0;
    }
    private static string CargoCheckpoint=>GenCommandLine.TryGetCommandLineArg("rimlayerscargocheckpoint",out var value)?value:"travel";
    private static Pawn? cargoQueuePawn;
    private static void SaveCargoCheckpoint()
    {
        var own=cargoMain.Requests.First(r=>r.Pawn==pawn);
        File.WriteAllText(Path.Combine(directory,"cargo-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,main=cargoMain.ThingID,item=cargoSteel.ThingID,job=pawn.CurJob.loadID,position=cargoMain.Motion.Position,phase=cargoPhase,handled=own.Handled,requests=cargoMain.Requests.Count}));
        GameDataSaveLoader.SaveGame("RimLayers_V1Cargo");
        cargoSaved=true;Check("cargo-saved-during-"+CargoCheckpoint,true);
        if(cargoQueuePawn!=null){cargoQueuePawn.jobs.StopAll();cargoQueuePawn.drafter.Drafted=true;}
    }
}
