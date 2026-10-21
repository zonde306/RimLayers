using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly List<object> grenadeLayerEvidence=new();
    private static Thing grenadeUpperVictim=null!,grenadeLowerVictim=null!;
    private static int grenadeUpperHp,grenadeLowerHp;
    private static bool grenadeBlastObserved;
    private static void BeginGrenadeLayers()
    {
        foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList()){p.jobs.StopAll();p.DeSpawn();}
        pawn=CombatAdult(Faction.OfPlayer);GenSpawn.Spawn(pawn,At(0,74,58),map);
        pawn.drafter.Drafted=true;pawn.drafter.FireAtWill=false;
        ClearCombatStrip(65,90,58);
        var weapon=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag"));
        pawn.equipment.AddEquipment(weapon);shootingVerb=weapon.GetComp<CompEquippable>().PrimaryVerb;
        var original=shootingVerb.verbProps;
        var precise=(VerbProperties)AccessTools.Method(typeof(object),"MemberwiseClone").Invoke(original,null)!;
        AccessTools.Field(typeof(VerbProperties),"forcedMissRadius").SetValue(precise,0f);precise.canGoWild=false;shootingVerb.verbProps=precise;
        // Native weapon cast + real Projectile_Explosive ticks. Only aim scatter is disabled
        // for the exact landing fixtures; the second group restores the unmodified weapon.
        GrenadeLayerProbe("up-one-platform",At(0,74,58),At(1,64,58),1);
        GrenadeLayerProbe("up-two-platforms",At(0,74,58),At(2,64,58),2);
        GrenadeLayerProbe("down-one-to-ground",At(1,64,58),At(0,74,58),0);
        GrenadeLayerProbe("down-two-to-ground",At(2,64,58),At(0,74,58),0);
        GrenadeLayerProbe("same-floor-open-sky",At(2,64,58),At(2,52,58),2);
        pawn.Position=At(0,60,58);pawn.Notify_Teleported();
        var blocked=owner.Combat.SolveWeapon(pawn,pawn.Position,At(1,63,58),shootingVerb);
        Check("grenade-cannot-throw-through-floor-from-below",!blocked.GeometricallyPossible&&blocked.Obstruction!=null,blocked.Obstruction.ToString()!);
        AccessTools.Field(typeof(Verb),"currentTarget").SetValue(shootingVerb,new LocalTargetInfo(At(1,63,58)));
        int count=owner.Combat.Flights.Count;
        bool fired=(bool)AccessTools.Method(typeof(Verb_LaunchProjectile),"TryCastShot").Invoke(shootingVerb,null)!;
        Check("floor-blocked-native-grenade-cast-does-not-launch",!fired&&owner.Combat.Flights.Count==count);
        // Launch into clear space, then insert a horizontal blocking surface across its arc.
        // It must land above that slab, not on the originally requested ground floor.
        GrenadeLayerProbe("new-upper-slab-intercepts-descending-grenade",At(2,64,58),At(0,74,58),2,lateSlab:true);
        shootingVerb.verbProps=original;
        var landedFloors=new HashSet<int>();
        Rand.PushState(20261021);
        try
        {
            for(int i=0;i<20;i++)
            {
                var f=GrenadeLayerProbe("native-scatter-"+i,At(0,74,58),At(2,64,58),null);
                if(f!=null)landedFloors.Add(f.ImpactLayer);
            }
        }
        finally{Rand.PopState();}
        Check("native-edge-scatter-can-land-on-upper-or-ground",landedFloors.Contains(0)&&landedFloors.Contains(2),string.Join(",",landedFloors));
        Check("grenade-range-remains-native-horizontal-range",Math.Abs(owner.Combat.EffectiveRange(shootingVerb,At(2,64,58),At(0,74,58))-original.range)<0.0001);
        shootingVerb.verbProps=precise;
        ExtendSecondFloorForCombat();
        GrenadeLayerProbe("second-to-third-platform",At(1,74,58),At(2,64,58),2);
        GrenadeLayerProbe("third-to-second-platform",At(2,64,58),At(1,74,58),1);
        File.WriteAllText(Path.Combine(directory,"grenade-layers.json"),JsonSerializer.Serialize(grenadeLayerEvidence,new JsonSerializerOptions{WriteIndented=true}));
        pawn.Position=At(1,74,58);pawn.Notify_Teleported();
        grenadeUpperVictim=BlastWall(2,63,59);grenadeLowerVictim=BlastWall(1,63,59);
        grenadeUpperHp=grenadeUpperVictim.HitPoints;grenadeLowerHp=grenadeLowerVictim.HitPoints;
        AccessTools.Field(typeof(Verb),"currentTarget").SetValue(shootingVerb,new LocalTargetInfo(At(2,64,58)));
        bool launched=(bool)AccessTools.Method(typeof(Verb_LaunchProjectile),"TryCastShot").Invoke(shootingVerb,null)!;
        Check("saved-platform-grenade-launched-by-native-weapon",launched);
        shotProjectile=owner.Combat.Flights.Last(f=>!f.Projectile.Destroyed&&!f.ImpactCommitted).Projectile;
        var flight=CombatService.Flight(shotProjectile)!;
        for(int i=0;i<flight.TotalTicks+5&&!flight.ImpactCommitted;i++)
            AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)}).Invoke(shotProjectile,new object[]{1});
        delayedImpact=flight.ImpactPoint;
        Check("saved-grenade-is-landed-on-upper-platform",flight.ImpactCommitted&&flight.ImpactLayer==2);
        GameDataSaveLoader.SaveGame("RimLayers_V1Grenade");
        File.WriteAllText(Path.Combine(directory,"grenade-baseline.json"),JsonSerializer.Serialize(new {
            projectile=shotProjectile.ThingID,upper=grenadeUpperVictim.ThingID,lower=grenadeLowerVictim.ThingID,
            upperHp=grenadeUpperHp,lowerHp=grenadeLowerHp,x=delayedImpact.x,y=delayedImpact.y,z=delayedImpact.z}));
        GrenadeLandingPicture();stage=412;ticks=0;
    }
    private static void GrenadeLandingPicture()
    {
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(At(2,64,58).ToVector3Shifted(),10);
        Find.Selector.ClearSelection();Find.Selector.Select(shotProjectile);
        pendingScreenshot=Path.Combine(directory,mode+"-grenade-landed.png");screenshotHold=3;
    }
    private static void RestoreGrenadeLayers()
    {
        using var file=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"grenade-baseline.json")));
        var b=file.RootElement;
        Thing Get(string name)=>map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty(name).GetString());
        shotProjectile=(Projectile)Get("projectile");grenadeUpperVictim=Get("upper");grenadeLowerVictim=Get("lower");
        grenadeUpperHp=b.GetProperty("upperHp").GetInt32();grenadeLowerHp=b.GetProperty("lowerHp").GetInt32();
        delayedImpact=new(b.GetProperty("x").GetSingle(),b.GetProperty("y").GetSingle(),b.GetProperty("z").GetSingle());
        var f=CombatService.Flight(shotProjectile);
        Check("restart-keeps-upper-grenade-contact-and-fuse",f is {ImpactCommitted:true,ImpactLayer:2}&&
            UnityEngine.Vector3.Distance(f.ImpactPoint,delayedImpact)<0.001&&
            (int)AccessTools.Field(typeof(Projectile_Explosive),"ticksToDetonation").GetValue(shotProjectile)!>0);
        GrenadeLandingPicture();stage=412;ticks=0;
    }
    private static void GrenadeLayersStep()
    {
        if(!grenadeBlastObserved&&owner.Combat.Blasts.FirstOrDefault(b=>!b.Explosion.Destroyed) is {} blast)
        {
            grenadeBlastObserved=true;
            Check("live-map-grenade-detonates-at-saved-upper-contact",UnityEngine.Vector3.Distance(blast.Origin,delayedImpact)<0.001&&
                owner.TryDecode(blast.Explosion.Position,out var c)&&c.LayerId==2);
        }
        if(ticks<300)return;
        Check("native-grenade-blast-damages-upper-floor-object",grenadeUpperVictim.Destroyed||grenadeUpperVictim.HitPoints<grenadeUpperHp);
        Check("intact-floor-protects-object-directly-below",!grenadeLowerVictim.Destroyed&&grenadeLowerVictim.HitPoints==grenadeLowerHp);
        Check("native-map-fuse-finishes-once",grenadeBlastObserved&&shotProjectile!.Destroyed&&owner.Combat.Blasts.All(b=>b.Explosion.Destroyed));
        pendingScreenshot=Path.Combine(directory,mode+"-grenade-exploded.png");screenshotHold=3;stage=11;ticks=0;
    }
    private static LayerFlight? GrenadeLayerProbe(string name,IntVec3 source,IntVec3 target,int? expectedLayer,bool lateSlab=false)
    {
        pawn.Position=source;pawn.Notify_Teleported();
        AccessTools.Field(typeof(Verb),"currentTarget").SetValue(shootingVerb,new LocalTargetInfo(target));
        bool fired=(bool)AccessTools.Method(typeof(Verb_LaunchProjectile),"TryCastShot").Invoke(shootingVerb,null)!;
        Check(name+"-native-throw",fired);
        if(!fired)return null;
        var f=owner.Combat.Flights.Last(f=>!f.Projectile.Destroyed&&!f.ImpactCommitted);
        var p=f.Projectile;
        var lateCells=new List<IntVec3>();
        if(lateSlab)
        {
            // Solve y(t)=6 independently from the sampled collision solver. Native precise
            // casts still jitter within a cell, so a fixed x strip can miss the real crossing.
            double linear=f.End.y-f.Start.y+4*f.ArcHeight;
            double t=(linear+Math.Sqrt(linear*linear+16*f.ArcHeight*(f.Start.y-6)))/(8*f.ArcHeight);
            int x=(int)Math.Floor(f.Start.x+(f.End.x-f.Start.x)*t);
            int z=(int)Math.Floor(f.Start.z+(f.End.z-f.Start.z)*t);
            Check(name+"-descending-plane-crossing",t>0&&t<1,$"t={t},x={x},z={z}");
            for(int dx=-1;dx<=1;dx++)
            {
                var c=At(2,x+dx,z);lateCells.Add(c);
                map.roofGrid.SetRoof(At(1,x+dx,z),RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(c,RimLayersDefOf.RL_StructuralFloor);
            }
        }
        for(int i=0;i<f.TotalTicks+5&&!p.Destroyed&&!f.ImpactCommitted;i++)AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)}).Invoke(p,new object[]{1});
        Check(name+"-lands-on-physical-layer",f.ImpactCommitted&&(!expectedLayer.HasValue||f.ImpactLayer==expectedLayer.Value)&&
            owner.TryDecode(p.Position,out var position)&&position.LayerId==f.ImpactLayer,
            $"expected={expectedLayer},actual={f.ImpactLayer},start={f.Start},end={f.End},impact={f.ImpactPoint},cell={p.Position}");
        Check(name+"-native-fuse-survives-landing",!p.Destroyed&&(int)AccessTools.Field(typeof(Projectile_Explosive),"ticksToDetonation").GetValue(p)!>0);
        int committed=owner.Combat.CommittedImpacts;
        for(int i=0;i<120&&!p.Destroyed;i++)AccessTools.Method(typeof(Projectile),"TickInterval",new[]{typeof(int)}).Invoke(p,new object[]{1});
        var blast=owner.Combat.Blasts.LastOrDefault(b=>!b.Explosion.Destroyed);
        Check(name+"-explodes-on-same-physical-contact",p.Destroyed&&blast!=null&&
            UnityEngine.Vector3.Distance(blast.Origin,f.ImpactPoint)<0.001&&owner.TryDecode(blast.Explosion.Position,out var explosionCell)&&explosionCell.LayerId==f.ImpactLayer&&
            owner.Combat.CommittedImpacts==committed,$"impact={f.ImpactPoint},explosion={blast?.Origin}/{blast?.Explosion.Position}");
        grenadeLayerEvidence.Add(new{name,expectedLayer,actual=f.ImpactLayer,start=f.Start.ToString(),aim=f.End.ToString(),impact=f.ImpactPoint.ToString(),
            projectileCell=p.Position.ToString(),explosionCell=blast?.Explosion.Position.ToString(),explosionOrigin=blast?.Origin.ToString()});
        // These probes verify launch/contact/fuse, not repeat explosion damage on the shared fixture.
        foreach(var b in owner.Combat.Blasts.ToList())if(!b.Explosion.Destroyed)b.Explosion.Destroy();
        if(!p.Destroyed)p.Destroy();owner.Combat.Tick();
        foreach(var cell in lateCells)
        {
            owner.TryDecode(cell,out var c);map.terrainGrid.SetTerrain(cell,RimLayersDefOf.RL_OpenAir);
            map.roofGrid.SetRoof(At(1,c.X,c.Z),null);
        }
        return f;
    }
}
