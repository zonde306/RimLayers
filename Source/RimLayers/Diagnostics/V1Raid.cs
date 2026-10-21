using System.Text.Json;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly List<Pawn> raidEnemies=new(),raidDefenders=new();
    private static readonly Dictionary<string,int> raidLastShot=new();
    private static readonly HashSet<string> raidFlights=new(),raidShooters=new(),raidUpperVisitors=new();
    private static readonly List<object> raidTrace=new(),raidHits=new();
    private static Building_TurretGun raidTurret=null!;
    private static Faction raidFaction=null!;
    private static int raidUpShots,raidDownShots,raidTurretShots;
    private static bool RaidIsolated=>scenario=="combat-raid-isolated";

    private static void BeginRaid()
    {
        // The fixture supplies a colony, not jobs/targets to the attackers. The incident
        // generates its own raiders, arrival, Lord, duties, target selection and retreat.
        foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList()) { p.jobs.StopAll();p.DeSpawn(); }
        for(int x=25;x<=115;x++)for(int z=25;z<=90;z++)
        {
            var c=At(0,x,z);
            if(x>=50&&x<=64&&z>=50&&z<=64)continue;
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn)
            {if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        Rand.PushState(20261020);
        try
        {
            for(int i=0;i<2;i++)
            {
                var p=CombatAdult(Faction.OfPlayer);GenSpawn.Spawn(p,At(2,64,56+i*4),map);
                p.drafter.Drafted=true;p.equipment.DestroyAllEquipment(DestroyMode.Vanish);
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle")));
                p.skills.GetSkill(SkillDefOf.Shooting).Level=10;raidDefenders.Add(p);
            }
        }
        finally {Rand.PopState();}
        pawn=raidDefenders[0];
        raidTurret=(Building_TurretGun)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"),ThingDefOf.Steel);
        GenSpawn.Spawn(raidTurret,At(2,64,63),map);raidTurret.SetFaction(Faction.OfPlayer);
        var battery=UtilityBuilding("Battery",2,61,62);battery.GetComp<CompPowerBattery>().AddEnergy(600);
        for(int x=61;x<=64;x++)UtilityBuilding("PowerConduit",2,x,62);
        FlushUtilityGraph();
        if(RaidIsolated)
            foreach(var s in map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<Building_Stairs>()
                .Where(s=>owner.TryDecode(s.Position,out var c)&&c.LayerId==1).ToList())s.Destroy(DestroyMode.Vanish);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        raidFaction=Find.FactionManager.AllFactions.First(f=>f.def.defName.StartsWith("Pirate")&&!f.defeated);
        var before=map.mapPawns.AllPawnsSpawned.ToHashSet();
        int raidSeed=17;
        if(GenCommandLine.TryGetCommandLineArg("rimlayersraidseed",out var seedValue))raidSeed=int.Parse(seedValue);
        var parms=new IncidentParms {target=map,points=1000,faction=raidFaction,forced=true,
            raidStrategy=RaidStrategyDefOf.ImmediateAttack,raidArrivalMode=PawnsArrivalModeDefOf.EdgeWalkIn,
            spawnCenter=At(0,112,58),spawnRotation=Rot4.West,pawnGroupMakerSeed=scenario=="combat-raid-melee"?20261020:raidSeed,
            dontUseSingleUseRocketLaunchers=true};
        if(scenario=="combat-raid-guns")
        {
            var group=IncidentParmsUtility.GetDefaultPawnGroupMakerParms(PawnGroupKindDefOf.Combat,parms);
            for(int candidate=1;candidate<=100;candidate++)
            {
                group.seed=candidate;
                if(PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(group,out var maker)&&maker.options.Any(o=>o.kind.defName=="Pirate"))
                {parms.pawnGroupMakerSeed=candidate;break;}
            }
        }
        bool executed;
        Rand.PushState(172020);try {executed=IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);}finally {Rand.PopState();}
        raidEnemies.AddRange(map.mapPawns.AllPawnsSpawned.Where(p=>!before.Contains(p)&&p.Faction==raidFaction));
        Check("real-raid-enemy-incident-executes",executed,$"count={raidEnemies.Count},points={parms.points},seed={parms.pawnGroupMakerSeed}");
        Check("incident-creates-native-assault-lord",raidEnemies.Count>=3&&raidEnemies.All(p=>p.GetLord()?.LordJob is LordJob_AssaultColony),
            string.Join(";",raidEnemies.Select(p=>$"{p.ThingID}:{p.kindDef.defName}/{p.equipment?.Primary?.def.defName}/{p.GetLord()?.LordJob.GetType().Name}")));
        Check("raid-arrives-on-ground",raidEnemies.All(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId==0));
        Check("raid-route-matches-fixture",raidEnemies.Count>0&&raidEnemies[0].CanReach(pawn,PathEndMode.Touch,Danger.Deadly)==!RaidIsolated);
        GameDataSaveLoader.SaveGame("RimLayers_V1RaidStart");
        stage=410;ticks=0;RaidSnapshot();
    }
    private static readonly Dictionary<Pawn,IntVec3> stalledRaidPawns=new();
    private static int recoveredRaidShots;
    private static void AuditSavedRaid()
    {
        var facts=new List<object>();int disagreements=0;
        foreach(var p in map.mapPawns.AllPawnsSpawned.Where(p=>p.HostileTo(Faction.OfPlayer)&&!p.Downed&&p.CurrentEffectiveVerb is Verb_LaunchProjectile))
        {
            var target=p.mindState.enemyTarget;if(target?.Spawned!=true)continue;
            var verb=p.CurrentEffectiveVerb;
            bool hit=verb.CanHitTarget(target),see=AttackTargetFinder.CanSee(p,target);
            var auto=AttackTargetFinder.BestShootTargetFromCurrentPosition(p,TargetScanFlags.NeedLOSToAll|TargetScanFlags.NeedThreat|TargetScanFlags.NeedAutoTargetable);
            facts.Add(new{id=p.ThingID,job=p.CurJob?.def.defName,position=p.Position.ToString(),target=target.ThingID,hit,see,auto=auto?.Thing.ThingID,busy=p.stances.FullBodyBusy});
            if(p.CurJob?.def==JobDefOf.Wait_Combat&&hit&&!see&&auto==null&&!p.stances.FullBodyBusy)
            {disagreements++;stalledRaidPawns.Add(p,p.Position);}
        }
        File.WriteAllText(Path.Combine(directory,"saved-raid-audit.json"),JsonSerializer.Serialize(facts,new JsonSerializerOptions{WriteIndented=true}));
        Check("saved-raid-reproduces-five-arc-only-waiting-pawns",disagreements==5,JsonSerializer.Serialize(facts));
        stage=413;ticks=0;RaidPicture("stalled-raid-before",0);
    }
    private static void RaidRecoveryStep()
    {
        foreach(var f in owner.Combat.Flights)
            if(raidFlights.Add(f.Projectile.ThingID)&&(f.Emitter??f.Projectile.Launcher) is Pawn shooter&&stalledRaidPawns.ContainsKey(shooter))recoveredRaidShots++;
        if(ticks<2400)return;
        var facts=stalledRaidPawns.Select(pair=>new {id=pair.Key.ThingID,start=pair.Value.ToString(),position=pair.Key.Position.ToString(),
            dead=pair.Key.Dead,spawned=pair.Key.Spawned,job=pair.Key.CurJob?.def.defName,target=pair.Key.mindState?.enemyTarget?.ThingID,
            moved=pair.Key.Position!=pair.Value}).ToArray();
        Check("all-previously-stalled-throwers-leave-unusable-firing-cell",facts.All(p=>p.moved||p.dead||!p.spawned),JsonSerializer.Serialize(facts));
        Check("recovered-native-ai-really-throws-grenades",recoveredRaidShots>0,$"shots={recoveredRaidShots}");
        bool stalled=stalledRaidPawns.Keys.Any(p=>p.Spawned&&!p.Dead&&!p.Downed&&p.CurJob?.def==JobDefOf.Wait_Combat&&
            p.mindState.enemyTarget is {Spawned:true} t&&p.CurrentEffectiveVerb is {} v&&v.CanHitTarget(t)&&!AttackTargetFinder.CanSee(p,t));
        Check("no-arc-only-waiting-loop-after-native-ai-resumes",!stalled);
        File.WriteAllText(Path.Combine(directory,"raid-recovery.json"),JsonSerializer.Serialize(new {ticks,recoveredRaidShots,facts},new JsonSerializerOptions{WriteIndented=true}));
        GameDataSaveLoader.SaveGame("RimLayers_V1RaidRecovered");RaidPicture("stalled-raid-after",0);stage=11;ticks=0;
    }
    private static void RaidDamage(Thing victim,DamageInfo d)
    {
        if(stage!=410||d.Instigator==null)return;
        if(d.Instigator is Pawn attacker&&(raidEnemies.Contains(attacker)||raidDefenders.Contains(attacker))||d.Instigator==raidTurret)
            raidHits.Add(new {tick=ticks,victim=victim.ThingID,source=d.Instigator.ThingID,damage=d.Def.defName,amount=d.Amount,
                sourceCell=d.Instigator.Position.ToString(),victimCell=victim.Position.ToString()});
    }
    private static void RaidSnapshot()
    {
        raidTrace.Add(new {tick=ticks,pawns=raidEnemies.Concat(raidDefenders).Select(p=>new {
            id=p.ThingID,position=p.Position.ToString(),spawned=p.Spawned,dead=p.Dead,down=p.Downed,
            job=p.CurJob?.def.defName,target=p.CurJob?.targetA.ToString(),enemy=p.mindState?.enemyTarget?.ThingID,
            duty=p.mindState?.duty?.def.defName,lord=p.GetLord()?.CurLordToil?.GetType().Name,
            injuries=InjurySum(p)}).ToArray(),turret=new {target=raidTurret.CurrentTarget.ToString(),dead=raidTurret.Destroyed},
            upShots=raidUpShots,downShots=raidDownShots,turretShots=raidTurretShots});
        File.WriteAllText(Path.Combine(directory,"raid-trace.json"),JsonSerializer.Serialize(new {raidTrace,raidHits},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void RaidPicture(string name,int layer)
    {
        LayerView.Switch(owner,layer);Find.Selector.ClearSelection();
        Find.CameraDriver.SetRootPosAndSize(At(layer,66,58).ToVector3Shifted(),25);
        pendingScreenshot=Path.Combine(directory,name+".png");screenshotHold=3;
    }
    private static void RaidStep()
    {
        foreach(var f in owner.Combat.Flights)
        {
            if(!raidFlights.Add(f.Projectile.ThingID))continue;
            var emitter=f.Emitter??f.Projectile.Launcher;
            if(emitter is Pawn p&&raidEnemies.Contains(p)) {raidShooters.Add(p.ThingID);raidLastShot[p.ThingID]=ticks;if(f.End.y>f.Start.y+1)raidUpShots++;}
            if(emitter is Pawn q&&raidDefenders.Contains(q)&&f.End.y<f.Start.y-1)raidDownShots++;
            if(emitter==raidTurret)raidTurretShots++;
        }
        foreach(var p in raidEnemies)if(p.Spawned&&owner.TryDecode(p.Position,out var c)&&c.LayerId==2)raidUpperVisitors.Add(p.ThingID);
        if(ticks%120==0)RaidSnapshot();
        if(ticks==600)RaidPicture("raid-ground-engagement",0);
        if(ticks==1000)RaidPicture("raid-upper-defense",2);
        bool resolved=raidEnemies.All(p=>!p.Spawned||p.Dead||p.Downed);
        if(ticks<10000&&!(ticks>1800&&resolved))return;
        RaidSnapshot();
        var aiming=raidEnemies.Where(p=>p.Spawned&&!p.Dead&&!p.Downed&&p.mindState.enemyTarget?.Spawned==true&&p.CurrentEffectiveVerb is Verb_LaunchProjectile)
            .Select(p=>new {id=p.ThingID,job=p.CurJob?.def.defName,target=p.mindState.enemyTarget.ThingID,
                canHit=p.CurrentEffectiveVerb.CanHitTarget(p.mindState.enemyTarget),
                canSee=AttackTargetFinder.CanSee(p,p.mindState.enemyTarget),
                auto=AttackTargetFinder.BestShootTargetFromCurrentPosition(p,TargetScanFlags.NeedLOSToAll|TargetScanFlags.NeedThreat|TargetScanFlags.NeedAutoTargetable)?.Thing.ThingID,
                lastShot=raidLastShot.GetValueOrDefault(p.ThingID),busy=p.stances.FullBodyBusy}).ToArray();
        File.WriteAllText(Path.Combine(directory,"raid-target-diagnostics.json"),JsonSerializer.Serialize(aiming,new JsonSerializerOptions{WriteIndented=true}));
        Check("no-arc-only-stationary-autofire-stall",!aiming.Any(p=>p.job=="Wait_Combat"&&p.canHit&&!p.canSee&&p.auto==null&&!p.busy&&ticks-p.lastShot>3000),JsonSerializer.Serialize(aiming));
        if(scenario!="combat-raid-melee")Check("raid-ranged-ai-fires-upward-without-assigned-jobs",raidUpShots>0,$"shots={raidUpShots},shooters={string.Join(",",raidShooters)}");
        Check("drafted-defenders-autonomously-fire-down",raidDownShots>0,$"shots={raidDownShots}");
        Check("automatic-turret-participates-in-real-raid",raidTurretShots>0,$"shots={raidTurretShots}");
        Check("raid-produces-native-combat-damage",raidHits.Count>0,$"hits={raidHits.Count}");
        if(RaidIsolated)Check("isolated-platform-is-not-entered-by-raiders",raidUpperVisitors.Count==0);
        else Check("native-raid-completes-or-reaches-upper-defense",resolved||raidUpperVisitors.Count>0,$"resolved={resolved},visitors={raidUpperVisitors.Count}");
        Check("raid-has-observable-resolution",resolved||raidDefenders.All(p=>p.Dead||p.Downed||!p.Spawned),
            $"ticks={ticks},enemyActive={raidEnemies.Count(p=>p.Spawned&&!p.Dead&&!p.Downed)},enemyDown={raidEnemies.Count(p=>p.Downed)},enemyDead={raidEnemies.Count(p=>p.Dead)},defenderDown={raidDefenders.Count(p=>p.Downed||p.Dead)}");
        GameDataSaveLoader.SaveGame("RimLayers_V1RaidEnd");RaidPicture("raid-final",0);stage=11;ticks=0;
    }
}
