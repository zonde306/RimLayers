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
    private static Thing mortarShells=null!;
    private static float mortarFuel;
    private static int mortarJobId;
    private static bool mortarVisitedUpper,mortarProfileAfterConsumption;
    private static void MortarConsumed()
    {
        if(CombatCoordinates.CastingThing==combatTurret)
            mortarProfileAfterConsumption=ShotProfiles.Overhead(combatTurret.AttackVerb)&&LayerExposureLaunch.Managed&&
                ShotProfiles.Projectile(combatTurret.AttackVerb)?.defName=="Bullet_Shell_HighExplosive";
    }
    private static void BeginManned()
    {
        ClearCombatStrip(65,115,58);
        new Harmony("rimlayers.diagnostics.mortar-consumption").Patch(AccessTools.Method(typeof(CompChangeableProjectile),"Notify_ProjectileLaunched"),postfix:new HarmonyMethod(typeof(V1Integration),nameof(MortarConsumed)));
        pawn.jobs.StopAll();pawn.drafter.Drafted=false;
        combatTurret=(Building_TurretGun)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"),ThingDefOf.Steel);
        GenSpawn.Spawn(combatTurret,At(2,58,58),map);combatTurret.SetFaction(Faction.OfPlayer);
        AccessTools.Field(typeof(Building_TurretGun),"holdFire").SetValue(combatTurret,true);
        mortarShells=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"));mortarShells.stackCount=2;
        GenSpawn.Spawn(mortarShells,At(0,55,54),map);mortarShells.SetForbidden(false);
        Check("native-ammo-query-finds-shell-on-other-floor",JobDriver_ManTurret.FindAmmoForTurret(pawn,combatTurret)==mortarShells);
        Check("operator-has-real-route-to-interaction-cell",pawn.CanReach(combatTurret,PathEndMode.InteractionCell,Danger.Deadly));
        pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret,combatTurret));mortarJobId=pawn.CurJob!.loadID;
        mortarFuel=combatTurret.refuelableComp.Fuel;stage=400;ticks=0;
    }
    private static void RestoreManned()
    {
        new Harmony("rimlayers.diagnostics.mortar-consumption").Patch(AccessTools.Method(typeof(CompChangeableProjectile),"Notify_ProjectileLaunched"),postfix:new HarmonyMethod(typeof(V1Integration),nameof(MortarConsumed)));
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"manned-baseline.json")));var b=doc.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("pawn").GetString());
        combatTurret=map.listerThings.AllThings.OfType<Building_TurretGun>().Single(t=>t.ThingID==b.GetProperty("turret").GetString());
        mortarShells=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("shells").GetString());
        mortarFuel=(float)b.GetProperty("fuel").GetDouble();mortarJobId=b.GetProperty("job").GetInt32();
        if(!GenCommandLine.CommandLineArgPassed("rimlayersmortarflightreload"))
        {
        Check("restart-keeps-native-manning-job",pawn.CurJob?.def==JobDefOf.ManTurret&&pawn.CurJob.loadID==mortarJobId);
        Check("restart-keeps-carried-shell-without-duplicate",pawn.carryTracker.CarriedThing?.stackCount==1&&mortarShells.stackCount==1);
        }
        if(GenCommandLine.CommandLineArgPassed("rimlayersmortarflightreload"))
        {
            using var flightDoc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"manned-flight.json")));
            shotProjectile=map.listerThings.AllThings.OfType<Projectile>().Single(p=>p.ThingID==flightDoc.RootElement.GetProperty("projectile").GetString());
            Check("restart-restores-mortar-emitter-and-physical-arc",CombatService.Flight(shotProjectile) is {ArcHeight:>0,ImpactCommitted:false} f&&f.Emitter==combatTurret);
            stage=403;
        }
        else stage=401;
        ticks=0;
    }
    private static void MannedStep()
    {
        if(owner.TryDecode(pawn.Position,out var at)&&at.LayerId==2)mortarVisitedUpper=true;
        if(stage==400&&pawn.carryTracker.CarriedThing?.def.defName=="Shell_HighExplosive")
        {
            Check("native-operator-takes-exactly-one-shell",pawn.carryTracker.CarriedThing.stackCount==1&&mortarShells.stackCount==1);
            GameDataSaveLoader.SaveGame("RimLayers_V1Manned");
            File.WriteAllText(Path.Combine(directory,"manned-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,turret=combatTurret.ThingID,shells=mortarShells.ThingID,fuel=mortarFuel,job=mortarJobId}));
            stage=401;ticks=0;
        }
        if(stage==401 && combatTurret.GetComp<CompMannable>().ManningPawn==pawn)
        {
            Check("operator-physically-arrives-at-native-interaction-cell",pawn.Position==combatTurret.InteractionCell&&mortarVisitedUpper);
            Check("native-manning-job-loads-carried-shell",combatTurret.gun.TryGetComp<CompChangeableProjectile>().Loaded&&pawn.carryTracker.CarriedThing==null);
            Check("hold-fire-does-not-consume-barrel",combatTurret.refuelableComp.Fuel==mortarFuel);
            Check("job-identity-survives-cross-layer-supply",pawn.CurJob?.loadID==mortarJobId);
            AccessTools.Field(typeof(Building_TurretGun),"holdFire").SetValue(combatTurret,false);
            combatTurret.OrderAttack(At(0,105,58));stage=402;ticks=0;
        }
        if(stage==402 && owner.Combat.Flights.FirstOrDefault(f=>f.Emitter==combatTurret&&!f.Projectile.Destroyed) is {} flight)
        {
            Check("manned-mortar-native-launch-uses-gun-not-operator",flight.Projectile.Launcher==pawn&&Math.Abs(flight.Start.x-combatTurret.DrawPos.x)<0.001&&flight.Start.y>6);
            Check("native-mortar-scatter-has-physical-arc",flight.ArcHeight>0&&flight.End.y<3);
            Check("shot-keeps-ammo-profile-after-native-consumption",mortarProfileAfterConsumption);
            Check("native-shot-consumes-one-barrel-use",Math.Abs(combatTurret.refuelableComp.Fuel-(mortarFuel-1))<0.001);
            Check("native-shot-consumes-loaded-shell-once",!combatTurret.gun.TryGetComp<CompChangeableProjectile>().Loaded&&mortarShells.stackCount==1);
            shotProjectile=flight.Projectile;
            AccessTools.Field(typeof(Building_TurretGun),"holdFire").SetValue(combatTurret,true);
            pawn.jobs.StopAll();pawn.drafter.Drafted=true;
            GameDataSaveLoader.SaveGame("RimLayers_V1MannedFlight");
            File.WriteAllText(Path.Combine(directory,"manned-flight.json"),JsonSerializer.Serialize(new{projectile=shotProjectile.ThingID}));
            stage=403;ticks=0;
        }
        if(stage==403&&ticks>50&&shotProjectile?.Destroyed==true)
        {
            Check("cancel-manning-stops-operator",combatTurret.GetComp<CompMannable>().ManningPawn==null&&pawn.CurJob?.def!=JobDefOf.ManTurret);
            Check("cancel-and-impact-do-not-repeat-ammo-or-fuel",Math.Abs(combatTurret.refuelableComp.Fuel-(mortarFuel-1))<0.001&&mortarShells.stackCount==1);
            Check("native-mortar-impact-commits-once",owner.Combat.CommittedImpacts==1);
            LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.Selector.Select(combatTurret);
            Find.CameraDriver.SetRootPosAndSize(combatTurret.DrawPos,15);
            pendingScreenshot=Path.Combine(directory,mode+"-manned-mortar.png");screenshotHold=3;stage=11;ticks=0;
        }
        if(ticks>6000){Check("manning-stage-completes",false,$"stage={stage},job={pawn.CurJob},pos={pawn.Position},toil={pawn.jobs.curDriver?.CurToilIndex},loaded={combatTurret.gun.TryGetComp<CompChangeableProjectile>().Loaded},target={combatTurret.CurrentTarget}");Finish();}
    }
}
