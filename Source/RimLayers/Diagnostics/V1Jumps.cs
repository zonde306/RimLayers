using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Generation;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void ConfigureJumps(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(LayerJumpFlyer),"RespawnPawn"),
            postfix:new HarmonyMethod(typeof(V1Integration),nameof(JumpLandingObserved)));
        h.Patch(AccessTools.Method(typeof(Targeter),nameof(Targeter.TargeterUpdate)),
            postfix:new HarmonyMethod(typeof(V1Integration),nameof(JumpPreviewUpdate)));
    }
    private static void JumpLandingObserved()
    {
        if(stage!=530)return;
        Check("native-landing-delivers-queued-order",pawn.CurJob?.loadID==expectedJobId||pawn.jobs.jobQueue.Any(j=>j.job.loadID==expectedJobId),
            $"expected={expectedJobId},current={pawn.CurJob}/{pawn.CurJob?.loadID},queue={string.Join(",",pawn.jobs.jobQueue.Select(j=>j.job.loadID))}");
    }
    private static bool jumpPreview;
    private static void JumpsOnGUI()
    {
        if(!jumpPreview||Event.current.type!=EventType.Repaint)return;
        jumpVerb.OnGUI(At(0,50,53));
    }
    private static void JumpPreviewUpdate()
    {
        if(jumpPreview)jumpVerb.DrawHighlight(At(0,50,53));
    }
    private static Verb_Jump jumpVerb=null!;
    private static LayerJumpFlyer? jumpSavedFlyer;
    private static Thing jumpCargo=null!;
    private static int jumpPhase,jumpCharges;
    private static bool jumpPositionsValid=true;
    private static readonly System.Reflection.MethodInfo JumpTick=AccessTools.Method(typeof(LayerJumpFlyer),"TickInterval");
    private static readonly System.Reflection.MethodInfo JumpCast=AccessTools.Method(typeof(Verb_Jump),"TryCastShot");
    private static void JumpPlace(IntVec3 cell)
    {
        pawn.jobs.StopAll();pawn.stances.CancelBusyStanceSoft();jumpVerb.Reset();
        if(!pawn.Spawned)GenSpawn.Spawn(pawn,cell,map);else pawn.Position=cell;
        pawn.Notify_Teleported();pawn.drafter.Drafted=true;pawn.drafter.FireAtWill=false;
    }
    private static void JumpFill()
    {
        AccessTools.Field(typeof(CompApparelVerbOwner_Charged),"remainingCharges").SetValue(jumpVerb.ReloadableCompSource,jumpVerb.ReloadableCompSource.MaxCharges);
    }
    private static bool JumpCastAt(IntVec3 cell)
    {
        AccessTools.Field(typeof(Verb),"currentTarget").SetValue(jumpVerb,new LocalTargetInfo(cell));
        return (bool)JumpCast.Invoke(jumpVerb,null)!;
    }
    private static LayerJumpFlyer? JumpLaunchAt(IntVec3 cell)
    {
        JumpFill();
        bool cast=JumpCastAt(cell);Check("direct-cross-jump-launch-"+cell,cast);
        return cast?map.listerThings.AllThings.OfType<LayerJumpFlyer>().Single():null;
    }
    private static void JumpDrain(LayerJumpFlyer flyer,int delta=1)
    {
        for(int i=0;i<300&&!flyer.Destroyed&&!flyer.Waiting;i++)
        {
            JumpTick.Invoke(flyer,new object[]{delta});
            if(!flyer.Destroyed)jumpPositionsValid&=owner.TryDecode(flyer.Position,out _);
        }
    }
    private static void BeginJumps()
    {
        foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList()){p.jobs.StopAll();p.DeSpawn();}
        for(int layer=0;layer<owner.Layout!.LayerCount;layer++)
            foreach(var logical in new CellRect(40,40,51,31))
            {
                var c=At(layer,logical.x,logical.z);
                foreach(var t in c.GetThingList(map).ToList())if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();
                map.roofGrid.SetRoof(c,null);
                map.terrainGrid.SetTerrain(c,layer==0?TerrainDefOf.Concrete:RimLayersDefOf.RL_OpenAir);
                map.fogGrid.Unfog(c);
            }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();
        for(int x=60;x<=70;x++)for(int z=50;z<=56;z++)
        {
            map.roofGrid.SetRoof(At(0,x,z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,x,z),RimLayersDefOf.RL_StructuralFloor);
        }
        for(int x=66;x<=70;x++)for(int z=50;z<=56;z++)
        {
            map.roofGrid.SetRoof(At(1,x,z),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(2,x,z),RimLayersDefOf.RL_StructuralFloor);
        }
        foreach(int x in new[]{61,67})foreach(int z in new[]{50,56})
        {
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,x,z),map);
            if(x==67)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(1,x,z),map);
        }
        pawn=CombatAdult(Faction.OfPlayer);GenSpawn.Spawn(pawn,At(0,50,53),map);
        pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump")));
        jumpVerb=pawn.apparel.AllApparelVerbs.OfType<Verb_Jump>().Single();JumpFill();JumpPlace(At(0,50,53));
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("jump-uses-native-23-9-range",Math.Abs(jumpVerb.EffectiveRange-23.9f)<.001);
        Check("cross-layer-jump-does-not-require-connectors",!pawn.CanReach(At(1,63,53),PathEndMode.OnCell,Danger.Deadly));
        Check("upper-edge-target-valid",jumpVerb.ValidateTarget(At(1,63,53),false));
        Check("warmup-shootline-uses-jump-solver",jumpVerb.TryFindShootLineFromTo(pawn.Position,At(1,63,53),out _));
        JumpPlace(At(0,63,53));int before=jumpVerb.ReloadableCompSource.RemainingCharges;
        Check("floor-from-below-rejected",!jumpVerb.ValidateTarget(At(1,63,53),false));
        Check("blocked-commit-does-not-spend-charge",!JumpCastAt(At(1,63,53))&&jumpVerb.ReloadableCompSource.RemainingCharges==before);
        JumpPlace(At(0,50,53));
        Check("void-landing-rejected",!jumpVerb.ValidateTarget(At(1,55,53),false));
        for(int x=49;x<=53;x++)map.roofGrid.SetRoof(At(0,x,53),RoofDefOf.RoofRockThick);
        Check("source-ceiling-blocks-cross-layer-arc",!jumpVerb.ValidateTarget(At(1,63,53),false));
        for(int x=49;x<=53;x++)map.roofGrid.SetRoof(At(0,x,53),null);
        foreach(var pair in new[]{(0,1,50,63),(1,0,63,50),(0,2,50,68),(2,0,68,50),(1,2,60,68),(2,1,68,60)})
        {
            JumpPlace(At(pair.Item1,pair.Item3,53));
            var f=JumpLaunchAt(At(pair.Item2,pair.Item4,53));
            if(f==null)continue;JumpDrain(f,7);
            Check("jump-arrives-"+pair,f.Destroyed&&pawn.Spawned&&pawn.Position==At(pair.Item2,pair.Item4,53),$"pawn={pawn.Position},waiting={f.Waiting}");
        }
        Check("all-cross-layer-flight-positions-decode",jumpPositionsValid);
        JumpPlace(At(0,50,53));JumpFill();
        Check("same-floor-native-cast",JumpCastAt(At(0,54,53)));
        var native=map.listerThings.AllThings.OfType<PawnFlyer>().Single();
        Check("same-floor-keeps-native-flyer",native.GetType()==typeof(PawnFlyer));
        for(int i=0;i<100&&!native.Destroyed;i++)AccessTools.Method(typeof(PawnFlyer),"TickInterval").Invoke(native,new object[]{1});
        Check("same-floor-native-landing",pawn.Spawned&&pawn.Position==At(0,54,53));
        // No vertical tunnel: even after an underground floor exists, surface geometry is solid.
        LiveMapExpansion.EnsureElevation(map,-1);owner=map.GetComponent<RimLayers.Core.LayerMapComponent>();
        int below=owner.Layout!.LayerAt(-1);
        foreach(var c in new CellRect(49,52,3,3))
        {
            var underground=At(below,c.x,c.z);
            foreach(var t in underground.GetThingList(map).ToList())if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();
            map.terrainGrid.SetTerrain(underground,TerrainDefOf.Concrete);map.fogGrid.Unfog(underground);
        }
        JumpPlace(At(0,50,53));
        Check("cannot-jump-from-surface-underground",!jumpVerb.ValidateTarget(At(below,50,53),false));
        // Launch, then add a new blocking roof. A large delta must stop on the incoming side.
        var interrupted=JumpLaunchAt(At(1,63,53));
        if(interrupted!=null)
        {
            for(int x=49;x<=59;x++)for(int z=52;z<=54;z++)map.roofGrid.SetRoof(At(0,x,z),RoofDefOf.RoofConstructed);
            JumpDrain(interrupted,120);
            Check("new-ceiling-never-passes-through",interrupted.Waiting||pawn.Spawned&&owner.IsGroundCell(pawn.Position));
            Check("interrupted-jump-retains-real-pawn",!pawn.Destroyed&&!pawn.Dead&&(pawn.Spawned||interrupted.GetDirectlyHeldThings().Contains(pawn)));
            for(int x=49;x<=59;x++)for(int z=52;z<=54;z++)map.roofGrid.SetRoof(At(0,x,z),null);
            if(interrupted.Waiting)
                for(int i=0;i<60&&!interrupted.Destroyed;i++)JumpTick.Invoke(interrupted,new object[]{30});
            JumpDrain(interrupted,7);
            Check("interrupted-jump-recovers-without-losing-pawn",pawn.Spawned&&!pawn.Dead&&interrupted.Destroyed);
        }
        JumpPlace(At(0,50,53));JumpFill();
        int warmupCharges=jumpVerb.ReloadableCompSource.RemainingCharges;
        Check("native-warmup-starts-on-clear-path",jumpVerb.TryStartCastOn(At(1,63,53)));
        map.roofGrid.SetRoof(At(0,53,53),RoofDefOf.RoofRockThick);
        Check("warmup-fixture-actually-obstructs-body",!jumpVerb.CanHitTarget(At(1,63,53)));
        for(int i=0;i<120;i++)Find.TickManager.DoSingleTick();
        Check("warmup-rechecks-new-ceiling-before-charge",pawn.Spawned&&pawn.stances.curStance is not Stance_Warmup&&jumpVerb.ReloadableCompSource.RemainingCharges==warmupCharges&&
            !map.listerThings.AllThings.OfType<LayerJumpFlyer>().Any());
        map.roofGrid.SetRoof(At(0,53,53),null);
        JumpPlace(At(0,50,53));
        var lostLanding=JumpLaunchAt(At(1,63,53));
        if(lostLanding!=null)
        {
            map.terrainGrid.SetTerrain(At(1,63,53),RimLayersDefOf.RL_OpenAir);
            JumpDrain(lostLanding,17);
            Check("removed-landing-chooses-clear-supported-neighbour",lostLanding.Destroyed&&pawn.Spawned&&
                pawn.Position!=At(1,63,53)&&owner.Structure.HasSurface(pawn.Position)&&!pawn.Dead);
            map.terrainGrid.SetTerrain(At(1,63,53),RimLayersDefOf.RL_StructuralFloor);
        }
        JumpPlace(At(0,50,53));
        var pack=(Apparel)jumpVerb.EquipmentSource;pawn.apparel.Remove(pack);
        var armor=(Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_ArmorLocust"));pawn.apparel.Wear(armor);
        jumpVerb=pawn.apparel.AllApparelVerbs.OfType<Verb_Jump>().Single(v=>v.EquipmentSource==armor);
        var locust=JumpLaunchAt(At(1,63,53));if(locust!=null)JumpDrain(locust,7);
        Check("locust-armour-jumps-through-same-integration",locust?.Destroyed==true&&pawn.Spawned&&pawn.Position==At(1,63,53));
        pawn.apparel.Remove(armor);armor.Destroy();pawn.apparel.Wear(pack);
        jumpVerb=pawn.apparel.AllApparelVerbs.OfType<Verb_Jump>().Single(v=>v.EquipmentSource==pack);
        if(ModsConfig.OdysseyActive)
        {
            map.terrainGrid.SetTerrain(At(2,69,55),DefDatabase<TerrainDef>.GetNamed("RL_ShipStructuralFloor"));
            JumpPlace(At(1,69,55));
            Check("ship-deck-remains-a-solid-jump-barrier",owner.Structure.HasSurface(At(2,69,55))&&!jumpVerb.ValidateTarget(At(2,69,55),false));
        }
        JumpPlace(At(0,50,53));
        var heldRetry=JumpLaunchAt(At(1,63,53));
        if(heldRetry!=null)
        {
            heldRetry.Destroy(); // The retained native holder must remain usable when selected via its pawn.
            var retry=pawn.GetGizmos().OfType<Command_Action>().FirstOrDefault(g=>g.defaultLabel=="RL_JumpRetry".Translate().ToString());
            Check("held-colonist-exposes-safe-landing-action",heldRetry.Waiting&&retry!=null);
            retry?.action();JumpDrain(heldRetry,7);
            Check("held-colonist-retry-actually-lands",heldRetry.Destroyed&&pawn.Spawned&&!pawn.Dead);
        }
        // Exercise a partially completed native drop: real pawn placed, original cargo retained.
        JumpPlace(At(0,50,53));
        var partialCargo=ThingMaker.MakeThing(ThingDefOf.Silver);partialCargo.stackCount=37;
        Check("partial-drop-fixture-carries-original-item",pawn.carryTracker.TryStartCarry(partialCargo));
        var partial=JumpLaunchAt(At(1,63,53));
        if(partial!=null)
        {
            partial.GetDirectlyHeldThings().TryDrop(pawn,At(0,50,53),map,ThingPlaceMode.Direct,out _);
            partial.Destroy();
            Check("failed-drop-does-not-destroy-held-cargo",!partial.Destroyed&&partial.GetDirectlyHeldThings().Contains(partialCargo));
            for(int i=0;i<30&&!partial.Destroyed;i++)JumpTick.Invoke(partial,new object[]{30});
            Check("retained-cargo-has-a-working-safe-retry",partial.Destroyed&&partialCargo.Spawned&&partialCargo.stackCount==37&&pawn.Spawned&&!pawn.Dead);
            partialCargo.Destroy();
        }
        Check("jump-carry-transaction-does-not-leak",JumpService.HandlingPawn==null&&JumpService.LaunchArc==null);
        // Real native job/warmup route, carrying an original item and a queued native order.
        JumpPlace(At(0,50,53));JumpFill();
        jumpCargo=ThingMaker.MakeThing(ThingDefOf.Silver);jumpCargo.stackCount=43;
        GenSpawn.Spawn(jumpCargo,pawn.Position,map);
        Check("fixture-picks-up-full-original-stack",pawn.carryTracker.TryStartCarry(jumpCargo,43,reserve:false)==43&&pawn.carryTracker.CarriedThing==jumpCargo);
        jumpVerb.OrderForceTarget(At(1,63,53));
        Check("jump-order-retains-carry",pawn.carryTracker.CarriedThing==jumpCargo);
        var queued=JobMaker.MakeJob(JobDefOf.Goto,At(1,63,54));pawn.jobs.TryTakeOrderedJob(queued,JobTag.Misc,requestQueueing:true);
        expectedJobId=queued.loadID;
        Check("native-order-starts-castjump",pawn.CurJob?.def==JobDefOf.CastJump);
        jumpCharges=jumpVerb.ReloadableCompSource.RemainingCharges;
        jumpPhase=0;stage=530;ticks=0;
    }
    private static void RestoreJumps()
    {
        using var file=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"jump-baseline.json")));
        var d=file.RootElement;
        jumpSavedFlyer=map.listerThings.AllThings.OfType<LayerJumpFlyer>().Single(f=>f.ThingID==d.GetProperty("flyer").GetString());
        pawn=jumpSavedFlyer.FlyingPawn;jumpCargo=pawn.carryTracker.CarriedThing??jumpSavedFlyer.CarriedThing;
        jumpVerb=pawn.apparel.AllApparelVerbs.OfType<Verb_Jump>().Single();
        expectedJobId=d.GetProperty("queued").GetInt32();jumpCharges=d.GetProperty("charges").GetInt32();
        Check("reload-retains-original-pawn",pawn.ThingID==d.GetProperty("pawn").GetString());
        Check("reload-retains-original-carried-stack",jumpCargo?.ThingID==d.GetProperty("cargo").GetString()&&jumpCargo?.stackCount==43);
        Check("reload-retains-logical-progress",Math.Abs(jumpSavedFlyer.Progress-d.GetProperty("progress").GetDouble())<1e-9);
        Check("reload-retains-charge-once",jumpVerb.ReloadableCompSource.RemainingCharges==jumpCharges);
        JumpPicture("load-jump-flight.png");jumpPhase=1;stage=530;ticks=0;
    }
    private static void JumpPicture(string name)
    {
        LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,62,53).ToVector3Shifted(),11);
        Find.Selector.ClearSelection();Find.Selector.Select(pawn);
        pendingScreenshot=Path.Combine(directory,name);screenshotHold=3;
    }
    private static void JumpStep()
    {
        ticks++;
        jumpSavedFlyer=map.listerThings.AllThings.OfType<LayerJumpFlyer>().FirstOrDefault();
        if(jumpPhase==0&&jumpSavedFlyer is {Progress:>.70})
        {
            Check("native-job-and-warmup-launched-custom-flyer",true);
            Check("native-launch-spends-exactly-one-charge",jumpVerb.ReloadableCompSource.RemainingCharges==jumpCharges-1);
            jumpCharges=jumpVerb.ReloadableCompSource.RemainingCharges;
            GameDataSaveLoader.SaveGame("RimLayers_V1Jump");
            File.WriteAllText(Path.Combine(directory,"jump-baseline.json"),JsonSerializer.Serialize(new{
                flyer=jumpSavedFlyer.ThingID,pawn=pawn.ThingID,cargo=jumpCargo.ThingID,progress=jumpSavedFlyer.Progress,queued=expectedJobId,charges=jumpCharges}));
            JumpPicture("generate-jump-flight.png");jumpPhase=1;return;
        }
        if(jumpPhase==1&&pawn.Spawned)
        {
            Check("native-job-jump-lands-upper-platform",owner.TryDecode(pawn.Position,out var at)&&at.LayerId==1);
            Check("landing-restores-drafted-and-fire-at-will",pawn.Drafted&&!pawn.drafter.FireAtWill);
            Check("landing-keeps-carried-stack-once",pawn.carryTracker.CarriedThing==jumpCargo&&jumpCargo.stackCount==43&&!jumpCargo.Spawned);
            Check("landing-restores-native-queued-job",pawn.CurJob?.loadID==expectedJobId||pawn.jobs.jobQueue.Any(j=>j.job.loadID==expectedJobId),
                $"expected={expectedJobId},current={pawn.CurJob}/{pawn.CurJob?.loadID},queue={string.Join(",",pawn.jobs.jobQueue.Select(j=>j.job.loadID))}");
            Check("landing-cleans-up-flyer",jumpSavedFlyer==null);
            JumpPicture(mode+"-jump-landed.png");jumpPhase=2;ticks=0;return;
        }
        if(jumpPhase==2&&ticks>20)
        {
            jumpPreview=true;LayerView.Switch(owner,0);
            Find.CameraDriver.SetRootPosAndSize(At(0,57,53).ToVector3Shifted(),16);
            pendingScreenshot=Path.Combine(directory,mode+"-jump-targeting.png");screenshotHold=4;
            jumpPhase=3;return;
        }
        if(jumpPhase==3){jumpPreview=false;Finish();return;}
        if(ticks>600){Check("jump-completes",false,DescribePawn());Finish();}
    }
}
