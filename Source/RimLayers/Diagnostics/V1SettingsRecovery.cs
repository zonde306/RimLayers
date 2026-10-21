using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Recovery;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;
using RimWorld.Planet;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static Pawn recoveryPawn=null!,recoveryHostile=null!;
    private static Thing recoveryClaimed=null!;
    private static IDisposable? recoveryClaim;
    private static Thing recoveryItem=null!,recoveryInventory=null!,recoveryCarry=null!,recoveryWeapon=null!;
    private static Corpse recoveryCorpse=null!;
    private static Map recoveryVoid=null!;
    private static int recoveryPhase;
    private static long recoveryAge;
    private static float recoveryFood,recoverySeverity;
    private static EntityRecoveryService RecoveryService=>EntityRecoveryService.Current!;
    private static void ClearRecoveryPatch(Map target,CellRect cells)
    {
        foreach(var cell in cells)
        {
            foreach(var thing in cell.GetThingList(target).ToList())if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
            target.roofGrid.SetRoof(cell,null);target.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);target.fogGrid.Unfog(cell);
        }
        target.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>cells.Contains(c));
    }
    private static void ClearRecoveryColumn(Map target,CellRect cells)
    {
        var o=LayerMapComponent.For(target)!;
        foreach(int layer in o.Layout!.OrderedLayers.Where(l=>o.Elevation(l)>0))
            foreach(var local in cells)
            {
                var cell=o.ToStorage(new(layer,local.x,local.z));
                foreach(var thing in cell.GetThingList(target).ToArray())
                    if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
                target.roofGrid.SetRoof(cell,null);target.terrainGrid.SetTerrain(cell,RimLayersDefOf.RL_OpenAir);
                target.roofCollapseBuffer.CellsMarkedToCollapse.Remove(cell);
            }
        ClearRecoveryPatch(target,cells);
    }
    private static void BeginSettingsRecovery()
    {
        Check("recovery-service-and-settings-defaults-loaded",EntityRecoveryService.Current!=null&&RimLayersMod.Settings.AutoRecovery&&RimLayersMod.Settings.UpperLimit==2);
        foreach(var native in map.mapPawns.FreeColonistsSpawned){native.jobs.StopAll();native.drafter.Drafted=true;}
        ClearRecoveryColumn(map,new CellRect(75,75,24,24));
        CheckRecoveryLandingRules();
        CheckRecoveryExclusions();
        CheckRecoveryConstructionWarnings();
        recoveryHostile=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfAncientsHostile);
        GenSpawn.Spawn(recoveryHostile,At(2,79,96),map);
        recoveryHostile.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture,5000),JobCondition.InterruptForced);
        recoveryClaimed=ThingMaker.MakeThing(ThingDefOf.Steel);GenSpawn.Spawn(recoveryClaimed,At(2,96,79),map);
        recoveryClaim=RecoveryCompatibility.BeginHandling(recoveryClaimed);
        recoveryPawn=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        recoveryWeapon=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_Autopistol"));
        recoveryPawn.equipment.DestroyAllEquipment();recoveryPawn.equipment.AddEquipment((ThingWithComps)recoveryWeapon);
        recoveryInventory=ThingMaker.MakeThing(ThingDefOf.Steel);recoveryInventory.stackCount=17;recoveryPawn.inventory.innerContainer.TryAdd(recoveryInventory,false);
        recoveryCarry=ThingMaker.MakeThing(ThingDefOf.WoodLog);recoveryCarry.stackCount=9;recoveryPawn.carryTracker.TryStartCarry(recoveryCarry);
        recoverySeverity=recoveryPawn.health.hediffSet.hediffs.Sum(h=>h.Severity);
        GenSpawn.Spawn(recoveryPawn,At(2,80,80),map);recoveryPawn.drafter.Drafted=true;
        recoveryItem=ThingMaker.MakeThing(ThingDefOf.Silver);recoveryItem.stackCount=43;GenSpawn.Spawn(recoveryItem,At(2,84,80),map);
        var body=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);GenSpawn.Spawn(body,At(0,88,80),map);body.Kill(null);recoveryCorpse=body.Corpse!;recoveryCorpse.Position=At(2,88,80);
        Check("injection-fixture-has-no-upper-mountain-support",new Thing[]{recoveryPawn,recoveryItem,recoveryCorpse}.All(t=>!RecoveryLanding.HasSupport(owner,t,t.Position)));
        Check("external-air-spawn-is-deferred-not-recreated",recoveryPawn.Spawned&&recoveryPawn.Position==At(2,80,80)&&RecoveryService.Records.Any(r=>r.Thing==recoveryPawn),$"pawn={recoveryPawn.ThingID}; pos={recoveryPawn.Position}; spawned={recoveryPawn.Spawned}; flying={recoveryPawn.Flying}; bed={recoveryPawn.CurrentBed()}; transport={recoveryPawn.BeingTransportedOnGravship}; lease={RecoveryCompatibility.IsClaimed(recoveryPawn)}; excluded={RecoveryCompatibility.Excluded(recoveryPawn,owner)}; transaction={RecoveryCompatibility.TransactionActive}; support={RecoveryLanding.HasSupport(owner,recoveryPawn,recoveryPawn.Position)}");
        int before=RecoveryService.Records.Count;RecoveryService.Notice(recoveryPawn,map);RecoveryService.Notice(recoveryPawn,map);
        Check("duplicate-notifications-have-one-record",RecoveryService.Records.Count==before);
        var leased=ThingMaker.MakeThing(ThingDefOf.Steel);
        using(RecoveryCompatibility.BeginHandling(leased))
        {GenSpawn.Spawn(leased,At(2,90,90),map);RecoveryService.Notice(leased,map);Check("compatibility-lease-keeps-normal-owner",!RecoveryService.Records.Any(r=>r.Thing==leased));}
        leased.Destroy(DestroyMode.Vanish);
        var excluded=ThingMaker.MakeThing(ThingDefOf.WoodLog);RimLayersMod.Settings.RecoveryExcludedDefs.Add(excluded.def.defName);
        GenSpawn.Spawn(excluded,At(2,92,90),map);RecoveryService.Notice(excluded,map);Check("def-exclusion-affects-only-recovery",!RecoveryService.Records.Any(r=>r.Thing==excluded)&&!owner.Structure.HasSurface(excluded.Position));excluded.Destroy(DestroyMode.Vanish);RimLayersMod.Settings.RecoveryExcludedDefs.Clear();
        stage=440;recoveryPhase=0;ticks=0;
    }
    private static void SettingsRecoveryStep()
    {
        if(recoveryPhase==0&&ticks>60)
        {
            Check("pawn-falls-to-nearest-supported-column",recoveryPawn.Position==At(0,80,80));
            Check("hostile-pawn-falls-to-own-column-without-colonist-attractor",recoveryHostile.Spawned&&recoveryHostile.Position==At(0,79,96)&&recoveryHostile.Faction==Faction.OfAncientsHostile);
            Check("external-owner-acquired-after-enqueue-wins-transfer",recoveryClaimed.Spawned&&recoveryClaimed.Position==At(2,96,79)&&!RecoveryService.Records.Any(r=>r.Thing==recoveryClaimed));
            recoveryClaim!.Dispose();recoveryClaim=null;recoveryClaimed.Destroy(DestroyMode.Vanish);recoveryHostile.Destroy(DestroyMode.Vanish);
            Check("item-falls-with-original-identity-and-stack",recoveryItem.Spawned&&recoveryItem.Position==At(0,84,80)&&recoveryItem.stackCount==43,$"pos={recoveryItem.Position}; spawned={recoveryItem.Spawned}; queue={RecoveryService.Records.Any(r=>r.Thing==recoveryItem)}");
            Check("corpse-and-inner-pawn-retained",recoveryCorpse.Spawned&&recoveryCorpse.Position==At(0,88,80)&&recoveryCorpse.InnerPawn.Dead);
            Check("compatibility-fall-adds-no-injury",recoveryPawn.health.hediffSet.hediffs.Sum(h=>h.Severity)<=recoverySeverity+.001f);
            CheckRecoveryContents();
            var safe=recoveryPawn.Position;recoveryPawn.Position=new IntVec3(-100,0,99999);
            Check("illegal-position-setter-does-not-corrupt-grid",recoveryPawn.Position==safe&&safe.GetThingList(map).Contains(recoveryPawn));
            recoveryPawn.Position=At(2,82,82);RimLayersMod.Settings.RecoveryMode=RecoveryPreference.ResetFirst;
            recoveryPhase=1;ticks=0;return;
        }
        if(recoveryPhase==1&&ticks>30)
        {
            Check("safe-reset-prefers-last-valid-position",recoveryPawn.Position==At(0,80,80));
            RimLayersMod.Settings.RecoveryMode=RecoveryPreference.FallThenReset;
            var item=ThingMaker.MakeThing(ThingDefOf.Steel);GenSpawn.Spawn(item,At(0,95,95),map);
            AccessTools.Field(typeof(Thing),"positionInt").SetValue(item,At(2,95,95));
            // Deliberately omit Notice: the shared item scan must discover raw external movement.
            recoveryItem=item;recoveryPhase=2;ticks=0;return;
        }
        // A raw field write has no notification. Allow one bounded item-cursor rotation,
        // not an arbitrary 30 ticks independent of the map's haulable inventory.
        if(recoveryPhase==2&&ticks>30&&(recoveryItem.Position==At(0,95,95)||ticks>Math.Max(120,map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Count/8+16)))
        {
            Check("bypassed-position-notification-repairs-original-registration",recoveryItem.Spawned&&recoveryItem.Position==At(0,95,95)&&At(0,95,95).GetThingList(map).Count(t=>t==recoveryItem)==1,$"ticks={ticks}; haulables={map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Count}; pos={recoveryItem.Position}");
            if(!ModsConfig.OdysseyActive){Check("storage-fixture-requires-odyssey",false);Finish();return;}
            var main=map;
            var tile=Find.WorldGrid.Orbit.Tiles.First(t=>Find.WorldObjects.MapParentAt(t.tile)==null).tile;
            recoveryVoid=MakeUnmanagedMap(tile,120,120);recoveryVoid.Parent.SetFaction(Faction.OfPlayer);
            Current.Game.CurrentMap=main;
            LiveMapExpansion.EnsureElevation(recoveryVoid,1);
            // Native space generator supplies vacuum and no landing surface. A suited resident keeps the test map loaded.
            var resident=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);WearVacuumGear(resident);
            recoveryVoid.terrainGrid.SetTerrain(new(30,0,30),TerrainDefOf.Concrete);
            GenSpawn.Spawn(resident,new IntVec3(30,0,30),recoveryVoid);resident.drafter.Drafted=true;
            recoveryPawn.DeSpawn();GenSpawn.Spawn(recoveryPawn,new IntVec3(40,0,40),recoveryVoid);recoveryPawn.drafter.Drafted=true;
            recoveryPawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture,5000),JobCondition.InterruptForced);
            if(recoveryPawn.carryTracker.CarriedThing!=recoveryCarry)recoveryPawn.carryTracker.TryStartCarry(recoveryCarry);
            Check("active-job-storage-fixture-carries-original-object",recoveryPawn.CurJob!=null&&recoveryPawn.carryTracker.CarriedThing==recoveryCarry);
            recoveryPhase=3;ticks=0;return;
        }
        if(recoveryPhase==3&&ticks>10)
        {
            Check("no-safe-vacuum-landing-stores-original-pawn",RecoveryService.GetDirectlyHeldThings().Contains(recoveryPawn)&&!recoveryPawn.Spawned&&recoveryPawn.Suspended);
            Check("stored-pawn-is-enumerated-and-reference-protected",PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.Contains(recoveryPawn)&&EntityRecoveryService.Contains(recoveryPawn));
            CheckRecoveryContents();
            recoveryAge=recoveryPawn.ageTracker.AgeBiologicalTicks;recoveryFood=recoveryPawn.needs.food.CurLevel;
            RimLayersMod.Settings.AutoRecovery=false;recoveryPhase=4;ticks=0;return;
        }
        if(recoveryPhase==4&&ticks>100&&Find.TickManager.TicksGame>=320)
        {
            Check("storage-uses-native-suspension-without-self-simulation",recoveryPawn.ageTracker.AgeBiologicalTicks==recoveryAge&&Math.Abs(recoveryPawn.needs.food.CurLevel-recoveryFood)<.00001f);
            Check("disabling-auto-keeps-existing-contents",RecoveryService.GetDirectlyHeldThings().Contains(recoveryPawn));
            CheckRecoveryLifetime();
            File.WriteAllText(Path.Combine(directory,"settings-recovery-baseline.json"),JsonSerializer.Serialize(new{pawn=recoveryPawn.ThingID,weapon=recoveryWeapon.ThingID,inventory=recoveryInventory.ThingID,carry=recoveryCarry.ThingID,sourceMap=recoveryVoid.uniqueID,age=recoveryAge,food=recoveryFood}));
            GameDataSaveLoader.SaveGame("RimLayers_SettingsRecovery");
            Check("native-save-completed-with-suspended-entity",File.Exists(Path.Combine(directory,"Saves/RimLayers_SettingsRecovery.rws")));
            Finish();return;
        }
        if(recoveryPhase==5&&ticks>50)
        {
            Check("reload-retains-suspended-pawn-and-pause",RecoveryService.GetDirectlyHeldThings().Contains(recoveryPawn)&&recoveryPawn.Suspended&&recoveryPawn.ageTracker.AgeBiologicalTicks==recoveryAge&&Math.Abs(recoveryPawn.needs.food.CurLevel-recoveryFood)<.00001f);
            var record=RecoveryService.Records.Single(r=>r.Thing==recoveryPawn);
            var plainItem=ThingMaker.MakeThing(ThingDefOf.Silver);plainItem.stackCount=11;
            RecoveryService.CaptureInvalidSpawn(plainItem,recoveryVoid,new IntVec3(-2,0,-2),Rot4.North);
            var plainRecord=RecoveryService.Records.Single(r=>r.Thing==plainItem);plainRecord.ManualOnly=true;
            Current.Game.DeinitAndRemoveMap(recoveryVoid,false);
            var nativeTile=Find.WorldGrid.Tiles.First(t=>t.PrimaryBiome==BiomeDefOf.TemperateForest&&TileFinder.IsValidTileForNewSettlement(t.tile)).tile;
            var nativeMap=MakeUnmanagedMap(nativeTile,120,120);nativeMap.Parent.SetFaction(Faction.OfPlayer);Current.Game.CurrentMap=map;
            ClearRecoveryPatch(nativeMap,new CellRect(18,18,5,5));var nativeCell=new IntVec3(20,0,20);nativeMap.areaManager.Home[nativeCell]=true;
            Check("ordinary-native-map-release-still-requires-cross-map-confirmation",!RecoveryService.CanRelease(plainRecord,nativeMap,nativeCell).Accepted);
            Check("confirmed-release-to-native-base-map-does-not-create-layers",RecoveryService.TryRelease(plainRecord,nativeMap,nativeCell,true)&&plainItem.Spawned&&plainItem.Position==nativeCell&&plainItem.stackCount==11&&plainItem.holdingOwner==nativeMap.spawnedThings&&LayerMapComponent.For(nativeMap)==null&&nativeMap.Size==new IntVec3(120,1,120));
            Check("map-removal-keeps-original-entity-in-game-owner",RecoveryService.OriginalMap(record)==null&&RecoveryService.GetDirectlyHeldThings().Contains(recoveryPawn));
            var target=At(0,80,80);
            Check("cross-map-release-requires-explicit-confirmation",!RecoveryService.CanRelease(record,map,target).Accepted);
            Check("confirmed-cross-map-release-retains-original-entity",RecoveryService.TryRelease(record,map,target,true)&&recoveryPawn.Spawned&&recoveryPawn.Map==map&&recoveryPawn.Position==target);
            CheckRecoveryContents();
            Check("release-establishes-single-owner",recoveryPawn.holdingOwner==map.spawnedThings&&!RecoveryService.GetDirectlyHeldThings().Contains(recoveryPawn)&&!RecoveryService.Records.Any(r=>r.Thing==recoveryPawn));
            RimLayersMod.Settings.AutoRecovery=true;recoveryPawn.Position=At(2,80,80);recoveryPhase=6;ticks=0;return;
        }
        if(recoveryPhase==6&&ticks>20)
        {
            var record=RecoveryService.Records.SingleOrDefault(r=>r.Thing==recoveryPawn);
            Check("repeated-external-relocation-pauses-in-persistent-holder",record is {ManualOnly:true,Reason:"RepeatedMovement"}&&RecoveryService.GetDirectlyHeldThings().Contains(recoveryPawn));
            CheckRecoveryContents();
            if(record==null){Finish();return;}
            RecoveryService.Retry(record);recoveryPhase=7;ticks=0;return;
        }
        if(recoveryPhase==7&&ticks>20)
        {
            Check("manual-retry-clears-loop-breaker-and-places-original-once",recoveryPawn.Spawned&&owner.IsGroundCell(recoveryPawn.Position)&&!RecoveryService.Records.Any(r=>r.Thing==recoveryPawn)&&recoveryPawn.holdingOwner==map.spawnedThings);
            CheckRecoveryContents();
            RimLayersMod.Settings.AutoRecovery=false;
            var ignored=ThingMaker.MakeThing(ThingDefOf.Steel);GenSpawn.Spawn(ignored,At(2,94,94),map);
            Check("disabled-auto-stops-only-new-capture",ignored.Spawned&&!RecoveryService.Records.Any(r=>r.Thing==ignored));ignored.Destroy(DestroyMode.Vanish);
            RimLayersMod.Settings.AutoRecovery=true;
            BeginRecoveryFault();recoveryPhase=8;ticks=0;return;
        }
        if(recoveryPhase==8&&ticks>20)
        {
            Check("one-entity-exception-does-not-stop-other-recoveries",recoveryFaultGood.Spawned&&owner.IsGroundCell(recoveryFaultGood.Position)&&RecoveryService.Records.Any(r=>r.Thing==recoveryFaultBad&&r.ManualOnly)&&!recoveryFaultBad.Destroyed);
            recoveryFaultHook!.UnpatchAll(recoveryFaultHook.Id);recoveryFaultHook=null;
            RecoveryService.Retry(RecoveryService.Records.Single(r=>r.Thing==recoveryFaultBad));recoveryPhase=9;ticks=0;return;
        }
        if(recoveryPhase==9&&ticks>20)
        {
            Check("retry-after-native-failure-preserves-one-original-owner",recoveryFaultBad.Spawned&&owner.IsGroundCell(recoveryFaultBad.Position)&&recoveryFaultBad.holdingOwner==map.spawnedThings&&!RecoveryService.Records.Any(r=>r.Thing==recoveryFaultBad));
            Finish();return;
        }
        if(ticks>1000)throw new Exception("Recovery test phase stalled: "+recoveryPhase);
    }
    private static void CheckRecoveryLandingRules()
    {
        var item=ThingMaker.MakeThing(ThingDefOf.Steel);
        var source=At(2,100,100);var contact=At(1,100,100);var ground=At(0,100,100);
        map.roofGrid.SetRoof(ground,RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(contact,RimLayersDefOf.RL_StructuralFloor);
        var first=RecoveryLanding.FallCandidates(owner,item,source).ToArray();
        Check("fall-candidates-stop-at-first-supported-plane",first.Length>0&&first.All(c=>owner.TryDecode(c,out var d)&&d.LayerId==1));
        var block=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),contact,map);
        Check("first-contact-blockage-does-not-resume-fall-below",!RecoveryLanding.Legal(owner,item,contact)&&RecoveryLanding.FallCandidates(owner,item,source).All(c=>owner.TryDecode(c,out var d)&&d.LayerId==1));
        block.Destroy(DestroyMode.Vanish);
        map.roofGrid.SetRoof(contact,RoofDefOf.RoofConstructed);
        Check("roof-contact-does-not-place-under-roof",!RecoveryLanding.FallCandidates(owner,item,source).Any());
        map.roofGrid.SetRoof(contact,null);
        var rock=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Granite")),contact,map);
        Check("rock-contact-cannot-be-a-landing-cell",!RecoveryLanding.Legal(owner,item,contact)&&RecoveryLanding.ContactElevation(owner,source)==1);
        rock.Destroy(DestroyMode.Vanish);
        var invalid=new RecoveryRecord{Thing=item,Origin=new IntVec3(-1,0,-1)};
        Check("illegal-coordinate-does-not-infer-fall-layer",RecoveryLanding.Candidates(owner,item,invalid).Take(10).All(c=>c.Stage!=RecoveryStage.Falling));
        var occupant=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        GenSpawn.Spawn(occupant,At(0,97,97),map);occupant.drafter.Drafted=true;
        var visitor=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        Check("pawn-landing-does-not-displace-occupant",!RecoveryLanding.Legal(owner,visitor,occupant.Position));
        using(RecoveryCompatibility.BeginHandling(occupant))
            Check("temporary-owner-excludes-pawn",RecoveryCompatibility.Excluded(occupant,owner));
        occupant.Destroy(DestroyMode.Vanish);visitor.Destroy(DestroyMode.Vanish);
        AccessTools.Field(typeof(Thing),"beingTransportedOnGravship").SetValue(item,true);
        Check("native-gravship-transport-state-excluded",RecoveryCompatibility.Excluded(item,owner));
        AccessTools.Field(typeof(Thing),"beingTransportedOnGravship").SetValue(item,false);
        var footprint=ThingMaker.MakeThing(ThingDefOf.Steel);
        footprint.def=(ThingDef)AccessTools.Method(typeof(object),"MemberwiseClone").Invoke(footprint.def,null)!;footprint.def.size=new IntVec2(2,2);
        Check("multi-cell-item-footprint-cannot-cross-layer-boundary",!RecoveryLanding.Legal(owner,footprint,At(0,119,119)));
        var blockedCell=At(0,97,96);var temporaryWall=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),blockedCell,map);
        Check("temporary-path-blockage-is-not-unsupported-air",RecoveryLanding.HasSupport(owner,item,blockedCell)&&!RecoveryLanding.Legal(owner,item,blockedCell));
        temporaryWall.Destroy(DestroyMode.Vanish);
        item.Destroy(DestroyMode.Vanish);
        map.terrainGrid.SetTerrain(contact,RimLayersDefOf.RL_OpenAir);map.roofGrid.SetRoof(ground,null);
        owner.StructuralCells.Remove(contact);owner.PendingCollapse.Remove(contact);
    }
    private static void CheckRecoveryExclusions()
    {
        var chicken=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.AllDefs.First(k=>k.race.defName=="Chicken"));
        using(RecoveryCompatibility.BeginHandling(chicken))GenSpawn.Spawn(chicken,At(2,78,78),map);
        chicken.flight.StartFlying();RecoveryService.Notice(chicken,map);
        Check("native-flying-pawn-is-not-taken-from-flight",chicken.Flying&&RecoveryCompatibility.Excluded(chicken,owner)&&!RecoveryService.Records.Any(r=>r.Thing==chicken));
        chicken.Destroy(DestroyMode.Vanish);
        foreach(string name in new[]{"PawnFlyer","DropPodIncoming","ActiveDropPod"})
        {
            var carrier=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(name));
            Check("native-carrier-excluded-"+name,RecoveryCompatibility.Excluded(carrier,owner));
        }
        var sleeper=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        var bed=(Building_Bed)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Bed"),ThingDefOf.WoodLog);bed.SetFaction(Faction.OfPlayer);
        using(RecoveryCompatibility.BeginHandling(bed))GenSpawn.Spawn(bed,At(2,77,85),map);
        using(RecoveryCompatibility.BeginHandling(sleeper))GenSpawn.Spawn(sleeper,bed.GetSleepingSlotPos(0),map);
        sleeper.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture,500),JobCondition.InterruptForced);
        sleeper.jobs.posture=PawnPosture.LayingInBed;RecoveryService.Notice(sleeper,map);
        Check("native-current-bed-pawn-not-misidentified-as-air",sleeper.CurrentBed()==bed&&!RecoveryService.Records.Any(r=>r.Thing==sleeper));
        sleeper.Destroy(DestroyMode.Vanish);bed.Destroy(DestroyMode.Vanish);
        var container=new RecoveryTestHolder();var content=ThingMaker.MakeThing(ThingDefOf.Silver);container.Things.TryAdd(content,false);
        RecoveryService.Notice(content,map,"Unsupported",At(2,78,88));
        Check("unknown-container-content-retains-original-owner",content.holdingOwner==container.Things&&!RecoveryService.Records.Any(r=>r.Thing==content));container.Things.Remove(content);content.Destroy(DestroyMode.Vanish);
        var wall=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(2,78,90),map);
        RecoveryService.Notice(wall,map);
        var warnings=(Dictionary<int,int>)AccessTools.Field(typeof(EntityRecoveryService),"warnings").GetValue(RecoveryService)!;
        Check("unsupported-building-is-warned-not-automatically-moved",wall.Spawned&&wall.Position==At(2,78,90)&&!RecoveryService.Records.Any(r=>r.Thing==wall)&&warnings.ContainsKey(wall.thingIDNumber));wall.Destroy(DestroyMode.Vanish);
    }
    private static Thing recoveryFaultBad=null!,recoveryFaultGood=null!;
    private static Harmony? recoveryFaultHook;
    private static void RecoveryFault(Thing thing)
    {if(thing==recoveryFaultBad)throw new InvalidOperationException("RimLayers diagnostic per-entity fault");}
    private static void BeginRecoveryFault()
    {
        recoveryFaultBad=ThingMaker.MakeThing(ThingDefOf.Steel);recoveryFaultGood=ThingMaker.MakeThing(ThingDefOf.Silver);
        recoveryFaultHook=new Harmony("rimlayers.diagnostics.recovery-fault");
        recoveryFaultHook.Patch(AccessTools.Method(typeof(RecoveryLanding),"Legal",new[]{typeof(Map),typeof(Thing),typeof(IntVec3)}),prefix:new HarmonyMethod(typeof(V1Integration),nameof(RecoveryFault)));
        GenSpawn.Spawn(recoveryFaultBad,At(2,93,94),map);GenSpawn.Spawn(recoveryFaultGood,At(2,94,94),map);
    }
    private sealed class RecoveryTestHolder:IThingHolder
    {
        internal readonly ThingOwner<Thing> Things;
        internal RecoveryTestHolder()=>Things=new(this);
        public IThingHolder ParentHolder=>null!;
        public ThingOwner GetDirectlyHeldThings()=>Things;
        public void GetChildHolders(List<IThingHolder> children)=>ThingOwnerUtility.AppendThingHoldersFromThings(children,Things);
    }
    private static void CheckRecoveryLifetime()
    {
        var reason=(string?)AccessTools.Method(typeof(WorldPawnGC),"GetCriticalPawnReason").Invoke(new WorldPawnGC(),new object[]{recoveryPawn});
        Check("native-world-gc-recognizes-recovery-owner",reason=="RimLayersRecovery");
        var holder=new RecoveryTestHolder();
        var residents=Find.Maps.SelectMany(m=>m.mapPawns.FreeColonistsSpawned).Select(p=>(Pawn:p,Map:p.Map,Cell:p.Position,Rotation:p.Rotation)).ToArray();
        var ender=Find.GameEnder;bool ending=ender.gameEnding;var faction=recoveryPawn.Faction;
        try
        {
            foreach(var resident in residents){resident.Pawn.DeSpawn();holder.Things.TryAdd(resident.Pawn,false);}
            ender.gameEnding=false;ender.CheckOrUpdateGameOver();
            Check("held-live-free-colonist-prevents-false-game-over",!ender.gameEnding);
            recoveryPawn.SetFaction(Faction.OfAncientsHostile);
            ender.gameEnding=false;ender.CheckOrUpdateGameOver();
            Check("held-noncolonist-does-not-prevent-native-game-over",ender.gameEnding);
        }
        finally
        {
            recoveryPawn.SetFaction(faction);
            foreach(var resident in residents)GenSpawn.Spawn(resident.Pawn,resident.Cell,resident.Map,resident.Rotation);
            ender.gameEnding=ending;
        }
    }
    private static void CheckRecoveryContents()
    {
        Check("equipment-inventory-carry-keep-original-objects",recoveryPawn.equipment.Primary==recoveryWeapon&&recoveryPawn.inventory.innerContainer.Contains(recoveryInventory)&&recoveryInventory.stackCount==17&&recoveryPawn.carryTracker.CarriedThing==recoveryCarry&&recoveryCarry.stackCount==9);
    }
    private static void RestoreSettingsRecovery()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"settings-recovery-baseline.json")));var b=doc.RootElement;
        recoveryPawn=RecoveryService.HeldPawns().Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        recoveryWeapon=recoveryPawn.equipment.Primary;recoveryInventory=recoveryPawn.inventory.innerContainer.Single(t=>t.ThingID==b.GetProperty("inventory").GetString());recoveryCarry=recoveryPawn.carryTracker.CarriedThing;
        recoveryVoid=Find.Maps.Single(m=>m.uniqueID==b.GetProperty("sourceMap").GetInt32());
        recoveryAge=b.GetProperty("age").GetInt64();recoveryFood=b.GetProperty("food").GetSingle();
        Check("native-load-preserves-all-original-identities",recoveryWeapon.ThingID==b.GetProperty("weapon").GetString()&&recoveryCarry.ThingID==b.GetProperty("carry").GetString());
        CheckRecoveryContents();stage=440;recoveryPhase=5;ticks=0;
    }
}
