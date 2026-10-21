using System.Reflection;
using HarmonyLib;
using RimLayers.Combat;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void CheckMortarAmmunition(Building_TurretGun mortar)
    {
        mortar.Position=At(2,64,58);
        var predicate=typeof(Building_TurretGun).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)
            .SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
            .Single(m=>m.Name.Contains("<TryFindNewTarget>")&&m.ReturnType==typeof(bool));
        object closure=Activator.CreateInstance(predicate.DeclaringType!,true)!;
        AccessTools.Field(predicate.DeclaringType,"<>4__this").SetValue(closure,mortar);
        AccessTools.Field(predicate.DeclaringType,"range").SetValue(closure,mortar.AttackVerb.EffectiveRange);
        var previous=(CombatCoordinates.Context,LayerTurretAcquisitionPatch.Current);
        CombatCoordinates.Context=map;LayerTurretAcquisitionPatch.Current=mortar;
        try
        {
            var near=(Building)BlastWall(0,80,58);var far=(Building)BlastWall(0,105,58);
            bool Test(Building t)=>(bool)predicate.Invoke(closure,new object[]{t})!;
            Check("hostile-mortar-building-branch-preserves-horizontal-minimum",!Test(near));
            Check("hostile-mortar-building-branch-accepts-visible-other-floor",Test(far));
            for(int x=99;x<=107;x++)for(int z=57;z<=59;z++)map.roofGrid.SetRoof(At(0,x,z),RoofDefOf.RoofConstructed);
            Check("hostile-mortar-building-branch-does-not-lock-hidden-entity",!Test(far));
            var area=owner.Combat.SolveWeapon(mortar,mortar.Position,new LocalTargetInfo(far.Position),mortar.AttackVerb);
            Check("roof-intercept-remains-legal-area-order",area.GeometricallyPossible&&area.Obstruction?.Kind==ObstructionKind.Roof,$"clear={area.Clear},range={area.InRange},launch={area.LaunchBlocked},hit={area.Obstruction}");
            for(int x=99;x<=107;x++)for(int z=57;z<=59;z++)map.roofGrid.SetRoof(At(0,x,z),null);
            near.Destroy(DestroyMode.Vanish);far.Destroy(DestroyMode.Vanish);
        }
        finally{(CombatCoordinates.Context,LayerTurretAcquisitionPatch.Current)=previous;}
        Rand.PushState(661204);
        try
        {
            foreach(string name in new[]{"HighExplosive","Incendiary","EMP","Smoke","Firefoam"})
            {
                var definition=DefDatabase<ThingDef>.GetNamed("Shell_"+name).projectileWhenLoaded;
                var victim=name=="EMP" ? (Thing)PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Mech_Scyther"),Faction.OfMechanoids)
                    : ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.WoodLog);
                GenSpawn.Spawn(victim,At(0,106,58),map);int hp=victim.HitPoints;
                Fire? fire=name=="Firefoam"?(Fire)GenSpawn.Spawn(ThingDefOf.Fire,At(0,105,58),map):null;
                if(fire!=null)fire.fireSize=1;
                var projectile=(Projectile)GenSpawn.Spawn(definition,mortar.Position,map);
                projectile.Launch(mortar,mortar.DrawPos,At(0,105,58),At(0,105,58),ProjectileHitFlags.All,false,mortar.gun);
                var flight=CombatService.Flight(projectile)!;
                FinishBallisticProbe(projectile);
                var blast=owner.Combat.Blasts.Single(b=>!b.Explosion.Destroyed);
                Check(name+"-shell-retains-native-effect-at-real-impact",blast.Explosion.damType==definition.projectile.damageDef&&blast.Origin.y<3&&blast.Explosion.Position==At(0,105,58));
                BlastAccess.Started(blast.Explosion)=Find.TickManager.TicksGame-1000;
                AccessTools.Method(typeof(Explosion),"Tick").Invoke(blast.Explosion,null);
                bool effect=name switch {
                    "HighExplosive"=>victim.Destroyed||victim.HitPoints<hp,
                    "Incendiary"=>victim.HasAttachment(ThingDefOf.Fire)||map.listerThings.ThingsOfDef(ThingDefOf.Fire).Any()||map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("Filth_Fuel")).Any(),
                    "EMP"=>((Pawn)victim).stances.stunner.Stunned,
                    "Smoke"=>At(0,105,58).AnyGas(map,GasType.BlindSmoke),
                    "Firefoam"=>fire!.Destroyed&&At(0,105,58).GetThingList(map).Any(t=>t.def.defName=="Filth_FireFoam"),
                    _=>false};
                Check(name+"-shell-applies-native-outcome",effect);
                if(victim is Pawn p)RemovePolicyPawn(p);else if(!victim.Destroyed)victim.Destroy(DestroyMode.Vanish);
                foreach(Fire f in map.listerThings.ThingsOfDef(ThingDefOf.Fire).OfType<Fire>().ToList())f.Destroy(DestroyMode.Vanish);
                foreach(var wave in owner.Combat.Blasts.ToList())if(!wave.Explosion.Destroyed)wave.Explosion.Destroy();
            }
        }
        finally{Rand.PopState();}
    }
}
