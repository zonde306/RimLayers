using System.Reflection;
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
    private static void ClearCombatStrip(int minX,int maxX,int z)
    {
        for(int x=minX;x<=maxX;x++)for(int dz=-2;dz<=2;dz++)
        {
            var cell=At(0,x,z+dz);
            foreach(Thing t in cell.GetThingList(map).ToList())
                if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>owner.TryDecode(c,out var p)&&p.X>=minX&&p.X<=maxX&&Math.Abs(p.Z-z)<=2);
    }
    private static Pawn CombatAdult(Faction faction)=>PawnGenerator.GeneratePawn(new PawnGenerationRequest(
        PawnKindDefOf.Colonist,faction,forceGenerateNewPawn:true,canGeneratePawnRelations:false,mustBeCapableOfViolence:true,
        fixedBiologicalAge:30,fixedChronologicalAge:30,forceBaselinerChance:1));
    private static void BeginCombat3D()
    {
        pawn=CombatAdult(Faction.OfPlayer);GenSpawn.Spawn(pawn,At(0,90,58),map);
        Check("controlled-adult-silhouette",Math.Abs(pawn.BodySize-1)<0.0001);
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.drafter.FireAtWill=false;
        pawn.Position=At(0,90,58);pawn.Notify_Teleported();ClearCombatStrip(65,110,58);
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        pawn.equipment.AddEquipment(gun);shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        var props=(VerbProperties)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(shootingVerb.verbProps,null)!;
        props.canGoWild=false;shootingVerb.verbProps=props;
        var target=CombatAdult(Faction.OfPlayer);
        GenSpawn.Spawn(target,At(1,63,58),map);target.jobs.StopAll();target.drafter.Drafted=true;
        frozenCombatPawn=target;
        var cover=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Sandbags"),ThingDefOf.Cloth),At(1,64,58),map);
        Check("sandbags-independent-height",Math.Abs(CombatService.TopHeight(cover)-1)<0.00001&&Math.Abs(cover.def.fillPercent-0.55)<0.00001);
        var up=owner.Combat.SolveWeapon(pawn,pawn.Position,target,shootingVerb);
        var level=owner.Combat.Solve(pawn,At(1,90,58),target,float.PositiveInfinity);
        Check("uphill-nine-sample-exposure-is-lower",up.Samples?.Length==9&&up.Exposure>0&&up.Exposure<level.Exposure,$"up={up.Exposure},level={level.Exposure}");
        Check("uphill-range-90-percent",Math.Abs(up.EffectiveRange-shootingVerb.EffectiveRange*0.9f)<0.0001);
        Check("two-layer-range-bounds",Math.Abs(owner.Combat.EffectiveRange(shootingVerb,At(2,90,58),At(0,60,58))-shootingVerb.EffectiveRange*1.2f)<0.0001&&
            Math.Abs(owner.Combat.EffectiveRange(shootingVerb,At(0,90,58),At(2,60,58))-shootingVerb.EffectiveRange*0.8f)<0.0001);
        Check("muzzle-above-own-sandbags-can-shoot-down",owner.Combat.Solve(target,target.Position,pawn,100).Clear);
        Check("native-report-matches-exposure",Math.Abs(ShotReport.HitReportFor(pawn,shootingVerb,target).PassCoverChance-up.Exposure)<0.0001);
        var nativeSame=ShotReport.HitReportFor(target,shootingVerb,pawn);
        // Same-layer reports must keep vanilla cover, rather than use the nine-sample result.
        var old=pawn.Position;pawn.Position=At(1,90,58);
        var flat=ShotReport.HitReportFor(pawn,shootingVerb,target);
        Check("same-layer-native-cover-unchanged",Math.Abs(flat.PassCoverChance-(1-CoverUtility.CalculateOverallBlockChance(target,pawn.Position,map)))<0.0001);
        pawn.Position=old;
        cover.Position=At(1,62,58);
        Check("cover-behind-target-does-not-protect",owner.Combat.SolveWeapon(pawn,pawn.Position,target,shootingVerb).Exposure==1);
        cover.Position=At(1,64,58);
        int blocked=0,clear=0;
        Rand.PushState(91023);
        try
        {
            for(int i=0;i<24;i++)
            {
                AccessTools.Field(typeof(Verb),"currentTarget").SetValue(shootingVerb,new LocalTargetInfo(target));
                bool fired=(bool)AccessTools.Method(typeof(Verb_LaunchProjectile),"TryCastShot").Invoke(shootingVerb,null)!;
                Check("native-launch-"+i,fired);
                var flight=owner.Combat.Flights.Last(f=>!f.Projectile.Destroyed);
                var hit=ShotGeometry.First(new(flight.Start.x,flight.Start.y,flight.Start.z),new(flight.End.x,flight.End.y,flight.End.z),
                    new LayerShotScene(owner,pawn,includeCover:true));
                if(hit?.ThingKey==cover.thingIDNumber)
                {
                    blocked++;int hp=cover.HitPoints;
                    FinishBallisticProbe(flight.Projectile);
                    Check("cover-sample-hits-real-sandbag-"+i,cover.HitPoints<hp);
                    cover.HitPoints=cover.MaxHitPoints;
                }
                else {clear++;flight.Projectile.Destroy();}
            }
        }
        finally{Rand.PopState();}
        Check("single-cover-roll-produces-blocked-and-clear-rays",blocked>0&&clear>0,$"blocked={blocked},clear={clear}");
        AccessTools.Method(typeof(Pawn_HealthTracker),"MakeDowned").Invoke(target.health,new object?[]{null,null});
        Check("prone-target-fully-hidden",!owner.Combat.SolveWeapon(pawn,pawn.Position,target,shootingVerb).Clear);
        RemovePolicyPawn(target);cover.Destroy(DestroyMode.Vanish);
        // Real fixed turret can acquire the added range, not just report it.
        var turret=(Building_TurretGun)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"),ThingDefOf.Steel);
        GenSpawn.Spawn(turret,At(2,64,58),map);turret.SetFaction(Faction.OfPlayer);
        target=CombatAdult(Faction.OfAncientsHostile);
        GenSpawn.Spawn(target,At(0,96,58),map);target.jobs.StopAll();
        Check("turret-target-beyond-native-within-height-range",32>turret.AttackVerb.EffectiveRange&&turret.AttackVerb.CanHitTarget(target));
        Check("automatic-turret-acquires-extra-range",turret.TryFindNewTarget().Thing==target);
        turret.OrderAttack(target);
        Check("forced-turret-order-keeps-real-cross-floor-target",turret.CurrentTarget.Thing==target);
        turret.Destroy(DestroyMode.Vanish);RemovePolicyPawn(target);
        // Arc obstruction uses the same geometry as the real projectile sweep, including same-floor arcs.
        var mortar=(Building_TurretGun)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"),ThingDefOf.Steel);
        GenSpawn.Spawn(mortar,At(0,70,58),map);mortar.SetFaction(Faction.OfPlayer);
        Check("unloaded-mortar-remains-curved-with-native-range",ShotProfiles.Curved(mortar.AttackVerb)&&
            owner.Combat.EffectiveRange(mortar.AttackVerb,mortar.Position,At(2,105,58))==mortar.AttackVerb.EffectiveRange);
        var magazine=mortar.gun.TryGetComp<CompChangeableProjectile>();
        magazine.LoadShell(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"),1);
        var aim=new LocalTargetInfo(At(0,105,58));
        var curve=owner.Combat.SolveWeapon(mortar,mortar.Position,aim,mortar.AttackVerb);
        Check("mortar-keeps-native-range-and-open-launch",curve.Curved&&curve.Clear&&Math.Abs(curve.EffectiveRange-mortar.AttackVerb.EffectiveRange)<0.001);
        // An overhead roof elsewhere in the muzzle column is not an open firing position.
        map.roofGrid.SetRoof(At(1,70,58),RoofDefOf.RoofConstructed);
        Check("mortar-checks-entire-launch-column",!owner.Combat.OpenLaunchColumn(mortar.Position)&&
            owner.Combat.SolveWeapon(mortar,mortar.Position,aim,mortar.AttackVerb).LaunchBlocked);
        map.roofGrid.SetRoof(At(1,70,58),null);
        var shell=(Projectile)GenSpawn.Spawn(((Verb_LaunchProjectile)mortar.AttackVerb).Projectile,mortar.Position,map);
        shell.Launch(mortar,mortar.DrawPos,aim,aim,ProjectileHitFlags.All,false,mortar.gun);
        var shellFlight=CombatService.Flight(shell);
        Check("same-floor-mortar-has-physical-flight",shellFlight is {ArcHeight:>0}&&shellFlight.Start.y<3&&shellFlight.End.y<3);
        // Put a roof at the ascent crossing, after launch. The swept shell must strike its underside.
        var scene=new LayerShotScene(owner);
        int crossing=-1;
        if(shellFlight!=null)
            for(int x=71;x<100;x++)
            {
                map.roofGrid.SetRoof(At(0,x,58),RoofDefOf.RoofConstructed);
                var contact=ExposureGeometry.First(new(shellFlight.Start.x,shellFlight.Start.y,shellFlight.Start.z),
                    new(shellFlight.End.x,shellFlight.End.y,shellFlight.End.z),shellFlight.ArcHeight,scene);
                if(contact!=null){crossing=x;break;}
                map.roofGrid.SetRoof(At(0,x,58),null);
            }
        FinishBallisticProbe(shell);
        Check("same-floor-mortar-hits-real-upper-obstruction",crossing>=0&&shellFlight!.ImpactPoint.y<3&&shellFlight.ImpactPoint.x<80);
        if(crossing>=0)map.roofGrid.SetRoof(At(0,crossing,58),null);
        foreach(var blast in owner.Combat.Blasts.ToList())if(!blast.Explosion.Destroyed)blast.Explosion.Destroy();
        CheckMortarAmmunition(mortar);
        mortar.Destroy(DestroyMode.Vanish);
        // Keep a real targeting UI open long enough for the rendered screenshot.
        target=CombatAdult(Faction.OfPlayer);
        GenSpawn.Spawn(target,At(1,63,58),map);target.drafter.Drafted=true;frozenCombatPawn=target;
        GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Barricade"),ThingDefOf.WoodLog),At(1,64,58),map);
        commandMouse=target.DrawPos;
        var h=new Harmony("rimlayers.diagnostics.combat3d-mouse");
        h.Patch(AccessTools.Method(typeof(UI),nameof(UI.MouseMapPosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandMouse)));
        Find.Selector.ClearSelection();Find.Selector.Select(pawn);
        Find.Targeter.BeginTargeting(shootingVerb);LayerView.Switch(owner,1);
        Find.CameraDriver.SetRootPosAndSize(At(1,71,58).ToVector3Shifted(),18);
        Check("targeting-readout-uses-viewed-target-floor",owner.ViewedLayer==1 && CombatReadout.Query(Find.Targeter,out _,out _,out var ui) && ui.AimCell.LayerId==1);
        pendingScreenshot=Path.Combine(directory,"combat3d-targeting.png");screenshotHold=5;
        stage=11;ticks=0;
    }
}
