using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Structure;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly Dictionary<int,int> skyImpactCounts=new();
    private static void SkyImpactObserved(Skyfaller __instance)=>skyImpactCounts[__instance.thingIDNumber]=skyImpactCounts.GetValueOrDefault(__instance.thingIDNumber)+1;
    private static bool SavedImpactsNotReplayed()=>new[]{pendingFall,fullIncoming,wallIncoming}.All(f=>skyImpactCounts.GetValueOrDefault(f.thingIDNumber)==(mode=="generate"?1:0));
    private static Skyfaller pendingFall=null!,flyingPod=null!,fullIncoming=null!,wallIncoming=null!;
    private static ActiveTransporter blockedPod=null!,fullCellPod=null!,nearPod=null!,partialPod=null!,wallPod=null!;
    private static Thing pendingStove=null!,podStove=null!,skySilver=null!,existingSilver=null!,incomingGun=null!,oldStove=null!,newStove=null!,partialPlaced=null!,partialHeld=null!;
    private static Pawn skyPassenger=null!,wallPassenger=null!;
    private static Thing landingWall=null!;
    private static int skyImpactBefore,skySavedCountdown;
    private static void SkyPlatform(int x,int z,bool wide)
    {
        for(int a=x-2;a<=x+2;a++)for(int b=z-2;b<=z+2;b++)
        {
            var ground=At(0,a,b);foreach(Thing t in ground.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(ground,TerrainDefOf.Concrete);map.roofGrid.SetRoof(ground,RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,a,b),RimLayersDefOf.RL_StructuralFloor);map.roofGrid.SetRoof(At(1,a,b),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(2,a,b),wide || a==x && b==z?RimLayersDefOf.RL_StructuralFloor:RimLayersDefOf.RL_OpenAir);
            if(a==x-2 && b==z-2 || a==x+2 && b==z+2)
            {
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),ground,map);
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(1,a,b),map);
            }
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>owner.TryDecode(c,out var local) && Math.Abs(local.X-x)<=2 && Math.Abs(local.Z-z)<=2);
    }
    private static Thing SkyStove()=>ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("FueledStove"));
    private static void BeginSkyfall()
    {
        pawn.drafter.Drafted=true;pawn.Position=At(0,40,40);pawn.Notify_Teleported();
        Check("drop-query-rejects-ground-beneath-upper-floor",!DropCellFinder.CanPhysicallyDropInto(At(0,62,62),map,true));
        Check("drop-query-accepts-highest-real-floor",DropCellFinder.CanPhysicallyDropInto(At(2,62,62),map,true));
        map.terrainGrid.SetTerrain(At(1,57,57),RimLayersDefOf.RL_OpenAir);map.terrainGrid.SetTerrain(At(2,57,57),RimLayersDefOf.RL_OpenAir);
        map.roofGrid.SetRoof(At(0,57,57),null);map.roofGrid.SetRoof(At(1,57,57),null);map.roofGrid.SetRoof(At(2,57,57),RoofDefOf.RoofConstructed);
        Check("drop-query-tests-all-higher-roofs-over-void",!DropCellFinder.CanPhysicallyDropInto(At(0,57,57),map,false) && DropCellFinder.CanPhysicallyDropInto(At(0,57,57),map,true));
        var canopyCargo=ThingMaker.MakeThing(ThingDefOf.Silver);canopyCargo.stackCount=13;
        var info=new ActiveTransporterInfo{openDelay=10,leaveSlag=false};info.innerContainer.TryAdd(canopyCargo);
        DropPodUtility.MakeDropPodAt(At(0,57,57),map,info);var canopyPod=map.listerThings.AllThings.OfType<DropPodIncoming>().Single();
        canopyPod.ticksToImpact=16;AccessTools.Method(typeof(Skyfaller),"Tick").Invoke(canopyPod,null);
        Check("incoming-pod-punches-remote-canopy-before-impact",map.roofGrid.RoofAt(At(2,57,57))==null && owner.IsGroundCell(canopyPod.Position));
        // This subcase has finished its roof test; keep real cargo owned by its native pod until ordinary ticks open it.
        SkyPlatform(75,75,false);SkyPlatform(85,75,false);
        pendingStove=SkyStove();
        pendingFall=SkyfallerMaker.SpawnSkyfaller(DefDatabase<ThingDef>.GetNamed("ShipChunkIncoming_SmallExplosion"),pendingStove,At(0,75,75),map);
        Check("multicell-impact-stops-at-highest-partial-plane",pendingFall.Position==At(2,75,75));
        pendingFall.ticksToImpact=1;
        AccessTools.Method(typeof(Skyfaller),"Tick").Invoke(pendingFall,null);
        Check("failed-placement-keeps-impacted-faller-and-real-payload",SkyfallAccess.Impacted(pendingFall) && pendingFall.Spawned && !pendingStove.Spawned && !pendingStove.Destroyed && pendingFall.GetDirectlyHeldThings().Contains(pendingStove));
        podStove=SkyStove();var blocked=new ActiveTransporterInfo{openDelay=0,leaveSlag=false,despawnPodBeforeSpawningThing=true,spawnWipeMode=WipeMode.Vanish};blocked.innerContainer.TryAdd(podStove);
        blockedPod=(ActiveTransporter)ThingMaker.MakeThing(ThingDefOf.ActiveDropPod);blockedPod.Contents=blocked;GenSpawn.Spawn(blockedPod,At(2,85,75),map);
        AccessTools.Method(typeof(ActiveTransporter),"Tick").Invoke(blockedPod,null);
        Check("failed-opening-respawns-native-pod-without-losing-contents",blockedPod.Spawned && !blockedPod.Destroyed && !podStove.Destroyed && !podStove.Spawned && blockedPod.Contents.innerContainer.Contains(podStove));
        SkyPlatform(95,95,false);
        existingSilver=GenSpawn.Spawn(ThingDefOf.Silver,At(2,95,95),map);existingSilver.stackCount=55;
        incomingGun=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
        var full=new ActiveTransporterInfo{openDelay=0,leaveSlag=false,despawnPodBeforeSpawningThing=true,spawnWipeMode=WipeMode.Vanish};full.innerContainer.TryAdd(incomingGun);
        DropPodUtility.MakeDropPodAt(At(0,95,95),map,full);
        fullIncoming=map.listerThings.AllThings.OfType<DropPodIncoming>().Single(t=>t.Contents==full);
        fullCellPod=(ActiveTransporter)fullIncoming.GetDirectlyHeldThings().Single();
        fullIncoming.ticksToImpact=1;AccessTools.Method(typeof(Skyfaller),"Tick").Invoke(fullIncoming,null);
        Check("full-cell-direct-landing-keeps-old-stock-and-native-pod",existingSilver.Spawned && existingSilver.stackCount==55 && !incomingGun.Spawned && !incomingGun.Destroyed &&
            !fullCellPod.Spawned && fullIncoming.Spawned && SkyfallAccess.Impacted(fullIncoming) && full.innerContainer.Contains(incomingGun),
            $"stock={existingSilver.Spawned}/{existingSilver.Destroyed}/{existingSilver.PositionHeld}, pod={fullCellPod.Spawned}, flight={fullIncoming.Spawned}");
        SkyPlatform(105,75,false);
        for(int x=104;x<=106;x++)map.terrainGrid.SetTerrain(At(2,x,75),RimLayersDefOf.RL_StructuralFloor);
        oldStove=GenSpawn.Spawn(SkyStove(),At(2,105,75),map);newStove=SkyStove();
        var crowded=new ActiveTransporterInfo{openDelay=0,leaveSlag=false,despawnPodBeforeSpawningThing=true};crowded.innerContainer.TryAdd(newStove);
        nearPod=(ActiveTransporter)ThingMaker.MakeThing(ThingDefOf.ActiveDropPod);nearPod.Contents=crowded;GenSpawn.Spawn(nearPod,At(2,105,75),map);
        AccessTools.Method(typeof(ActiveTransporter),"Tick").Invoke(nearPod,null);
        Check("near-placement-does-not-wipe-existing-building-as-last-resort",oldStove.Spawned && !oldStove.Destroyed && !newStove.Spawned && crowded.innerContainer.Contains(newStove) && nearPod.Spawned);
        SkyPlatform(110,100,false);
        var pair=new[]{ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle")),ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"))};
        var partial=new ActiveTransporterInfo{openDelay=0,leaveSlag=false,spawnWipeMode=WipeMode.Vanish};foreach(var gun in pair)partial.innerContainer.TryAdd(gun);
        partialPod=(ActiveTransporter)ThingMaker.MakeThing(ThingDefOf.ActiveDropPod);partialPod.Contents=partial;GenSpawn.Spawn(partialPod,At(2,110,100),map);
        AccessTools.Method(typeof(ActiveTransporter),"Tick").Invoke(partialPod,null);
        partialPlaced=pair.Single(g=>g.Spawned);partialHeld=pair.Single(g=>!g.Spawned);
        Check("partial-exact-unload-keeps-native-holder-and-both-real-items",partialPod.Spawned && !partialPlaced.Destroyed && !partialHeld.Destroyed && partial.innerContainer.Contains(partialHeld));
        Check("pending-shell-restoration-does-not-displace-placed-cargo",partialPlaced.Position==partialPod.Position && partialPlaced.Position.GetItemCount(map)==2);
        SkyPlatform(110,45,false);
        landingWall=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(2,110,45),map);
        wallPassenger=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        var wallInfo=new ActiveTransporterInfo{openDelay=0,leaveSlag=false,spawnWipeMode=WipeMode.Vanish};wallInfo.innerContainer.TryAdd(wallPassenger);
        DropPodUtility.MakeDropPodAt(At(0,110,45),map,wallInfo);
        wallIncoming=map.listerThings.AllThings.OfType<DropPodIncoming>().Single(t=>t.Contents==wallInfo);wallPod=(ActiveTransporter)wallIncoming.GetDirectlyHeldThings().Single();
        wallIncoming.ticksToImpact=1;AccessTools.Method(typeof(Skyfaller),"Tick").Invoke(wallIncoming,null);
        Check("direct-unload-never-spawns-or-kills-pawn-inside-solid-wall",wallIncoming.Spawned && !wallPod.Spawned && !wallPassenger.Spawned && !wallPassenger.Dead && wallInfo.innerContainer.Contains(wallPassenger));
        skySilver=ThingMaker.MakeThing(ThingDefOf.Silver);skySilver.stackCount=37;
        skyPassenger=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        var travel=new ActiveTransporterInfo{openDelay=10,leaveSlag=false};travel.innerContainer.TryAdd(skySilver);travel.innerContainer.TryAdd(skyPassenger);
        DropPodUtility.MakeDropPodAt(At(0,62,62),map,travel);
        flyingPod=map.listerThings.AllThings.OfType<DropPodIncoming>().Single(t=>t.Contents==travel);
        Check("native-incoming-pod-is-redirected-before-spawn",flyingPod.Position==At(2,62,62) && !skyPassenger.Spawned);
        flyingPod.ticksToImpact=40;
        map.terrainGrid.SetTerrain(At(2,62,62),RimLayersDefOf.RL_OpenAir);
        AccessTools.Method(typeof(Skyfaller),"Tick").Invoke(flyingPod,null);
        Check("flight-rechecks-removed-landing-without-restarting-clock",flyingPod.Position==At(1,62,62) && flyingPod.ticksToImpact==39);
        map.terrainGrid.SetTerrain(At(2,62,62),RimLayersDefOf.RL_StructuralFloor);
        AccessTools.Method(typeof(Skyfaller),"Tick").Invoke(flyingPod,null);
        Check("flight-rechecks-added-landing-without-restarting-clock",flyingPod.Position==At(2,62,62) && flyingPod.ticksToImpact==38);
        skySavedCountdown=flyingPod.ticksToImpact;skyImpactBefore=owner.Skyfall.ImpactCommits;
        GameDataSaveLoader.SaveGame("RimLayers_V1Skyfall");
        File.WriteAllText(Path.Combine(directory,"skyfall-baseline.json"),JsonSerializer.Serialize(new{fall=pendingFall.ThingID,pending=pendingStove.ThingID,pod=blockedPod.ThingID,podItem=podStove.ThingID,flight=flyingPod.ThingID,silver=skySilver.ThingID,passenger=skyPassenger.ThingID,countdown=skySavedCountdown,full=fullIncoming.ThingID,existing=existingSilver.ThingID,gun=incomingGun.ThingID,near=nearPod.ThingID,oldStove=oldStove.ThingID,newStove=newStove.ThingID,partial=partialPod.ThingID,placed=partialPlaced.ThingID,retained=partialHeld.ThingID,wallPod=wallIncoming.ThingID,wallPawn=wallPassenger.ThingID,wall=landingWall.ThingID}));
        Check("saved-impacted-payload-pending-open-pod-and-active-flight",true);stage=160;ticks=0;
    }
    private static void RestoreSkyfall()
    {
        using var b=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"skyfall-baseline.json")));var d=b.RootElement;
        Thing FindThing(string key)=>map.listerThings.AllThings.Single(t=>t.ThingID==d.GetProperty(key).GetString());
        pendingFall=(Skyfaller)FindThing("fall");pendingStove=pendingFall.GetDirectlyHeldThings().Single();
        blockedPod=(ActiveTransporter)FindThing("pod");podStove=blockedPod.Contents.innerContainer.Single();flyingPod=(Skyfaller)FindThing("flight");
        var travel=((DropPodIncoming)flyingPod).Contents;skySilver=travel.innerContainer.Single(t=>t.ThingID==d.GetProperty("silver").GetString());
        skyPassenger=(Pawn)travel.innerContainer.Single(t=>t.ThingID==d.GetProperty("passenger").GetString());
        Check("restart-preserves-native-impact-commit-and-payload-id",SkyfallAccess.Impacted(pendingFall) && pendingStove.ThingID==d.GetProperty("pending").GetString() && !pendingStove.Spawned);
        Check("restart-preserves-native-open-pod-and-payload-id",podStove.ThingID==d.GetProperty("podItem").GetString() && !podStove.Spawned && blockedPod.Spawned);
        fullIncoming=(Skyfaller)FindThing("full");fullCellPod=(ActiveTransporter)fullIncoming.GetDirectlyHeldThings().Single();existingSilver=FindThing("existing");incomingGun=fullCellPod.Contents.innerContainer.Single();
        Check("restart-preserves-full-cell-stock-and-held-weapon",incomingGun.ThingID==d.GetProperty("gun").GetString() && !incomingGun.Spawned && existingSilver.stackCount==55);
        nearPod=(ActiveTransporter)FindThing("near");oldStove=FindThing("oldStove");newStove=nearPod.Contents.innerContainer.Single();
        Check("restart-preserves-blocked-near-placement-without-wiping-old-building",newStove.ThingID==d.GetProperty("newStove").GetString() && !newStove.Spawned && oldStove.Spawned);
        partialPod=(ActiveTransporter)FindThing("partial");partialPlaced=FindThing("placed");partialHeld=partialPod.Contents.innerContainer.Single();
        Check("restart-preserves-partially-unloaded-cargo-and-holder",partialHeld.ThingID==d.GetProperty("retained").GetString() && partialPlaced.Spawned && partialPod.Spawned && partialPlaced.Position==partialPod.Position);
        wallIncoming=(Skyfaller)FindThing("wallPod");wallPod=(ActiveTransporter)wallIncoming.GetDirectlyHeldThings().Single();landingWall=FindThing("wall");wallPassenger=(Pawn)wallPod.Contents.innerContainer.Single();
        Check("restart-preserves-real-passenger-at-blocked-solid-wall",wallPassenger.ThingID==d.GetProperty("wallPawn").GetString() && !wallPassenger.Spawned && !wallPassenger.Dead);
        skySavedCountdown=d.GetProperty("countdown").GetInt32();Check("restart-keeps-original-flight-countdown",flyingPod.ticksToImpact==skySavedCountdown);
        skyImpactBefore=owner.Skyfall.ImpactCommits;stage=160;ticks=0;
    }
    private static void SkyfallStep()
    {
        if(stage==160 && ticks>80)
        {
            Check("pending-impact-does-not-replay-explosion",pendingFall.Spawned && SkyfallAccess.Impacted(pendingFall) && owner.Skyfall.ImpactCommits==skyImpactBefore+2,
                $"before={skyImpactBefore}, now={owner.Skyfall.ImpactCommits}");
            Check("each-saved-impact-independently-never-replays",SavedImpactsNotReplayed());
            Check("blocked-pod-and-faller-keep-unspawned-payloads",pendingFall.GetDirectlyHeldThings().Contains(pendingStove) && blockedPod.Contents.innerContainer.Contains(podStove) && !pendingStove.Destroyed && !podStove.Destroyed);
            Check("ordinary-pod-opens-at-real-upper-floor",skySilver.Spawned && skySilver.stackCount==37 && owner.TryDecode(skySilver.Position,out var s) && s.LayerId==2 && skyPassenger.Spawned && owner.TryDecode(skyPassenger.Position,out var p) && p.LayerId==2,
                $"silver={skySilver.PositionHeld}, pawn={skyPassenger.PositionHeld}");
            // The canopy test's pod was not recorded separately in the baseline but is also in the native save.
            skyImpactBefore=owner.Skyfall.ImpactCommits;
            Check("full-cell-retries-never-delete-old-stock",existingSilver.Spawned && existingSilver.stackCount==55 && !incomingGun.Spawned && !incomingGun.Destroyed);
            SkyPlatform(75,75,true);SkyPlatform(85,75,true);SkyPlatform(95,95,true);SkyPlatform(105,75,true);SkyPlatform(110,100,true);SkyPlatform(110,45,true);landingWall.Destroy(DestroyMode.Vanish);stage=161;ticks=0;
        }
        else if(stage==161 && ticks>120)
        {
            Check("restored-floor-releases-original-faller-payload",pendingStove.Spawned && pendingStove.Position==At(2,75,75) && pendingFall.Destroyed);
            Check("restored-floor-releases-original-pod-payload",podStove.Spawned && podStove.Position==At(2,85,75) && blockedPod.Destroyed);
            Check("full-cell-recovery-safely-moves-original-stock",fullIncoming.Destroyed && fullCellPod.Destroyed && incomingGun.Spawned && incomingGun.Position==At(2,95,95) &&
                existingSilver.Spawned && existingSilver.stackCount==55 && existingSilver.Position!=At(2,95,95) && owner.TryDecode(existingSilver.Position,out var original) && original.LayerId==2);
            Check("near-recovery-preserves-old-and-new-buildings",nearPod.Destroyed && newStove.Spawned && oldStove.Spawned && newStove.Position!=oldStove.Position &&
                owner.TryDecode(newStove.Position,out var adjacent) && adjacent.LayerId==2);
            Check("partial-unload-recovers-both-original-items-without-duplication",partialPod.Destroyed && partialPlaced.Spawned && partialHeld.Spawned && partialPlaced.Position!=partialHeld.Position &&
                owner.TryDecode(partialPlaced.Position,out var one) && owner.TryDecode(partialHeld.Position,out var two) && one.LayerId==2 && two.LayerId==2);
            Check("cleared-wall-releases-same-live-passenger-on-correct-floor",wallPod.Destroyed && wallPassenger.Spawned && !wallPassenger.Dead &&
                owner.TryDecode(wallPassenger.Position,out var survivor) && survivor.LayerId==2 && wallPassenger.Position.Walkable(map));
            Check("recovery-does-not-replay-impact-side-effects",owner.Skyfall.ImpactCommits==skyImpactBefore);
            Check("recovery-keeps-each-original-impact-receipt",SavedImpactsNotReplayed());
            Check("each-recovered-building-is-owned-once",map.listerThings.AllThings.Count(t=>t==pendingStove)==1 && map.listerThings.AllThings.Count(t=>t==podStove)==1);
            LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(At(2,90,85).ToVector3Shifted(),25);
            pendingScreenshot=Path.Combine(directory,mode+"-skyfall.png");screenshotHold=3;stage=11;ticks=0;
        }
    }
}
