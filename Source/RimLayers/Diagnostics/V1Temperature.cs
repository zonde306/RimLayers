using System.Text.Json;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn? temperatureBaby;
    private static int temperaturePhase;
    private static bool temperatureJobSeen, temperatureSaved, temperatureCarrySeen;
    private static Room TemperatureRoom(int layer) => At(layer,57,57).GetRoom(map);
    private static Region? TemperatureQuery(Pawn subject, Pawn mover, FloatRange range) =>
        JobGiver_SeekSafeTemperature.ClosestRegionWithinTemperatureRange(subject.PositionHeld,map,subject,range,TraverseParms.For(mover));
    private static void TemperatureNeeds(Pawn p)
    {
        if(p.needs.food!=null)p.needs.food.CurLevelPercentage=1;
        if(p.needs.rest!=null)p.needs.rest.CurLevelPercentage=1;
        if(p.needs.mood!=null)p.needs.mood.CurLevel=1;
    }
    private static void TemperatureClimate()
    {
        bool cold=temperaturePhase==1;
        for(int l=0;l<3;l++)TemperatureRoom(l).Temperature=cold?(l==0?21:-40):(l==2?21:65);
    }
    private static void BeginTemperature()
    {
        for(int layer=0;layer<3;layer++)
            foreach(var local in new CellRect(50,50,15,15))
            {
                var c=At(layer,local.x,local.z);
                map.fogGrid.Unfog(c);map.areaManager.Home[c]=true;
                map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
                if(local.x!=50&&local.x!=64&&local.z!=50&&local.z!=64)continue;
                foreach(var thing in c.GetThingList(map).ToList())
                    if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),c,map);
            }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("temperature-fixture-has-three-distinct-enclosed-rooms",
            Enumerable.Range(0,3).Select(TemperatureRoom).Distinct().Count()==3&&
            Enumerable.Range(0,3).All(l=>!TemperatureRoom(l).UsesOutdoorTemperature));
        pawn.jobs.StopAll();pawn.drafter.Drafted=false;TemperatureNeeds(pawn);
        temperaturePhase=0;TemperatureClimate();
        var range=pawn.ComfortableTemperatureRange();
        Check("temperature-query-finds-third-floor-with-no-local-refuge",TemperatureQuery(pawn,pawn,range)?.Room==TemperatureRoom(2));
        // Same-floor native success must not be reordered by the added search.
        TemperatureRoom(0).Temperature=21;
        Check("temperature-keeps-native-local-refuge",TemperatureQuery(pawn,pawn,range)?.Room==TemperatureRoom(0));
        TemperatureClimate();
        var upper=map.listerThings.AllThings.OfType<Building_Stairs>().Single(s=>owner.TryDecode(s.Position,out var c)&&c.LayerId==1);
        upper.SetForbidden(true);
        Check("temperature-rejects-forbidden-sole-connector",TemperatureQuery(pawn,pawn,range)==null);
        upper.SetForbidden(false);
        var blocker=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),upper.UpperCell,map);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();TemperatureClimate();
        Check("temperature-rejects-blocked-landing",TemperatureQuery(pawn,pawn,range)==null);
        blocker.Destroy(DestroyMode.Vanish);map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();TemperatureClimate();
        map.areaManager.TryMakeNewAllowed(out var restricted);
        foreach(var local in new CellRect(51,51,13,13))restricted[At(0,local.x,local.z)]=true;
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap=restricted;
        Check("temperature-respects-protected-pawn-allowed-area",TemperatureQuery(pawn,pawn,range)==null);
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap=null;
        var animal=PawnGenerator.GeneratePawn(PawnKindDefOf.Muffalo,Faction.OfPlayer);
        GenSpawn.Spawn(animal,At(0,55,55),map);
        Check("temperature-does-not-let-unroped-roamer-escape",animal.FenceBlocked&&TemperatureQuery(animal,animal,range)==null);
        animal.Destroy(DestroyMode.Vanish);
        TemperatureRoom(2).Temperature=65;
        Check("temperature-no-refuge-returns-null",TemperatureQuery(pawn,pawn,range)==null);
        TemperatureClimate();
        Check("temperature-climate-really-outside-comfort",!range.Includes(pawn.AmbientTemperature));
        // Native default requires Serious temperature injury; merely uncomfortable pawns keep working.
        TemperatureInjury(HediffDefOf.Heatstroke);
        Check("temperature-native-serious-injury-trigger",pawn.health.hediffSet.HasTemperatureInjury(TemperatureInjuryStage.Serious));
        // Let the real Humanlike tree select GotoSafeTemperature. No direct job injection.
        pawn.drafter.Drafted=false;pawn.jobs.StopAll();
        temperatureJobSeen=false;stage=540;ticks=0;
    }

    private static void TemperatureInjury(HediffDef? def)
    {
        foreach(var h in pawn.health.hediffSet.hediffs.Where(h=>h.def==HediffDefOf.Heatstroke||h.def==HediffDefOf.Hypothermia).ToList())pawn.health.RemoveHediff(h);
        if(def!=null){var injury=HediffMaker.MakeHediff(def,pawn);injury.Severity=0.4f;pawn.health.AddHediff(injury);}
    }
    private static void SaveTemperature()
    {
        GameDataSaveLoader.SaveGame("RimLayers_Temperature");
        File.WriteAllText(Path.Combine(directory,"temperature-baseline.json"),JsonSerializer.Serialize(new{
            pawn=pawn.ThingID,baby=temperatureBaby?.ThingID,phase=temperaturePhase,
            job=pawn.CurJob!.loadID,target=pawn.CurJob.targetA.Thing?.ThingID}));
        temperatureSaved=true;
        Check("temperature-saved-native-job-on-middle-floor",true);
    }
    private static void RestoreTemperature()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"temperature-baseline.json")));
        var b=baseline.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        temperaturePhase=b.GetProperty("phase").GetInt32();
        if(temperaturePhase==2)
        {
            temperatureBaby=pawn.carryTracker.CarriedThing as Pawn;
            Check("temperature-reload-keeps-original-baby-and-holder",temperatureBaby!=null&&
                temperatureBaby.ThingID==b.GetProperty("baby").GetString()&&temperatureBaby.ParentHolder==pawn.carryTracker&&!temperatureBaby.Spawned);
            temperatureCarrySeen=true;
        }
        Check("temperature-reload-keeps-native-job-id",pawn.CurJob?.loadID==b.GetProperty("job").GetInt32());
        Check("temperature-reload-starts-on-middle-floor",owner.TryDecode(pawn.Position,out var c)&&c.LayerId==1);
        temperatureSaved=true;temperatureJobSeen=true;stage=540;ticks=0;
    }
    private static void BeginTemperatureBaby()
    {
        TemperatureInjury(null);
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(0,54,54);pawn.Notify_Teleported();TemperatureNeeds(pawn);
        temperatureBaby=PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist,Faction.OfPlayer,
            forceGenerateNewPawn:true,allowDowned:true,canGeneratePawnRelations:false,fixedBiologicalAge:0.25f,
            fixedChronologicalAge:0.25f,developmentalStages:DevelopmentalStage.Baby,forceBaselinerChance:1));
        GenSpawn.Spawn(temperatureBaby,At(0,55,54),map);TemperatureNeeds(temperatureBaby);
        temperatureBaby.mindState.lastBroughtToSafeTemperatureTick=Find.TickManager.TicksGame-2501;
        temperaturePhase=2;TemperatureClimate();pawn.drafter.Drafted=false;
        Check("temperature-baby-fixture-has-no-bed",RestUtility.FindBedFor(temperatureBaby,pawn,true)==null);
        Check("temperature-baby-needs-remote-refuge",ChildcareUtility.BabyNeedsMovingForTemperatureReasons(temperatureBaby,pawn,out var region)&&region.Room==TemperatureRoom(2));
        // Separate beneficiary restrictions from movement: baby cannot walk this route.
        Check("temperature-search-uses-carrier-not-baby-traversal",temperatureBaby.Downed&&TemperatureQuery(temperatureBaby,pawn,temperatureBaby.ComfortableTemperatureRange())?.Room==TemperatureRoom(2));
        var safe=ChildcareUtility.SafePlaceForBaby(temperatureBaby,pawn);
        Check("temperature-baby-drop-cell-is-on-safe-floor",safe.IsValid&&!safe.HasThing&&owner.TryDecode(safe.Cell,out var c)&&c.LayerId==2);
        var upper=map.listerThings.AllThings.OfType<Building_Stairs>().Single(s=>owner.TryDecode(s.Position,out var pos)&&pos.LayerId==1);
        upper.SetForbidden(true);
        Check("temperature-baby-cannot-use-forbidden-carrier-route",!ChildcareUtility.BabyNeedsMovingForTemperatureReasons(temperatureBaby,pawn,out _));
        upper.SetForbidden(false);
        pawn.drafter.Drafted=false;
        var job=ChildcareUtility.MakeBringBabyToSafetyJob(pawn,temperatureBaby);
        Check("temperature-native-baby-job-created",job!=null);
        if(job==null){Finish();return;}
        pawn.jobs.TryTakeOrderedJob(job);temperatureJobSeen=false;ticks=0;
    }
    private static void TemperatureStep()
    {
        ticks++;TemperatureClimate();
        if(temperaturePhase<2)
        {
            temperatureJobSeen|=pawn.CurJob?.def==JobDefOf.GotoSafeTemperature;
            owner.TryDecode(pawn.Position,out var current);
            if(!ModsConfig.BiotechActive&&!temperatureSaved&&temperaturePhase==0&&current.LayerId==1&&temperatureJobSeen)SaveTemperature();
            int target=temperaturePhase==0?2:0;
            if(current.LayerId==target&&pawn.ComfortableTemperatureRange().Includes(pawn.AmbientTemperature))
            {
                Check(temperaturePhase==0?"temperature-native-ai-escapes-heat-upstairs":"temperature-native-ai-escapes-cold-downstairs",temperatureJobSeen,DescribePawn());
                if(temperaturePhase==0)
                {
                    temperaturePhase=1;temperatureJobSeen=false;ticks=0;TemperatureInjury(HediffDefOf.Hypothermia);
                    pawn.jobs.StopAll();TemperatureNeeds(pawn);return;
                }
                if(ModsConfig.BiotechActive){BeginTemperatureBaby();return;}
                TemperatureFinished();return;
            }
        }
        else
        {
            temperatureJobSeen|=pawn.CurJob?.def==JobDefOf.BringBabyToSafety||pawn.CurJob?.def==JobDefOf.BringBabyToSafetyUnforced;
            temperatureCarrySeen|=pawn.carryTracker.CarriedThing==temperatureBaby;
            if(!temperatureSaved&&pawn.carryTracker.CarriedThing==temperatureBaby&&owner.TryDecode(pawn.Position,out var c)&&c.LayerId==1)SaveTemperature();
            if(temperatureBaby!.Spawned&&owner.TryDecode(temperatureBaby.Position,out var babyCell)&&babyCell.LayerId==2)
            {
                Check("temperature-native-job-carries-and-drops-baby-in-safe-room",temperatureJobSeen&&temperatureCarrySeen&&
                    temperatureBaby.ComfortableTemperatureRange().Includes(temperatureBaby.AmbientTemperature)&&pawn.carryTracker.CarriedThing==null);
                Check("temperature-baby-original-entity-exists-once",map.mapPawns.AllPawnsSpawned.Count(p=>p.ThingID==temperatureBaby.ThingID)==1);
                TemperatureFinished();return;
            }
        }
        if(ticks>5000){Check("temperature-native-job-completes",false,$"phase={temperaturePhase};{DescribePawn()};baby={temperatureBaby?.PositionHeld}");Finish();}
    }
    private static void TemperatureFinished()
    {
        Check("temperature-save-checkpoint-reached",temperatureSaved);
        LayerView.Switch(owner,temperaturePhase==1?0:2);
        Find.CameraDriver.SetRootPosAndSize(At(owner.ViewedLayer,57,57).ToVector3Shifted(),13);
        Find.Selector.ClearSelection();Find.Selector.Select(temperatureBaby??pawn);
        pendingScreenshot=Path.Combine(directory,mode+"-temperature.png");screenshotHold=3;stage=11;ticks=0;
    }
}



