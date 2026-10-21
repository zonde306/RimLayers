using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void ExtendSecondFloorForCombat()
    {
        ClearCombatStrip(65,90,58);
        for(int x=66;x<=86;x+=5)for(int z=56;z<=60;z+=4)
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,x,z),map);
        for(int x=65;x<=88;x++)for(int z=56;z<=60;z++)
        {
            map.roofGrid.SetRoof(At(0,x,z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,x,z),RimLayersDefOf.RL_StructuralFloor);map.fogGrid.Unfog(At(1,x,z));
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
    }
    private static void BeginUpperCombat()
    {
        foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList()){p.jobs.StopAll();p.DeSpawn();}
        ExtendSecondFloorForCombat();
        var low=CombatAdult(Faction.OfPlayer);var high=CombatAdult(Faction.OfAncientsHostile);
        GenSpawn.Spawn(low,At(1,84,58),map);GenSpawn.Spawn(high,At(2,63,58),map);
        low.drafter.Drafted=true;low.drafter.FireAtWill=false;
        pawn=low;frozenCombatPawn=high;
        Verb Arm(Pawn p)
        {
            p.equipment.DestroyAllEquipment(DestroyMode.Vanish);
            var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));p.equipment.AddEquipment(gun);
            var v=gun.GetComp<CompEquippable>().PrimaryVerb;
            var properties=(VerbProperties)AccessTools.Method(typeof(object),"MemberwiseClone").Invoke(v.verbProps,null)!;
            properties.canGoWild=false;v.verbProps=properties;p.skills.GetSkill(SkillDefOf.Shooting).Level=20;return v;
        }
        var upVerb=Arm(low);var downVerb=Arm(high);
        var cover=GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Sandbags"),ThingDefOf.Cloth),At(2,64,58),map);
        foreach(var s in map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<Building_Stairs>()
            .Where(s=>owner.TryDecode(s.Position,out var c)&&c.LayerId==1).ToList())s.Destroy(DestroyMode.Vanish);
        Check("second-and-third-floor-have-no-walking-route",!low.CanReach(high,PathEndMode.Touch,Danger.Deadly));
        var up=owner.Combat.SolveWeapon(low,low.Position,high,upVerb);
        var down=owner.Combat.SolveWeapon(high,high.Position,low,downVerb);
        Check("second-to-third-range-90-percent",Math.Abs(up.EffectiveRange-upVerb.EffectiveRange*0.9f)<0.0001);
        Check("third-to-second-range-110-percent",Math.Abs(down.EffectiveRange-downVerb.EffectiveRange*1.1f)<0.0001);
        Check("second-to-third-sees-partial-body-above-sandbags",up.GeometricallyPossible&&up.Exposure>0&&up.Exposure<1,$"exposure={up.Exposure}");
        Check("third-to-second-muzzle-clears-own-sandbags",down.GeometricallyPossible,$"exposure={down.Exposure}");
        Check("upper-floor-native-hit-report-matches-exposure",Math.Abs(ShotReport.HitReportFor(low,upVerb,high).PassCoverChance-up.Exposure)<0.0001);
        var original=low.Position;low.Position=At(1,60,58);low.Notify_Teleported();
        var blocked=owner.Combat.SolveWeapon(low,low.Position,high,upVerb);
        Check("second-to-third-intact-floor-blocks-shot",!blocked.GeometricallyPossible&&blocked.Obstruction?.Kind is ObstructionKind.Floor or ObstructionKind.Roof,blocked.Obstruction.ToString()!);
        AccessTools.Field(typeof(Verb),"currentTarget").SetValue(upVerb,new LocalTargetInfo(high));
        Check("second-to-third-floor-blocked-native-cast-refuses",!(bool)AccessTools.Method(typeof(Verb_LaunchProjectile),"TryCastShot").Invoke(upVerb,null)!);
        low.Position=At(1,65,58);low.Notify_Teleported();
        Check("steep-third-to-second-fire-blocked-by-own-cover",!owner.Combat.SolveWeapon(high,high.Position,low,downVerb).GeometricallyPossible);
        low.Position=original;low.Notify_Teleported();
        Rand.PushState(20261022);
        try
        {
            int coverHits=0,bodyHits=0;
            for(int i=0;i<24&&(coverHits==0||bodyHits==0);i++)
            {
                int hp=cover.HitPoints;float injuries=InjurySum(high);
                var flight=UpperNativeShot(upVerb,high);
                if(flight==null)break;
                Check("upward-bullet-impacts-third-floor-"+i,flight.ImpactLayer==2,$"impact={flight.ImpactPoint}");
                if(cover.HitPoints<hp)coverHits++;
                if(InjurySum(high)>injuries)bodyHits++;
            }
            Check("upward-shots-hit-real-third-floor-cover-and-pawn",coverHits>0&&bodyHits>0,$"cover={coverHits},body={bodyHits}");
            float before=InjurySum(low);
            for(int i=0;i<12&&InjurySum(low)==before;i++)
            {
                var flight=UpperNativeShot(downVerb,low);
                if(flight==null)break;
                Check("downward-bullet-impacts-second-floor-"+i,flight.ImpactLayer==1,$"impact={flight.ImpactPoint}");
            }
            Check("downward-shot-damages-real-second-floor-pawn",InjurySum(low)>before);
        }
        finally{Rand.PopState();}
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(At(2,68,58).ToVector3Shifted(),18);
        pendingScreenshot=Path.Combine(directory,"upper-floor-combat.png");screenshotHold=3;stage=11;ticks=0;
    }
    private static LayerFlight? UpperNativeShot(Verb verb,Thing target)
    {
        AccessTools.Field(typeof(Verb),"currentTarget").SetValue(verb,new LocalTargetInfo(target));
        bool fired=(bool)AccessTools.Method(typeof(Verb_LaunchProjectile),"TryCastShot").Invoke(verb,null)!;
        Check("upper-floor-native-weapon-fires",fired);if(!fired)return null;
        var f=owner.Combat.Flights.Last(f=>!f.Projectile.Destroyed);FinishBallisticProbe(f.Projectile);return f;
    }
}
