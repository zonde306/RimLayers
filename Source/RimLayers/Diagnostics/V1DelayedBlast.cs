using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Vector3 delayedImpact;
    private static void BeginDelayedBlast()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        for(int x=65;x<=79;x++) for(int z=57;z<=63;z++)
        {
            IntVec3 cell=At(0,x,z);
            foreach(Thing thing in cell.GetThingList(map).ToList())
                if(thing is not Pawn) { if(thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
            map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65 && c.x<=79 && c.z>=57 && c.z<=63);
        shotTarget=BlastWall(0,75,60);shotHPBefore=shotTarget.HitPoints;
        shotProjectile=(Projectile)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Proj_GrenadeFrag"));
        GenSpawn.Spawn(shotProjectile,pawn.Position,map);
        shotProjectile.Launch(pawn,pawn.DrawPos,shotTarget,shotTarget,ProjectileHitFlags.All);
        Check("native-delayed-grenade-has-physical-arc",CombatService.Flight(shotProjectile) is {} flight && flight.ArcHeight>0);
        stage=50;ticks=0;
    }
    private static void RestoreDelayedBlast()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"combat-delay-baseline.json")));
        var b=baseline.RootElement;
        shotTarget=map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty("target").GetString());
        shotProjectile=map.listerThings.AllThings.OfType<Projectile>().Single(t=>t.ThingID==b.GetProperty("projectile").GetString());
        shotHPBefore=b.GetProperty("hitPoints").GetInt32();
        delayedImpact=new Vector3(b.GetProperty("x").GetSingle(),b.GetProperty("height").GetSingle(),b.GetProperty("z").GetSingle());
        var flight=CombatService.Flight(shotProjectile);
        Check("restart-restores-landed-impact-receipt",flight is {ImpactCommitted:true} &&
            Vector3.Distance(flight.ImpactPoint,delayedImpact)<0.001f);
        Check("restart-keeps-native-detonation-countdown",(int)AccessTools.Field(typeof(Projectile_Explosive),"ticksToDetonation").GetValue(shotProjectile)!>0);
        Check("restart-before-explosion-keeps-health",shotTarget.HitPoints==shotHPBefore);
        stage=51;ticks=0;
    }
    private static void DelayedBlastStep()
    {
        if(stage==50 && shotProjectile!=null && CombatService.Flight(shotProjectile) is {ImpactCommitted:true} flight && !shotProjectile.Destroyed)
        {
            delayedImpact=flight.ImpactPoint;
            Check("landed-grenade-awaits-native-delay",(int)AccessTools.Field(typeof(Projectile_Explosive),"ticksToDetonation").GetValue(shotProjectile)!>0);
            Check("landing-does-not-submit-explosion-damage",shotTarget.HitPoints==shotHPBefore && owner.Combat.Blasts.Count==0);
            pawn.Position=At(0,40,40);pawn.Notify_Teleported();
            GameDataSaveLoader.SaveGame("RimLayers_V1DelayedBlast");
            File.WriteAllText(Path.Combine(directory,"combat-delay-baseline.json"),JsonSerializer.Serialize(new {
                projectile=shotProjectile.ThingID,target=shotTarget.ThingID,hitPoints=shotHPBefore,
                x=delayedImpact.x,height=delayedImpact.y,z=delayedImpact.z
            }));
            Check("saved-after-landing-before-detonation",true);stage=51;ticks=0;
        }
        else if(stage==51 && owner.Combat.Blasts.FirstOrDefault(b=>!b.Explosion.Destroyed) is {} blast)
        {
            Check("delayed-explosion-retains-saved-impact-height",Vector3.Distance(blast.Origin,delayedImpact)<0.001f,
                $"blast={blast.Origin}, impact={delayedImpact}");
            testBlast=blast.Explosion;stage=52;ticks=0;
        }
        else if(stage==52 && testBlast.Destroyed)
        {
            Check("native-delayed-blast-damages-real-target",shotTarget.Destroyed || shotTarget.HitPoints<shotHPBefore);
            Check("delayed-blast-submits-target-damage-once",blastDamageCalls.GetValueOrDefault(shotTarget.ThingID)==1);
            Check("detonation-does-not-replay-impact-receipt",owner.Combat.CommittedImpacts==(mode=="generate"?1:0));
            LayerView.Switch(owner,0);Find.CameraDriver.SetRootPosAndSize(At(0,75,60).ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,mode+"-delayed-blast.png");screenshotHold=3;
            stage=11;ticks=0;
        }
        else if(ticks>600) { Check("delayed-detonation-finishes",false);Finish(); }
    }
}
