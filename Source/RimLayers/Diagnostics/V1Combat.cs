using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Thing shotTarget=null!;
    private static Projectile? shotProjectile;
    private static int shotHPBefore,damageApplications,screenshotHold;
    private static string? pendingScreenshot;
    private static Verb shootingVerb=null!;

    private static int structuralFallHits;
    private static float structuralFallDamage;
    private static void DamageArriving(Thing __instance,DamageInfo dinfo)
    {
        if(scenario=="structure"&&stage==13&&__instance==pawn&&dinfo.Def==DamageDefOf.Blunt){structuralFallHits++;structuralFallDamage+=dinfo.Amount;}
        if(scenario.StartsWith("combat-raid")) RaidDamage(__instance,dinfo);
        if(scenario=="combat-turret" && dinfo.Instigator==combatTurret)blastDamageCalls[__instance.ThingID]=blastDamageCalls.GetValueOrDefault(__instance.ThingID)+1;
        if(scenario=="combat-shot" && __instance==shotTarget) damageApplications++;
        if(scenario=="combat-policy" || scenario=="combat-explosion" || scenario=="combat-delay" || scenario=="combat-grenade-flee" || scenario is "combat-shield-save" or "combat-shield-pass") blastDamageCalls[__instance.ThingID]=blastDamageCalls.GetValueOrDefault(__instance.ThingID)+1;
    }
    private static void BeginCombatShot()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        for(int x=65;x<=97;x++)
            for(int z=59;z<=61;z++)
            {
                IntVec3 cell=At(0,x,z);
                foreach(Thing thing in cell.GetThingList(map).ToList())
                    if(thing is not Pawn) { if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
                map.roofGrid.SetRoof(cell,null);
                map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);
                map.fogGrid.Unfog(cell);
            }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65 && c.x<=97 && c.z>=59 && c.z<=61);
        shotTarget=ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel);
        GenSpawn.Spawn(shotTarget,At(0,95,60),map);
        shotTarget.SetFaction(Faction.OfPlayer); // No autonomous repeated fire from the test pawn.
        shotHPBefore=shotTarget.HitPoints;
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        pawn.equipment.AddEquipment(gun);
        shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        // Isolate spatial correctness from accuracy RNG without changing the shared weapon Def.
        var properties=(VerbProperties)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(shootingVerb.verbProps,null)!;
        properties.canGoWild=false;properties.warmupTime=0.5f;
        shootingVerb.verbProps=properties;
        pawn.skills.GetSkill(SkillDefOf.Shooting).Level=20;
        Check("native-verb-can-shoot-down-over-edge",shootingVerb.CanHitTarget(shotTarget));
        Check("native-verb-blocks-interior-floor-shot",!shootingVerb.TryFindShootLineFromTo(At(2,52,52),shotTarget,out _,ignoreRange:true));
        var report=ShotReport.HitReportFor(pawn,shootingVerb,shotTarget);
        float reportDistance=(float)AccessTools.Field(typeof(ShotReport),"distance").GetValue(report)!;
        Check("native-hit-report-uses-logical-distance",Math.Abs(reportDistance-31)<0.001,reportDistance.ToString());
        Find.Selector.Select(pawn);
        LayerView.Switch(owner,0);
        Check("switch-keeps-real-command-pawn",Find.Selector.SelectedObjectsListForReading.Contains(pawn) && pawn.Position==At(2,64,60));
        Job attack=JobMaker.MakeJob(JobDefOf.AttackStatic,shotTarget);
        attack.maxNumStaticAttacks=1;
        pawn.jobs.TryTakeOrderedJob(attack);
        Check("real-weapon-starts-attack-job",pawn.CurJob?.def==JobDefOf.AttackStatic);
        stage=20;ticks=0;
    }
    private static void RestoreCombatShot()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"combat-shot-baseline.json")));
        var data=baseline.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("pawn").GetString());
        shotTarget=map.listerThings.AllThings.Single(t=>t.ThingID==data.GetProperty("target").GetString());
        shotProjectile=map.listerThings.AllThings.OfType<Projectile>().Single(p=>p.ThingID==data.GetProperty("projectile").GetString());
        shotHPBefore=data.GetProperty("hitPoints").GetInt32();
        var flight=CombatService.Flight(shotProjectile);
        Check("restart-restores-live-projectile-fact",flight!=null && !flight.ImpactCommitted);
        Check("restart-keeps-immutable-muzzle",flight!=null && flight.Start.y>6 && owner.IsGroundCell(pawn.Position));
        Check("restart-before-hit-keeps-target-health",shotTarget.HitPoints==shotHPBefore);
        Check("restart-has-no-damage-replay",owner.Combat.CommittedImpacts==0 && damageApplications==0);
        stage=21;ticks=0;
    }
    private static void CombatShotStep()
    {
        if(stage==20)
        {
            var flight=owner.Combat.Flights.FirstOrDefault(f=>f.Projectile.intendedTarget.Thing==shotTarget &&
                !f.ImpactCommitted && f.Progress>0 && f.Progress<0.85);
            if(flight!=null)
            {
                shotProjectile=flight.Projectile;
                Check("real-weapon-launched-cross-layer-projectile",true,shotProjectile.ThingID);
                pawn.Position=At(0,40,40);pawn.Notify_Teleported();
                Check("launcher-move-does-not-change-muzzle",flight.Start.y>6 && owner.IsGroundCell(pawn.Position));
                GameDataSaveLoader.SaveGame("RimLayers_V1Combat");
                File.WriteAllText(Path.Combine(directory,"combat-shot-baseline.json"),JsonSerializer.Serialize(new {
                    pawn=pawn.ThingID,target=shotTarget.ThingID,projectile=shotProjectile.ThingID,hitPoints=shotHPBefore
                }));
                Check("saved-in-flight-before-damage",damageApplications==0 && shotTarget.HitPoints==shotHPBefore);
                var point=flight.Point(flight.Progress);
                LayerView.Switch(owner,Math.Clamp((int)Math.Floor(point.Height/3),0,2));
                Find.CameraDriver.SetRootPosAndSize(new Vector3((float)point.X,0,(float)point.Z+owner.ViewedLayer*owner.Layout!.Stride),12);
                pendingScreenshot=Path.Combine(directory,"combat-in-flight.png");
                screenshotHold=3;
                stage=21;ticks=0;
            }
            else if(ticks>1500) { Check("cross-layer-projectile-launched",false,DescribePawn()+$"; verb={shootingVerb.state}, stance={pawn.stances.curStance}, flights={owner.Combat.Flights.Count}, targetHP={shotTarget.HitPoints}, damageCalls={damageApplications}");Finish(); }
        }
        else if(stage==21)
        {
            if(shotProjectile?.Destroyed==true)
            {
                Check("native-projectile-damages-real-target",shotTarget.HitPoints<shotHPBefore,
                    $"before={shotHPBefore}, after={shotTarget.HitPoints}");
                Check("native-damage-applied-once",damageApplications==1,damageApplications.ToString());
                Check("impact-override-chain-commits-once",owner.Combat.CommittedImpacts==1,owner.Combat.CommittedImpacts.ToString());
                stage=22;ticks=0;
            }
            else if(ticks>500) { Check("projectile-impacts",false);Finish(); }
        }
        else if(stage==22 && ticks>50)
        {
            Check("completed-shot-does-not-replay",damageApplications==1 && owner.Combat.Flights.Count==0);
            LayerView.Switch(owner,0);
            Find.CameraDriver.SetRootPosAndSize(shotTarget.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,mode+"-combat-hit.png");
            screenshotHold=3;
            stage=11;ticks=0;
        }
    }
}
