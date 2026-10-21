using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void BeginCombatPolicy()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        pawn.equipment.AddEquipment(gun);shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        for(int x=65;x<=97;x++)for(int z=58;z<=62;z++)
        {
            var cell=At(0,x,z);
            foreach(Thing thing in cell.GetThingList(map).ToList())
                if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
            map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65&&c.x<=97&&c.z>=58&&c.z<=62);
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,78,61),map);
        map.roofGrid.SetRoof(At(0,80,60),RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(1,80,60),RimLayersDefOf.RL_StructuralFloor);
        float oldFactor=Find.Storyteller.difficulty.friendlyFireChanceFactor;
        try
        {
            PolicyProbe(gun,"prevent-friendly-fire",true,1,ProjectileHitFlags.All,Faction.OfPlayer,1,false);
            PolicyProbe(gun,"difficulty-zero-friendly-fire",false,0,ProjectileHitFlags.All,Faction.OfPlayer,1,false);
            PolicyProbe(gun,"hit-flags-exclude-incidental-pawns",false,1,ProjectileHitFlags.IntendedTarget|ProjectileHitFlags.NonTargetWorld,Faction.OfPlayer,1,false);
            PolicyProbe(gun,"friendly-fire-enabled",false,1,ProjectileHitFlags.All,Faction.OfPlayer,1,true);
            PolicyProbe(gun,"enemy-is-not-protected-by-friendly-rules",true,0,ProjectileHitFlags.All,Faction.OfAncientsHostile,1,true);
            PolicyProbe(gun,"same-column-pawn-below-flight-is-not-hit",false,1,ProjectileHitFlags.All,Faction.OfPlayer,0,false);
            foreach(string kind in new[]{"Squirrel","Muffalo","Mech_Scyther"})
            {
                Pawn target=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed(kind),kind=="Mech_Scyther"?Faction.OfMechanoids:Faction.OfPlayer);
                GenSpawn.Spawn(target,At(0,95,60),map);shotTarget=target;
                var samples=owner.Combat.SolveWeapon(pawn,pawn.Position,target,shootingVerb).Samples!;
                var projectile=PolicyShot(gun,ProjectileHitFlags.All,true,811);
                var flight=CombatService.Flight(projectile)!;
                Check(kind+"-uses-real-body-aim-height",samples.Any(s=>Math.Abs(flight.End.y-s.Aim.Height)<0.00001),
                    $"body={target.BodySize},aim={flight.End.y},top={CombatService.TopHeight(target)}");
                Check(kind+"-native-damage-pipeline-once",blastDamageCalls.GetValueOrDefault(target.ThingID)==1,
                    $"damageCalls={blastDamageCalls.GetValueOrDefault(target.ThingID)},dead={target.Dead}");
                RemovePolicyPawn(target);
            }
            Pawn downed=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
            GenSpawn.Spawn(downed,At(0,67,60),map);downed.jobs.StopAll();
            AccessTools.Method(typeof(Pawn_HealthTracker),"MakeDowned").Invoke(downed.health,new object?[]{null,null});
            Check("native-downed-pawn-has-prone-posture",downed.Downed&&downed.GetPosture()!=PawnPosture.Standing);
            shotTarget=downed;
            var proneShot=PolicyShot(gun,ProjectileHitFlags.All,true,812);
            Check("downed-target-uses-low-physical-aim",CombatService.Flight(proneShot)!.End.y>0 && CombatService.Flight(proneShot)!.End.y<CombatService.TopHeight(downed));
            Check("explicit-prone-target-keeps-native-damage-once",blastDamageCalls.GetValueOrDefault(downed.ThingID)==1);
            RemovePolicyPawn(downed);
            Check("shooter-never-intercepts-own-cross-floor-shot",blastDamageCalls.GetValueOrDefault(pawn.ThingID)==0);
        }
        finally {Find.Storyteller.difficulty.friendlyFireChanceFactor=oldFactor;}
        LayerView.Switch(owner,1);Find.Selector.ClearSelection();Find.CameraDriver.SetRootPosAndSize(At(1,80,60).ToVector3Shifted(),12);
        pendingScreenshot=Path.Combine(directory,"combat-policy.png");screenshotHold=3;stage=11;ticks=0;
    }

    private static void PolicyProbe(Thing equipment,string name,bool prevent,float factor,ProjectileHitFlags flags,Faction faction,int layer,bool expectHit)
    {
        Find.Storyteller.difficulty.friendlyFireChanceFactor=factor;
        Pawn victim=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Muffalo"),faction);
        GenSpawn.Spawn(victim,At(layer,80,60),map);victim.jobs.StopAll();
        shotTarget=BlastWall(0,95,60);
        int impactsBefore=owner.Combat.CommittedImpacts,attempts=0;
        for(;attempts<12;attempts++)
        {
            PolicyShot(equipment,flags,prevent,1123+attempts);
            if(blastDamageCalls.GetValueOrDefault(victim.ThingID)>0){attempts++;break;}
        }
        int calls=blastDamageCalls.GetValueOrDefault(victim.ThingID);
        Check(name,expectHit?calls==1:calls==0,$"calls={calls},attempts={attempts},body={victim.BodySize},floor={layer}");
        Check(name+"-each-projectile-commits-once",owner.Combat.CommittedImpacts-impactsBefore==attempts);
        if(!expectHit)Check(name+"-continues-to-real-target",blastDamageCalls.GetValueOrDefault(shotTarget.ThingID)==attempts);
        shotTarget.Destroy(DestroyMode.Vanish);RemovePolicyPawn(victim);
    }
    private static Projectile PolicyShot(Thing equipment,ProjectileHitFlags flags,bool prevent,int seed)
    {
        Rand.PushState(seed);
        try
        {
            var projectile=(Projectile)ThingMaker.MakeThing(shootingVerb.verbProps.defaultProjectile);
            GenSpawn.Spawn(projectile,pawn.Position,map);
            projectile.Launch(pawn,pawn.DrawPos,shotTarget,shotTarget,flags,prevent,equipment);
            var flight=CombatService.Flight(projectile)??throw new InvalidOperationException("No physical policy flight");
            AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)}).Invoke(projectile,new object[]{flight.TotalTicks});
            if(!projectile.Destroyed)throw new InvalidOperationException("Policy projectile did not terminate");
            return projectile;
        }
        finally{Rand.PopState();}
    }
    private static void RemovePolicyPawn(Pawn target)
    {
        if(target.Corpse is {} corpse)corpse.Destroy(DestroyMode.Vanish);
        else if(!target.Destroyed)target.Destroy(DestroyMode.Vanish);
    }
}
