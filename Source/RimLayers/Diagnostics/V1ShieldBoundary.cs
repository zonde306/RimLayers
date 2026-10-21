using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static CompProjectileInterceptor boundaryShield=null!;
    private static Thing? boundaryWall;
    private static bool boundarySaved;
    private static int boundaryChecks,boundaryShieldHP,boundaryWallHP,boundaryDraws;
    private static void ShieldFlightDrawObserved(Projectile __instance,UnityEngine.Vector3 drawLoc)
    {
        if(mode=="load"&&__instance==shotProjectile&&stage==190&&owner.IsViewedCell(drawLoc.ToIntVec3()))boundaryDraws++;
    }
    private static bool ShieldAccepts => scenario=="combat-shield-save";

    private static void BeginShieldBoundary()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        pawn.equipment.AddEquipment(gun);shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        for(int x=65;x<=97;x++)for(int z=59;z<=61;z++)
        {
            var cell=At(0,x,z);
            foreach(Thing thing in cell.GetThingList(map).ToList())
                if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
            map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65&&c.x<=97&&c.z>=59&&c.z<=61);
        shotTarget=BlastWall(0,95,60);shotHPBefore=shotTarget.HitPoints;
        boundaryShield=SpawnBroadshield(1,80,60);
        if(!ShieldAccepts)
        {
            boundaryShield.parent.SetFaction(Faction.OfPlayer);
            map.roofGrid.SetRoof(At(0,84,60),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,84,60),RimLayersDefOf.RL_StructuralFloor);
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,82,60),map);
            boundaryWall=BlastWall(1,84,60);boundaryWallHP=boundaryWall.HitPoints;
        }
        Check("boundary-native-range-shield-is-active",boundaryShield.Active);
        shotProjectile=(Projectile)ThingMaker.MakeThing(shootingVerb.verbProps.defaultProjectile);
        GenSpawn.Spawn(shotProjectile,pawn.Position,map);
        shotProjectile.Launch(pawn,pawn.DrawPos,shotTarget,shotTarget,ProjectileHitFlags.All,false,gun);
        var flight=CombatService.Flight(shotProjectile)!;
        // Keep the entire native decision + later wall inside one large-delta callback.
        AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)}).Invoke(shotProjectile,new object[]{flight.TotalTicks});
        Check("save-observed-at-native-shield-return",boundarySaved&&boundaryChecks==1,$"saved={boundarySaved},checks={boundaryChecks}");
        stage=190;ticks=0;
    }

    private static void ShieldDecisionObserved(CompProjectileInterceptor __instance,Projectile projectile,bool __result)
    {
        if(__instance!=boundaryShield||projectile!=shotProjectile)return;
        boundaryChecks++;
        if(mode!="generate"||boundarySaved)return;
        boundarySaved=true;
        var flight=CombatService.Flight(projectile)!;
        Check("native-shield-decision-matches-faction",__result==ShieldAccepts,$"accepted={__result}");
        Check("save-sees-final-shield-receipt",flight.ShieldCommitted==__result,$"receipt={flight.ShieldCommitted}");
        Check("sweep-cursor-keeps-remaining-segment",flight.LastProgress>0&&flight.LastProgress<flight.Progress,
            $"consumed={flight.LastProgress},clock={flight.Progress}");
        boundaryShieldHP=boundaryShield.currentHitPoints;
        Check("native-shield-debit-precedes-save",boundaryShieldHP==(ShieldAccepts?82:100),$"hp={boundaryShieldHP}");
        Check("save-before-any-native-impact",!flight.ImpactCommitted&&owner.Combat.CommittedImpacts==0&&shotTarget.HitPoints==shotHPBefore);
        GameDataSaveLoader.SaveGame("RimLayers_V1ShieldBoundary");
        File.WriteAllText(Path.Combine(directory,"shield-boundary-baseline.json"),JsonSerializer.Serialize(new {
            projectile=projectile.ThingID,shield=boundaryShield.parent.ThingID,target=shotTarget.ThingID,
            targetHP=shotHPBefore,shieldHP=boundaryShieldHP,wall=boundaryWall?.ThingID,wallHP=boundaryWallHP,
            accepted=__result,consumed=flight.LastProgress,clock=flight.Progress
        }));
    }

    private static void RestoreShieldBoundary()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"shield-boundary-baseline.json")));
        var b=baseline.RootElement;
        shotProjectile=map.listerThings.AllThings.OfType<Projectile>().Single(t=>t.ThingID==b.GetProperty("projectile").GetString());
        boundaryShield=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("shield").GetString()).TryGetComp<CompProjectileInterceptor>();
        shotTarget=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("target").GetString());
        shotHPBefore=b.GetProperty("targetHP").GetInt32();boundaryShieldHP=b.GetProperty("shieldHP").GetInt32();
        if(!ShieldAccepts)
        {boundaryWall=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("wall").GetString());boundaryWallHP=b.GetProperty("wallHP").GetInt32();}
        var flight=CombatService.Flight(shotProjectile)!;
        Check("restart-keeps-native-debit-and-final-receipt",boundaryShield.currentHitPoints==boundaryShieldHP&&flight.ShieldCommitted==ShieldAccepts);
        Check("restart-keeps-consumed-not-clock-position",Math.Abs(flight.LastProgress-b.GetProperty("consumed").GetSingle())<0.00001&&flight.LastProgress<flight.Progress);
        Check("restart-has-no-impact-before-resume",!flight.ImpactCommitted&&owner.Combat.CommittedImpacts==0);
        Check("restart-draws-at-saved-contact",Math.Abs(flight.DrawPoint.X-flight.Point(flight.LastProgress).X)<0.00001);
        LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.CameraDriver.SetRootPosAndSize(At(1,80,60).ToVector3Shifted(),12);
        pendingScreenshot=Path.Combine(directory,mode+"-shield-boundary.png");screenshotHold=3;
        stage=190;ticks=0;
    }

    private static void ShieldBoundaryStep()
    {
        if(shotProjectile?.Destroyed!=true)
        {
            if(ticks>50){Check("boundary-projectile-terminates",false);Finish();}
            return;
        }
        if(mode=="load")Check("saved-contact-renders-before-first-resumed-tick",boundaryDraws>0,$"draws={boundaryDraws}");
        Check("boundary-impact-commits-exactly-once",owner.Combat.CommittedImpacts==1,$"commits={owner.Combat.CommittedImpacts}");
        Check("shield-charge-is-not-replayed",boundaryShield.currentHitPoints==boundaryShieldHP,$"hp={boundaryShield.currentHitPoints},saved={boundaryShieldHP}");
        Check("far-target-is-never-damaged",shotTarget.HitPoints==shotHPBefore&&blastDamageCalls.GetValueOrDefault(shotTarget.ThingID)==0);
        if(ShieldAccepts)Check("accepted-receipt-does-not-recheck-shield",boundaryChecks==(mode=="generate"?1:0),$"checks={boundaryChecks}");
        else Check("rejected-shield-resumes-and-hits-later-wall-once",boundaryWall!.HitPoints<boundaryWallHP&&blastDamageCalls.GetValueOrDefault(boundaryWall.ThingID)==1,
            $"wall={boundaryWall.HitPoints}/{boundaryWallHP},calls={blastDamageCalls.GetValueOrDefault(boundaryWall.ThingID)}");
        stage=11;ticks=0;
    }
}
