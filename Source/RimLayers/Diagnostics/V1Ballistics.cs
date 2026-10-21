using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void BeginBallisticTests()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(2,64,61);pawn.Notify_Teleported();
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        pawn.equipment.AddEquipment(gun);shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        for(int x=65;x<=97;x++) for(int z=58;z<=63;z++)
        {
            var cell=At(0,x,z);
            foreach(Thing thing in cell.GetThingList(map).ToList())
                if(thing is not Pawn) { if(thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
            map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65 && c.x<=97 && c.z>=58 && c.z<=63);
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,68,62),map);
        map.roofGrid.SetRoof(At(0,68,60),RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(1,68,60),RimLayersDefOf.RL_StructuralFloor);
        var obstacle=BlastWall(1,68,60);
        shotTarget=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Ship_ComputerCore"));
        GenSpawn.Spawn(shotTarget,At(0,70,60),map);shotTarget.SetFaction(Faction.OfPlayer);
        var solution=owner.Combat.Solve(pawn,pawn.Position,shotTarget,shootingVerb.EffectiveRange);
        Check("multicell-anchor-is-blocked-but-other-cell-exposed",solution.GeometricallyPossible &&
            owner.ToStorage(solution.AimCell)!=shotTarget.Position &&
            !owner.Combat.Solve(pawn,pawn.Position,new LocalTargetInfo(shotTarget.Position),shootingVerb.EffectiveRange).GeometricallyPossible);
        int before=shotTarget.HitPoints;
        var projectile=LaunchBallisticProbe(gun);
        var flight=CombatService.Flight(projectile)!;
        Check("launch-aims-at-exposed-occupied-cell",flight.End.z>=61);
        FinishBallisticProbe(projectile);
        Check("native-shot-hits-real-multicell-target",shotTarget.HitPoints<before,
            $"HP={before}->{shotTarget.HitPoints}, impact={flight.ImpactPoint}, blockerHP={obstacle.HitPoints}");
        shotTarget.Destroy(DestroyMode.Vanish);obstacle.Destroy(DestroyMode.Vanish);
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        shotTarget=BlastWall(0,95,60);before=shotTarget.HitPoints;
        projectile=LaunchBallisticProbe(gun);flight=CombatService.Flight(projectile)!;
        AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)}).Invoke(projectile,new object[]{1});
        // Place the fixture at this actual pawn's muzzle/flight crossing, not an assumed adult height.
        int slabX=(int)Math.Floor(flight.Start.x+(flight.End.x-flight.Start.x)*(6-flight.Start.y)/(flight.End.y-flight.Start.y));
        Check("dynamic-slab-intersects-snapshotted-flight",slabX>64&&slabX<95);
        // Insert a valid new slab only after the bullet has actually left the muzzle.
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,slabX,62),map);
        foreach(int z in new[]{60,62})
        {
            map.roofGrid.SetRoof(At(0,slabX,z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,slabX,z),RimLayersDefOf.RL_StructuralFloor);
        }
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(1,slabX,62),map);
        map.roofGrid.SetRoof(At(1,slabX,60),RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(2,slabX,60),RimLayersDefOf.RL_StructuralFloor);
        Check("late-slab-is-valid-supported-structure",owner.Structure.HasSurface(At(2,slabX,60)));
        FinishBallisticProbe(projectile);
        Check("in-flight-geometry-sees-new-slab",shotTarget.HitPoints==before && flight.ImpactLayer==2 && Math.Abs(flight.ImpactPoint.y-6)<0.001,$"muzzle={flight.Start},contact={flight.ImpactPoint},layer={flight.ImpactLayer},HP={shotTarget.HitPoints}/{before}");
        map.terrainGrid.SetTerrain(At(2,slabX,60),RimLayersDefOf.RL_OpenAir);
        map.roofGrid.SetRoof(At(1,slabX,60),null);
        projectile=LaunchBallisticProbe(gun);flight=CombatService.Flight(projectile)!;
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,95,62),map);
        map.roofGrid.SetRoof(At(0,95,60),RoofDefOf.RoofConstructed);
        map.terrainGrid.SetTerrain(At(1,95,60),RimLayersDefOf.RL_StructuralFloor);
        shotTarget.Position=At(1,95,60);
        FinishBallisticProbe(projectile);
        Check("airborne-shot-does-not-home-on-target-new-floor",shotTarget.HitPoints==before && flight.ImpactLayer==0);
        Check("three-impact-chains-submit-once",owner.Combat.CommittedImpacts==3,owner.Combat.CommittedImpacts.ToString());
        stage=11;ticks=0;
    }
    private static Projectile LaunchBallisticProbe(Thing equipment)
    {
        var projectile=(Projectile)ThingMaker.MakeThing(shootingVerb.verbProps.defaultProjectile);
        GenSpawn.Spawn(projectile,pawn.Position,map);
        projectile.Launch(pawn,pawn.DrawPos,shotTarget,shotTarget,ProjectileHitFlags.All,false,equipment);
        Check("ballistic-probe-has-real-flight",CombatService.Flight(projectile)!=null);
        return projectile;
    }
    private static void FinishBallisticProbe(Projectile projectile)
    {
        AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)})
            .Invoke(projectile,new object[]{CombatService.Flight(projectile)!.TotalTicks});
        Check("ballistic-probe-terminates",projectile.Destroyed);
    }
}
