using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void BeginShieldTests()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        pawn.equipment.AddEquipment(gun);
        shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        for(int x=65;x<=97;x++)
            for(int z=59;z<=61;z++)
            {
                var cell=At(0,x,z);
                foreach(Thing thing in cell.GetThingList(map).ToList())
                    if(thing is not Pawn) {if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
                map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);map.fogGrid.Unfog(cell);
            }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65 && c.x<=97 && c.z>=59 && c.z<=61);
        shotTarget=ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel);
        GenSpawn.Spawn(shotTarget,At(0,95,60),map);shotTarget.SetFaction(Faction.OfPlayer);
        int before=shotTarget.HitPoints;
        var lower=SpawnBroadshield(0,80,60);
        Check("royalty-range-shield-active",lower.Active);
        FireFastProbe(gun);
        Check("shield-on-wrong-floor-does-not-intercept",shotTarget.HitPoints<before && lower.currentHitPoints==100);
        lower.parent.Destroy(DestroyMode.Vanish);
        var middle=SpawnBroadshield(1,80,60);
        before=shotTarget.HitPoints;
        FireFastProbe(gun);
        Check("actual-flight-layer-shield-intercepts",shotTarget.HitPoints==before && middle.currentHitPoints==82,
            $"shield={middle.currentHitPoints}, target={shotTarget.HitPoints}");
        var wall=(Building)ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel);
        map.roofGrid.SetRoof(At(0,74,60),RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(1,74,60),RimLayersDefOf.RL_StructuralFloor);
        GenSpawn.Spawn(wall,At(1,74,60),map);
        int wallBefore=wall.HitPoints,shieldBefore=middle.currentHitPoints;
        FireFastProbe(gun);
        Check("earlier-wall-wins-over-later-shield",wall.HitPoints<wallBefore &&
            middle.currentHitPoints==shieldBefore && shotTarget.HitPoints==before);
        wall.Destroy(DestroyMode.Vanish);middle.parent.Destroy(DestroyMode.Vanish);

        // Reverse direction, using the real core shield belt's normal damage pipeline.
        // The previous target must not remain as a full wall around the reversed muzzle.
        shotTarget.Destroy(DestroyMode.Vanish);
        var target=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);
        target.jobs.StopAll();target.Position=At(2,64,60);target.Notify_Teleported();
        frozenCombatPawn=target;
        var belt=(Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_ShieldBelt"));
        target.apparel.Wear(belt,dropReplacedApparel:false);
        var personal=belt.GetComp<CompShield>();
        AccessTools.Field(typeof(CompShield),"energy").SetValue(personal,1f);
        AccessTools.Field(typeof(CompShield),"ticksToReset").SetValue(personal,-1);
        float severity=target.health.hediffSet.hediffs.Sum(h=>h.Severity),energy=personal.Energy;
        pawn.Position=At(0,95,60);pawn.Notify_Teleported();
        shotTarget=target;
        FireFastProbe(gun);
        Check("upward-shot-runs-native-personal-shield",personal.Energy<energy &&
            Math.Abs(target.health.hediffSet.hediffs.Sum(h=>h.Severity)-severity)<0.0001,
            $"energy={energy}->{personal.Energy}");
        var empShield=SpawnBroadshield(1,80,60);
        float protectedEnergy=personal.Energy;
        FireFastProbe(gun,DamageDefOf.EMP);
        Check("range-shield-keeps-native-emp-disarm",!empShield.Active && personal.Energy==protectedEnergy);
        empShield.parent.Destroy(DestroyMode.Vanish);
        pawn.Position=At(0,64,60);pawn.Notify_Teleported();
        Verb melee=pawn.meleeVerbs.TryGetMeleeVerb(target);
        AccessTools.Field(typeof(Verb),"currentTarget").SetValue(melee,new LocalTargetInfo(target));
        bool committed=(bool)AccessTools.Method(typeof(Verb_MeleeAttack),"TryCastShot").Invoke(melee,null)!;
        Check("melee-commit-rejects-same-column-other-floor",!committed &&
            Math.Abs(target.health.hediffSet.hediffs.Sum(h=>h.Severity)-severity)<0.0001);
        Check("all-five-native-impact-chains-commit-once",owner.Combat.CommittedImpacts==5,
            owner.Combat.CommittedImpacts.ToString());
        stage=11;ticks=0;
    }

    private static CompProjectileInterceptor SpawnBroadshield(int layer,int x,int z)
    {
        if(layer>0)
        {
            if(At(0,x-2,z).GetEdifice(map)==null)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,x-2,z),map);
            map.roofGrid.SetRoof(At(0,x,z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,x,z),RimLayersDefOf.RL_StructuralFloor);
        }
        Thing building=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("BroadshieldProjector"));
        GenSpawn.Spawn(building,At(layer,x,z),map);building.SetFaction(Faction.OfAncientsHostile);
        var comp=building.TryGetComp<CompProjectileInterceptor>();
        comp.maxHitPointsOverride=100;comp.currentHitPoints=100;
        return comp;
    }
    private static void FireFastProbe(Thing equipment,DamageDef? damage=null)
    {
        var projectile=(Projectile)ThingMaker.MakeThing(shootingVerb.verbProps.defaultProjectile);
        GenSpawn.Spawn(projectile,pawn.Position,map);
        if(damage!=null) projectile.damageDefOverride=damage;
        projectile.Launch(pawn,pawn.DrawPos,shotTarget,shotTarget,ProjectileHitFlags.All,false,equipment);
        var flight=CombatService.Flight(projectile);
        Check("probe-has-real-saved-flight",flight!=null);
        if(flight==null) return;
        // Exercise a real large-delta callback: the complete wall/shield ordering is within one sweep.
        AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)})
            .Invoke(projectile,new object[]{flight.TotalTicks});
        Check("swept-projectile-terminates-once",projectile.Destroyed);
    }
}
