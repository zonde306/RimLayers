using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn grenadeEscaper=null!;
    private static Projectile_Explosive escapingGrenade=null!;
    private static IntVec3 grenadeOldCell;
    private static int grenadeClock,grenadeBudget;
    private static float grenadeInjuries;
    private static bool grenadeSprint,grenadeCovered,grenadeWave;
    private static readonly AccessTools.FieldRef<Projectile_Explosive,int> GrenadeTimer=AccessTools.FieldRefAccess<Projectile_Explosive,int>("ticksToDetonation");

    private static void BeginRoofCombat()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        for(int x=65;x<=77;x++)for(int z=58;z<=62;z++)
        {
            IntVec3 c=At(0,x,z);
            foreach(Thing t in c.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.roofGrid.SetRoof(c,null);
        }
        for(int x=65;x<=69;x++)for(int z=59;z<=61;z++)map.roofGrid.SetRoof(At(1,x,z),RoofDefOf.RoofConstructed);
        pawn.Position=At(0,75,60);pawn.Notify_Teleported();shotTarget=BlastWall(2,64,60);int hp=shotTarget.HitPoints;
        var scene=new LayerShotScene(owner);
        Check("real-roof-without-same-floor-surface-is-solid",!owner.Structure.HasSurface(At(1,67,60)) && scene.Roof(1,67,60));
        var solution=owner.Combat.Solve(pawn,pawn.Position,shotTarget,40);
        Check("roof-over-void-blocks-cross-floor-target-solution",solution.InRange && !solution.Clear && solution.Obstruction?.Kind==ObstructionKind.Roof);
        var bullet=(Projectile)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Bullet_BoltActionRifle"),pawn.Position,map);
        bullet.Launch(pawn,pawn.DrawPos,shotTarget,shotTarget,ProjectileHitFlags.All);
        var flight=CombatService.Flight(bullet)!;
        AccessTools.Method(typeof(Projectile),"TickInterval").Invoke(bullet,new object[]{flight.TotalTicks});
        Check("actual-bullet-stops-on-underside-of-remote-roof",bullet.Destroyed && flight.ImpactCommitted && flight.ImpactPoint.y<6 && flight.ImpactPoint.y>5.99f && flight.ImpactLayer==1);
        Check("roof-blocked-bullet-never-damages-upper-target",shotTarget.HitPoints==hp);
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(shotTarget.Position.ToVector3Shifted(),12);
        pendingScreenshot=Path.Combine(directory,"combat-roof.png");screenshotHold=3;stage=11;ticks=0;
    }
    private static void BeginGrenadeFlee()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        for(int x=64;x<=82;x++)for(int z=64;z<=76;z++)
        {
            IntVec3 c=At(0,x,z);foreach(Thing t in c.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);map.terrainGrid.SetTerrain(At(1,x,z),RimLayersDefOf.RL_StructuralFloor);
            if((x-64)%6==0 && (z-64)%6==0)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),c,map);
        }
        for(int x=64;x<=71;x++){map.roofGrid.SetRoof(At(0,x,70),null);map.terrainGrid.SetTerrain(At(1,x,70),RimLayersDefOf.RL_OpenAir);}
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(0,70,70),map);
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>owner.TryDecode(c,out var p) && p.X>=64 && p.X<=82 && p.Z>=64 && p.Z<=76);
        pawn.Position=At(1,76,70);pawn.Notify_Teleported();
        grenadeEscaper=map.mapPawns.FreeColonistsSpawned.Where(p=>p!=pawn && !p.Downed).OrderByDescending(p=>p.GetStatValue(StatDefOf.MoveSpeed)).First();
        grenadeEscaper.jobs.StopAll();grenadeEscaper.drafter.Drafted=false;grenadeEscaper.jobs.StopAll();
        grenadeEscaper.Position=grenadeOldCell=At(1,71,71);grenadeEscaper.Notify_Teleported();
        if(grenadeEscaper.needs.food!=null)grenadeEscaper.needs.food.CurLevelPercentage=1;
        if(grenadeEscaper.needs.rest!=null)grenadeEscaper.needs.rest.CurLevelPercentage=1;
        grenadeEscaper.mindState.knownExploder=null;map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        escapingGrenade=(Projectile_Explosive)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Proj_GrenadeFrag"),pawn.Position,map);
        Rand.PushState(71941);
        try{escapingGrenade.Launch(pawn,pawn.DrawPos,At(0,68,70),At(0,68,70),ProjectileHitFlags.All,true);}
        finally{Rand.PopState();}
        for(int i=0;i<200 && CombatService.Flight(escapingGrenade) is {ImpactCommitted:false};i++)AccessTools.Method(typeof(Projectile_Explosive),"TickInterval").Invoke(escapingGrenade,new object[]{1});
        var flight=CombatService.Flight(escapingGrenade)!;
        Check("native-frag-grenade-lands-on-lower-wall-side",flight.ImpactCommitted && !escapingGrenade.Destroyed && flight.ImpactLayer==0 && flight.ImpactPoint.y<3 && flight.ImpactPoint.y>2.5f,$"impact={flight.ImpactPoint}");
        Check("unmodified-grenade-radius-and-delay-are-used",escapingGrenade.def.projectile.explosionRadius==1.9f && escapingGrenade.def.projectile.explosionDelay==100);
        Check("landed-grenade-notifies-real-upper-pawn",grenadeEscaper.mindState.knownExploder==escapingGrenade && owner.Warnings.Exposed(escapingGrenade,grenadeOldCell));
        grenadeBudget=GrenadeTimer(escapingGrenade);grenadeClock=Find.TickManager.TicksGame;grenadeInjuries=InjurySum(grenadeEscaper);
        GameDataSaveLoader.SaveGame("RimLayers_V1GrenadeFlee");
        File.WriteAllText(Path.Combine(directory,"grenade-flee-baseline.json"),JsonSerializer.Serialize(new{pawn=grenadeEscaper.ThingID,grenade=escapingGrenade.ThingID,clock=grenadeClock,budget=grenadeBudget,injuries=grenadeInjuries}));
        Check("saved-native-landed-grenade-before-detonation",grenadeBudget>0 && grenadeBudget<=100);stage=180;ticks=0;
    }
    private static void RestoreGrenadeFlee()
    {
        using var b=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"grenade-flee-baseline.json")));var d=b.RootElement;
        grenadeEscaper=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==d.GetProperty("pawn").GetString());
        escapingGrenade=(Projectile_Explosive)map.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty("grenade").GetString());
        grenadeOldCell=At(1,71,71);grenadeClock=d.GetProperty("clock").GetInt32();grenadeBudget=d.GetProperty("budget").GetInt32();grenadeInjuries=d.GetProperty("injuries").GetSingle();
        Check("restart-keeps-native-grenade-countdown-exactly",GrenadeTimer(escapingGrenade)==grenadeBudget);
        owner.Warnings.Tick();
        Check("restart-rebuilds-landed-grenade-warning",grenadeEscaper.mindState.knownExploder==escapingGrenade);
        Check("warning-rebuild-does-not-restart-grenade-clock",GrenadeTimer(escapingGrenade)==grenadeBudget);
        stage=180;ticks=0;
    }
    private static void CaptureGrenadeWave()
    {
        foreach(var blast in owner.Combat.Blasts.Where(b=>!b.Explosion.Destroyed))
        {grenadeWave=true;grenadeCovered|=BlastAccess.Cells(blast.Explosion).Contains(grenadeOldCell);}
    }
    private static void GrenadeFleeStep()
    {
        if(stage==180)
        {
            if(grenadeEscaper.CurJob?.def==JobDefOf.Goto && grenadeEscaper.CurJob.locomotionUrgency==LocomotionUrgency.Sprint)grenadeSprint=true;
            if(escapingGrenade.Destroyed)
            {
                Check("real-grenade-flee-starts-sprint-and-actually-moves",grenadeSprint && grenadeEscaper.Position!=grenadeOldCell);
                Check("grenade-detonates-on-original-native-deadline",Find.TickManager.TicksGame-grenadeClock==grenadeBudget,$"budget={grenadeBudget}, elapsed={Find.TickManager.TicksGame-grenadeClock}");
                CaptureGrenadeWave();stage=181;ticks=0;
            }
        }
        else if(stage==181)
        {
            CaptureGrenadeWave();
            if(ticks>40 && !owner.Combat.Blasts.Any(b=>!b.Explosion.Destroyed))
            {
                Check("real-frag-explosion-would-cover-original-upper-cell",grenadeWave && grenadeCovered);
                Check("escaped-pawn-receives-no-damage-from-actual-grenade",!grenadeEscaper.Dead && InjurySum(grenadeEscaper)==grenadeInjuries && blastDamageCalls.GetValueOrDefault(grenadeEscaper.ThingID)==0);
                Check("landed-impact-is-not-replayed-on-detonation",owner.Combat.CommittedImpacts==(mode=="generate"?1:0));
                grenadeEscaper.drafter.Drafted=true;LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.Selector.Select(grenadeEscaper);
                Find.CameraDriver.SetRootPosAndSize(grenadeEscaper.Position.ToVector3Shifted(),12);
                pendingScreenshot=Path.Combine(directory,mode+"-grenade-flee.png");screenshotHold=3;stage=11;ticks=0;
            }
        }
        if(ticks>500){Check("grenade-flee-stage-completes",false,$"stage={stage},pawn={grenadeEscaper.Position},job={grenadeEscaper.CurJob}");Finish();}
    }
}
