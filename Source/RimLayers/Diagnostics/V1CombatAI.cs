using System.Diagnostics;
using HarmonyLib;
using RimLayers.Combat;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn? frozenCombatPawn;
    private static Pawn combatDummy=null!;
    private static int aiImpactBaseline;
    private static IntVec3 aiMoveStart;
    private static bool AllowCombatPawnTick(Pawn __instance)=>__instance!=frozenCombatPawn;

    private static void BeginCombatAI()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        var gun=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        pawn.equipment.AddEquipment(gun);
        shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        var properties=(VerbProperties)AccessTools.Method(typeof(object),"MemberwiseClone").Invoke(shootingVerb.verbProps,null)!;
        properties.canGoWild=false;properties.warmupTime=0.5f;shootingVerb.verbProps=properties;
        pawn.skills.GetSkill(SkillDefOf.Shooting).Level=20;
        combatDummy=map.mapPawns.FreeColonistsSpawned.First(p=>p!=pawn);
        combatDummy.jobs.StopAll();combatDummy.Position=At(0,70,60);combatDummy.Notify_Teleported();
        frozenCombatPawn=combatDummy; // A stationary real target; only this test profile suppresses its ticks.
        foreach(var cell in new CellRect(65,58,8,5))
        {
            IntVec3 location=At(0,cell.x,cell.z);
            foreach(Thing thing in location.GetThingList(map).ToList())
                if(thing is not Pawn) { if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
            map.roofGrid.SetRoof(location,null);map.terrainGrid.SetTerrain(location,TerrainDefOf.Concrete);
            map.fogGrid.Unfog(location);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65 && c.x<=72 && c.z>=58 && c.z<=62);
        pawn.drafter.Drafted=false;
        pawn.SetFaction(Faction.OfAncientsHostile);
        pawn.jobs.StopAll();
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var watch=Stopwatch.StartNew();
        var request=new CastPositionRequest {
            caster=pawn,target=combatDummy,verb=shootingVerb,maxRangeFromTarget=shootingVerb.EffectiveRange,
            maxRangeFromCaster=25,maxRangeFromLocus=2,locus=At(1,64,60),maxRegions=100,
            validator=c=>owner.TryDecode(c,out var logical) && logical.LayerId==1
        };
        bool found=CastPositionFinder.TryFindCastPosition(request,out var firing);
        Check("native-cast-finder-searches-other-floor",found && owner.TryDecode(firing,out var selected) && selected.LayerId==1,
            $"cell={firing}, ms={watch.Elapsed.TotalMilliseconds:F1}");
        Check("firing-cell-honors-locus-and-validator",found && request.validator(firing) &&
            RimLayers.Work.QueryGeometry.DistanceSquared(owner,firing,request.locus)<=4);
        Check("firing-cell-has-real-route-and-shot",found && pawn.CanReach(firing,PathEndMode.OnCell,Danger.Some) &&
            shootingVerb.CanHitTargetFrom(firing,combatDummy));
        request.maxRegions=1;request.maxRangeFromLocus=0;
        Check("firing-search-preserves-region-budget",!CastPositionFinder.TryFindCastPosition(request,out _));
        request.maxRegions=100;request.maxRangeFromLocus=2;request.validator=_=>false;
        Check("firing-search-preserves-caller-veto",!CastPositionFinder.TryFindCastPosition(request,out _));
        // A solid ground wall makes walking to the target impossible. Tactical memory must
        // still let the enemy walk to an upper firing position instead of forgetting the target.
        for(int z=0;z<owner.Layout!.Height;z++)
        {
            IntVec3 cell=At(0,65,z);
            foreach(Thing thing in cell.GetThingList(map).ToList())
                if(thing is not Pawn) { if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),cell,map);
        }
        pawn.Position=aiMoveStart=At(2,52,60);pawn.Notify_Teleported();
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        pawn.mindState.enemyTarget=combatDummy;pawn.mindState.lastEngageTargetTick=Find.TickManager.TicksGame;
        Check("known-target-needs-movement-to-fire",!shootingVerb.CanHitTarget(combatDummy) &&
            !pawn.CanReach(combatDummy,PathEndMode.Touch,Danger.Deadly));
        var tactical=new JobGiver_AIFightEnemies();
        AccessTools.Field(typeof(JobGiver_AIFightEnemy),"chaseTarget").SetValue(tactical,true);
        bool forget=(bool)AccessTools.Method(typeof(JobGiver_AIFightEnemies),"ShouldLoseTarget")
            .Invoke(tactical,new object[]{pawn})!;
        Check("enemy-remembers-target-with-reachable-firing-position",!forget);
        ThinkResult move=tactical.TryIssueJobPackage(pawn,default);
        Check("enemy-ai-issues-native-move-to-fire",move.Job!=null && move.Job.def==JobDefOf.Goto,move.Job?.def.defName??"none");
        if(move.Job==null) { Finish();return; }
        pawn.jobs.TryTakeOrderedJob(move.Job);
        stage=31;ticks=0;
    }

    private static void BeginAIWithoutRoute()
    {
        // Remove the only link from floor three. A clear ranged target still has to be acquired and kept.
        foreach(var stair in map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<RimLayers.Structure.Building_Stairs>()
            .Where(s=>owner.TryDecode(s.Position,out var layer)&&layer.LayerId==1).ToList())
            stair.Destroy(DestroyMode.Vanish);
        Check("ai-fixture-has-no-walking-route",!pawn.CanReach(combatDummy,PathEndMode.Touch,Danger.Deadly));
        var target=AttackTargetFinder.BestAttackTarget(pawn,
            TargetScanFlags.NeedReachableIfCantHitFromMyPos|TargetScanFlags.NeedLOSToAll|TargetScanFlags.NeedLOSToPawns,
            t=>t==combatDummy,maxDist:30);
        Check("native-target-finder-acquires-real-cross-floor-enemy",target?.Thing==combatDummy);
        pawn.mindState.enemyTarget=combatDummy;
        pawn.mindState.lastEngageTargetTick=Find.TickManager.TicksGame;
        var giver=new JobGiver_AIFightEnemies();
        bool lose=(bool)AccessTools.Method(typeof(JobGiver_AIFightEnemies),"ShouldLoseTarget").Invoke(giver,new object[]{pawn})!;
        Check("enemy-ai-keeps-shootable-target-without-route",!lose);
        ThinkResult fight=giver.TryIssueJobPackage(pawn,default);
        Check("enemy-ai-issues-native-fight-job",fight.Job!=null,fight.Job?.def.defName??"none");
        if(fight.Job==null) { Finish();return; }
        pawn.jobs.TryTakeOrderedJob(fight.Job);
        aiImpactBaseline=owner.Combat.CommittedImpacts;
        stage=30;ticks=0;
    }
    private static void CombatAIStep()
    {
        if(stage==31)
        {
            if(pawn.Position!=aiMoveStart && shootingVerb.CanHitTarget(combatDummy))
            {
                Check("enemy-actually-moves-to-useful-upper-shooting-position",owner.TryDecode(pawn.Position,out var layer) && layer.LayerId>0,
                    pawn.Position.ToString());
                Check("movement-keeps-real-enemy-target",pawn.mindState.enemyTarget==combatDummy);
                pawn.jobs.StopAll();
                BeginAIWithoutRoute();
            }
            else if(ticks>1500) { Check("enemy-moves-to-firing-position",false,DescribePawn());Finish(); }
            return;
        }
        if(owner.Combat.CommittedImpacts>aiImpactBaseline)
        {
            Check("native-enemy-ai-fires-across-floor",true);
            pawn.jobs.StopAll();
            stage=11;ticks=0;
        }
        else if(ticks>1800)
        { Check("native-enemy-ai-fires-across-floor",false,DescribePawn());Finish(); }
    }
}
