using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn hazardPawn=null!;
    private static Thing hazardSource=null!;
    private static IntVec3 hazardStart;
    private static float hazardInjuries;
    private static bool hazardWaveSeen,hazardOriginalCovered,hazardSprintSeen;
    private static long hazardFireAttempts;
    private static int hazardClockStart,hazardWickBudget;
    private static bool TimedHazards=>scenario=="hazards-timed";
    private static float InjurySum(Pawn p)=>p.health.hediffSet.hediffs.OfType<Hediff_Injury>().Sum(h=>h.Severity);
    private static void BeginHazards()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.Position=At(0,40,40);pawn.Notify_Teleported();
        hazardPawn=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn && !p.Downed && p.health.capacities.GetLevel(PawnCapacityDefOf.Hearing)>0);
        hazardPawn.jobs.StopAll();hazardPawn.drafter.Drafted=true;
        hazardPawn.Position=At(1,53,52);hazardPawn.Notify_Teleported();
        var fire=(Fire)GenSpawn.Spawn(ThingDefOf.Fire,At(0,52,52),map);fire.fireSize=1.8f;fire.instigator=pawn;
        var door=(Building_Door)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Door,ThingDefOf.Steel),At(1,52,52),map);
        Check("closed-door-blocks-vertical-flame-channel",!owner.Fire.Targets(fire,1).Any());
        var sealedFire=(Fire)GenSpawn.Spawn(ThingDefOf.Fire,At(0,58,58),map);sealedFire.fireSize=1.8f;
        Check("solid-slab-blocks-vertical-flame",!owner.Fire.Targets(sealedFire,1).Any());sealedFire.Destroy(DestroyMode.Vanish);
        Check("storage-gap-cannot-start-static-fire",FireUtility.ChanceToStartFireIn(new IntVec3(60,0,owner.Layout!.Height),map)==0);
        AccessTools.Method(typeof(Building_Door),"DoorOpen").Invoke(door,new object[]{10000});
        var firePermission=RimLayersDefOf.RL_Stairs.GetModExtension<ConnectorTransmission>();firePermission.fire=false;
        try { Check("flame-permission-independent-from-air",owner.Air.IsOpen(At(0,52,52),At(1,52,52)) && !owner.Fire.Targets(fire,1).Any()); }
        finally { firePermission.fire=true; }
        Thing fuel=ThingMaker.MakeThing(ThingDefOf.WoodLog);fuel.stackCount=75;GenSpawn.Spawn(fuel,At(1,52,52),map);
        Rand.PushState(184973);
        try { for(int i=0;i<100 && !At(1,52,52).ContainsStaticFire(map);i++)AccessTools.Method(typeof(Fire),"TrySpread").Invoke(fire,null); }
        finally { Rand.PopState(); }
        Fire upper=At(1,52,52).GetThingList(map).OfType<Fire>().Single();
        Check("native-spread-creates-real-upper-fire-once",upper!=fire && upper.instigator==pawn && At(1,52,52).GetThingList(map).OfType<Fire>().Count()==1);
        fire.Destroy(DestroyMode.Vanish);upper.fireSize=1.8f;
        Rand.PushState(184974);
        try { for(int i=0;i<2000 && !At(0,52,52).ContainsStaticFire(map);i++)AccessTools.Method(typeof(Fire),"TrySpread").Invoke(upper,null); }
        finally { Rand.PopState(); }
        Check("embers-can-spread-down-authorized-channel",At(0,52,52).GetThingList(map).OfType<Fire>().Any(f=>f.instigator==pawn));
        foreach(Fire f in map.listerThings.ThingsOfDef(ThingDefOf.Fire).OfType<Fire>().ToList())f.Destroy(DestroyMode.Vanish);
        foreach(Thing spark in map.listerThings.ThingsOfDef(ThingDefOf.Spark).ToList())spark.Destroy(DestroyMode.Vanish);
        fuel.Destroy(DestroyMode.Vanish);
        GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(1,54,52),map);
        pawn.Position=At(0,52,52);pawn.Notify_Teleported();
        var other=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn && p!=hazardPawn);
        other.jobs.StopAll();other.drafter.Drafted=true;other.Position=At(2,54,52);other.Notify_Teleported();
        foreach(IntVec3 c in new CellRect(51,51,3,3))
            if(c.x!=52 || c.z!=52)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(0,c.x,c.z),map);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var heard=new Dictionary<Pawn,int>();
        void Clamor(float radius) { heard.Clear();GenClamor.DoClamor(pawn,pawn.Position,radius,(s,p)=>heard[p]=heard.GetValueOrDefault(p)+1); }
        Clamor(10);
        var acoustic=owner.Sound.Distances(pawn.Position,10);
        Check("acoustic-graph-reaches-two-upper-floors",acoustic.TryGetValue(hazardPawn.Position,out float one) && Math.Abs(one-4)<0.0001 &&
            acoustic.TryGetValue(other.Position,out float two) && Math.Abs(two-8)<0.0001);
        float Hearing(Pawn p)=>Math.Clamp(p.health.capacities.GetLevel(PawnCapacityDefOf.Hearing),0,1);
        Check("native-clamor-retains-hearing-and-one-callback-per-listener",
            heard.GetValueOrDefault(hazardPawn)==(Hearing(hazardPawn)*10>=4?1:0) &&
            heard.GetValueOrDefault(other)==(Hearing(other)*10>=8?1:0) && heard.GetValueOrDefault(pawn)==(Hearing(pawn)>0?1:0),
            $"hearing={Hearing(pawn)},{Hearing(hazardPawn)},{Hearing(other)}; callbacks={heard.GetValueOrDefault(pawn)},{heard.GetValueOrDefault(hazardPawn)},{heard.GetValueOrDefault(other)}");
        Clamor(2);Check("sound-budget-includes-physical-floor-height",!heard.ContainsKey(hazardPawn));
        AccessTools.Method(typeof(Building_Door),"DoorTryClose").Invoke(door,null);Clamor(10);
        Check("closed-door-stops-remote-clamor",!door.Open && !heard.ContainsKey(hazardPawn) && !heard.ContainsKey(other),$"doorOpen={door.Open}, listeners={heard.Count}");
        AccessTools.Method(typeof(Building_Door),"DoorOpen").Invoke(door,new object[]{10000});
        var stairs=At(0,52,52).GetThingList(map).OfType<RimLayers.Structure.Building_Stairs>().Single();stairs.SetForbidden(true);Clamor(10);
        Check("pawn-forbiddance-does-not-silence-stairs",owner.Sound.Distances(pawn.Position,10).ContainsKey(hazardPawn.Position) &&
            heard.GetValueOrDefault(hazardPawn)==(Hearing(hazardPawn)*10>=4?1:0));stairs.SetForbidden(false);
        var permission=RimLayersDefOf.RL_Stairs.GetModExtension<ConnectorTransmission>();permission.sound=false;
        try { Clamor(10);Check("sound-permission-independent-from-air",owner.Air.IsOpen(At(0,52,52),At(1,52,52)) && !owner.Sound.Distances(pawn.Position,10).ContainsKey(hazardPawn.Position) && !heard.ContainsKey(hazardPawn)); }
        finally { permission.sound=true; }
        hazardSource=GenSpawn.Spawn(ThingDefOf.Chemfuel,At(0,52,52),map);hazardSource.stackCount=100;
        hazardPawn.drafter.Drafted=false;hazardPawn.mindState.knownExploder=null;
        GenExplosion.NotifyNearbyPawnsOfDangerousExplosive(hazardSource,DamageDefOf.Flame,Faction.OfPlayer,pawn);
        Check("solid-floor-blocks-warning-despite-acoustic-channel",hazardPawn.mindState.knownExploder==null);
        var underside=(Projectile_Explosive)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Proj_GrenadeFrag"),pawn.Position,map);
        underside.Launch(pawn,pawn.DrawPos,At(1,52,52),At(1,52,52),ProjectileHitFlags.All);
        for(int i=0;i<200 && CombatService.Flight(underside) is {ImpactCommitted:false};i++)
            AccessTools.Method(typeof(Projectile_Explosive),"TickInterval").Invoke(underside,new object[]{1});
        Check("slab-underside-impact-warning-stays-on-impact-side",CombatService.Flight(underside) is {ImpactCommitted:true} hit &&
            Math.Abs(hit.ImpactPoint.y-3)<0.001f && owner.TryDecode(underside.Position,out var impactLayer) && impactLayer.LayerId==0 &&
            !owner.Warnings.Exposed(underside,hazardPawn.Position) && hazardPawn.mindState.knownExploder==null,
            $"point={CombatService.Flight(underside)?.ImpactPoint}, position={underside.Position}");
        AccessTools.Method(typeof(Projectile_Explosive),"Explode").Invoke(underside,null);
        var undersideBlast=owner.Combat.Blasts.Single(b=>!b.Explosion.Destroyed);
        Check("underside-detonation-does-not-teleport-above-slab",undersideBlast.Origin.y<3 &&
            !BlastAccess.Cells(undersideBlast.Explosion).Contains(hazardPawn.Position),$"origin={undersideBlast.Origin}");
        // Coverage is frozen now; this subcase isolates the blast-side contract from later destruction.
        undersideBlast.Explosion.Destroy(DestroyMode.Vanish);
        hazardSource.Destroy(DestroyMode.Vanish);door.Destroy(DestroyMode.Vanish);
        // A supported balcony with a real open shaft gives the impending blast a clear path up.
        for(int x=64;x<=82;x++)for(int z=64;z<=76;z++)
        {
            IntVec3 ground=At(0,x,z),up=At(1,x,z);
            foreach(Thing t in ground.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(ground,TerrainDefOf.Concrete);map.roofGrid.SetRoof(ground,RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(up,RimLayersDefOf.RL_StructuralFloor);
            if((x-64)%6==0 && (z-64)%6==0)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),ground,map);
        }
        map.roofGrid.SetRoof(At(0,70,70),null);map.terrainGrid.SetTerrain(At(1,70,70),RimLayersDefOf.RL_OpenAir);
        // The hot plume can reach fuel on the adjacent supported rim, not a floating fire in air.
        fire=(Fire)GenSpawn.Spawn(ThingDefOf.Fire,At(0,70,70),map);fire.fireSize=1.8f;
        Check("open-shaft-plume-can-reach-adjacent-supported-rim",owner.Fire.Targets(fire,1).Contains(At(1,71,70)) && FireUtility.ChanceToStartFireIn(At(1,70,70),map)==0);
        fire.Destroy(DestroyMode.Vanish);
        var woodWall=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.WoodLog),At(0,69,70),map);
        pawn.Position=At(1,71,70);pawn.Notify_Teleported();
        var fallingSpark=(Spark)GenSpawn.Spawn(ThingDefOf.Spark,pawn.Position,map);
        fallingSpark.Launch(pawn,pawn.DrawPos,woodWall,woodWall,ProjectileHitFlags.All);
        for(int i=0;i<200 && !fallingSpark.Destroyed;i++)AccessTools.Method(typeof(Projectile),"TickInterval").Invoke(fallingSpark,new object[]{1});
        Check("cross-floor-spark-ignites-hit-wall-not-approach-cell",fallingSpark.Destroyed && At(0,69,70).ContainsStaticFire(map) && !At(0,70,70).ContainsStaticFire(map));
        foreach(Fire f in At(0,69,70).GetThingList(map).OfType<Fire>().ToList())f.Destroy(DestroyMode.Vanish);
        woodWall.Destroy(DestroyMode.Vanish);
        var multi=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("FueledStove")),At(0,69,70),map);
        RimLayers.Environment.SparkImpactPatch.Ignite(fallingSpark.Position,map,0.1f,pawn,null!,fallingSpark,multi);
        Check("multicell-spark-ignition-keeps-contact-edge-not-anchor",At(0,70,70).ContainsStaticFire(map) && !At(0,69,70).ContainsStaticFire(map));
        foreach(IntVec3 c in multi.OccupiedRect())foreach(Fire f in c.GetThingList(map).OfType<Fire>().ToList())f.Destroy(DestroyMode.Vanish);
        multi.Destroy(DestroyMode.Vanish);
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>owner.TryDecode(c,out var local) && local.X>=64 && local.X<=82 && local.Z>=64 && local.Z<=76);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        hazardStart=At(1,71,70);hazardPawn.jobs.StopAll();hazardPawn.Position=hazardStart;hazardPawn.Notify_Teleported();
        pawn.Position=At(0,40,40);pawn.Notify_Teleported();other.Position=At(2,54,54);other.Notify_Teleported();
        hazardSource=GenSpawn.Spawn(ThingDefOf.Chemfuel,At(0,69,70),map);hazardSource.stackCount=100;
        Check("warning-fixture-has-three-dimensional-blast-line",owner.Warnings.Exposed(hazardSource,hazardStart));
        hazardPawn.mindState.knownExploder=null;
        GenExplosion.NotifyNearbyPawnsOfDangerousExplosive(hazardSource,DamageDefOf.Flame,Faction.OfMechanoids,pawn);
        Check("warning-preserves-faction-filter",hazardPawn.mindState.knownExploder==null);
        hazardPawn.drafter.Drafted=true;
        GenExplosion.NotifyNearbyPawnsOfDangerousExplosive(hazardSource,DamageDefOf.Flame,Faction.OfPlayer,pawn);
        Check("drafted-pawn-does-not-auto-flee",hazardPawn.mindState.knownExploder==null);
        hazardPawn.drafter.Drafted=false;
        var wait=JobMaker.MakeJob(JobDefOf.Wait);wait.expiryInterval=5000;wait.playerForced=true;
        hazardPawn.jobs.TryTakeOrderedJob(wait);
        Check("fixture-player-command-is-active-before-warning",hazardPawn.CurJob?.playerForced==true,$"job={hazardPawn.CurJob}, queued={hazardPawn.jobs.jobQueue.Count}, waitForced={wait.playerForced}");
        GenExplosion.NotifyNearbyPawnsOfDangerousExplosive(hazardSource,DamageDefOf.Flame,Faction.OfPlayer,pawn);
        Check("warning-does-not-preempt-player-forced-job",hazardPawn.CurJob==wait && wait.playerForced,$"job={hazardPawn.CurJob}, wait={wait}, forced={hazardPawn.CurJob?.playerForced}");
        hazardPawn.jobs.StopAll();hazardPawn.mindState.knownExploder=null;
        var comp=hazardSource.TryGetComp<CompExplosive>();comp.customExplosiveRadius=4;comp.StartWick(pawn);
        hazardWickBudget=comp.wickTicksLeft;hazardClockStart=Find.TickManager.TicksGame;
        if(TimedHazards)Check("uses-untouched-native-wick-budget",hazardWickBudget>=70 && hazardWickBudget<=150,$"ticks={hazardWickBudget}");
        else comp.wickTicksLeft=600;
        Check("native-warning-remembers-real-cross-floor-exploder",hazardPawn.mindState.knownExploder==hazardSource,$"radius={comp.ExplosiveRadius()}");
        var scheduledFuel=ThingMaker.MakeThing(ThingDefOf.WoodLog);scheduledFuel.stackCount=75;GenSpawn.Spawn(scheduledFuel,At(0,54,54),map);
        var scheduledFire=(Fire)GenSpawn.Spawn(ThingDefOf.Fire,At(0,54,54),map);scheduledFire.fireSize=1.8f;
        hazardInjuries=InjurySum(hazardPawn);
        GameDataSaveLoader.SaveGame("RimLayers_V1Hazards");
        File.WriteAllText(Path.Combine(directory,"hazards-baseline.json"),JsonSerializer.Serialize(new {pawn=hazardPawn.ThingID,source=hazardSource.ThingID,injuries=hazardInjuries,wick=comp.wickTicksLeft,savedTick=hazardClockStart}));
        hazardFireAttempts=owner.Fire.Attempts;stage=100;ticks=0;
    }
    private static void RestoreHazards()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"hazards-baseline.json")));var b=baseline.RootElement;
        hazardPawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        hazardSource=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("source").GetString());
        hazardStart=At(1,71,70);hazardInjuries=b.GetProperty("injuries").GetSingle();
        Check("restart-keeps-native-active-wick-without-restart",hazardSource.TryGetComp<CompExplosive>().wickTicksLeft==b.GetProperty("wick").GetInt32());
        owner.Warnings.Tick();
        Check("restart-rebuilds-known-danger-before-simulation",hazardPawn.mindState.knownExploder==hazardSource);
        Check("warning-rebuild-does-not-reset-wick",hazardSource.TryGetComp<CompExplosive>().wickTicksLeft==b.GetProperty("wick").GetInt32());
        if(TimedHazards){hazardWickBudget=b.GetProperty("wick").GetInt32();hazardClockStart=b.GetProperty("savedTick").GetInt32();}
        hazardFireAttempts=owner.Fire.Attempts;stage=100;ticks=0;
    }
    private static void HazardsStep()
    {
        if(stage==100 && hazardPawn.CurJob?.def==JobDefOf.Goto && hazardPawn.CurJob.locomotionUrgency==LocomotionUrgency.Sprint)hazardSprintSeen=true;
        if(stage==100 && TimedHazards && hazardSource.Destroyed)
        {
            Check("native-warning-drives-real-sprint-job",hazardSprintSeen);
            Check("pawn-actually-moves-before-unmodified-detonation",hazardPawn.Position!=hazardStart,$"position={hazardPawn.Position}");
            Check("detonation-occurs-on-original-native-wick-deadline",Find.TickManager.TicksGame-hazardClockStart==hazardWickBudget,
                $"budget={hazardWickBudget}, elapsed={Find.TickManager.TicksGame-hazardClockStart}");
            stage=101;ticks=0;
        }
        else if(stage==100 && !TimedHazards && owner.Warnings.DistanceSquared(hazardSource,hazardPawn.Position)>=81)
        {
            Check("native-warning-drives-real-sprint-job",hazardSprintSeen);
            Check("pawn-actually-runs-out-of-cross-floor-blast-danger",hazardPawn.Position!=hazardStart && !hazardPawn.Dead,$"position={hazardPawn.Position}");
            hazardSource.TryGetComp<CompExplosive>().wickTicksLeft=1;stage=101;ticks=0;
        }
        else if(stage==101)
        {
            foreach(var blast in owner.Combat.Blasts.Where(b=>!b.Explosion.Destroyed))
            { hazardWaveSeen=true;hazardOriginalCovered|=BlastAccess.Cells(blast.Explosion).Contains(hazardStart); }
            if(ticks>30 && hazardSource.Destroyed && !owner.Combat.Blasts.Any(b=>!b.Explosion.Destroyed))
            {
                Check("real-native-explosion-would-cover-old-upper-position",hazardWaveSeen && hazardOriginalCovered);
                Check("escaped-pawn-is-unharmed-after-actual-detonation",!hazardPawn.Dead && InjurySum(hazardPawn)==hazardInjuries);
                hazardPawn.drafter.Drafted=true;stage=102;ticks=0;
            }
        }
        else if(stage==102 && ticks>200)
        {
                Check("real-map-ticks-schedule-native-fire-spread",owner.Fire.Attempts>hazardFireAttempts,$"attempts={owner.Fire.Attempts-hazardFireAttempts}");
                Check("escaped-pawn-remains-safe-through-followup-ticks",!FireUtility.IsBurning(hazardPawn) && InjurySum(hazardPawn)==hazardInjuries);
                LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.Selector.Select(hazardPawn);
                Find.CameraDriver.SetRootPosAndSize(hazardPawn.Position.ToVector3Shifted(),10);
                pendingScreenshot=Path.Combine(directory,mode+"-hazards.png");screenshotHold=3;stage=11;ticks=0;
        }
        if(ticks>400) { Check("cross-floor-flee-and-detonation-complete",false,$"stage={stage},pawn={hazardPawn.Position},job={hazardPawn.CurJob}");Finish(); }
    }
}
