using System.IO;
using HarmonyLib;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Skyfaller incomingShuttle=null!;
    private static Thing crashedShuttle=null!;
    private static Blueprint shuttlePlan=null!;
    private static int shuttleCommits,shuttleSteel;
    private static Frame shuttleFrame=null!;
    private static Blueprint ShuttleBlueprint()=>GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall,At(2,38,75),map,Rot4.North,Faction.OfPlayer,ThingDefOf.Steel);
    private static void BeginShuttleFall()
    {
        pawn.drafter.Drafted=true;pawn.Position=At(0,40,40);pawn.Notify_Teleported();
        var drops=Enumerable.Range(0,64).SelectMany(_=>new[]{DropCellFinder.RandomDropSpot(map,true),DropCellFinder.RandomDropSpot(map,false)}).ToList();
        Check("both-native-random-drop-modes-reject-isolation-and-covered-bands",drops.All(c=>c.IsValid && owner.TryDecode(c,out _) && owner.Skyfall.CanArrive(c,true)));
        SkyPlatform(38,75,false);crashedShuttle=ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ShuttleCrashed"));
        shuttlePlan=ShuttleBlueprint();
        incomingShuttle=SkyfallerMaker.SpawnSkyfaller(DefDatabase<ThingDef>.GetNamed("ShuttleCrashing"),crashedShuttle,At(0,38,75),map);
        Check("real-shuttle-def-and-rotated-footprint-have-fifteen-cells",incomingShuttle is ShuttleIncoming && crashedShuttle.OccupiedRect().Area==15 && incomingShuttle.Position==At(2,38,75));
        incomingShuttle.ticksToImpact=1;AccessTools.Method(typeof(Skyfaller),"Tick").Invoke(incomingShuttle,null);
        Check("partial-platform-keeps-real-five-by-three-shuttle",incomingShuttle.Spawned && !crashedShuttle.Spawned && !crashedShuttle.Destroyed && incomingShuttle.GetDirectlyHeldThings().Contains(crashedShuttle));
        Check("failed-shuttle-placement-does-not-precommit-blueprint-cancellation",shuttlePlan.Spawned);
        shuttleCommits=skyImpactCounts.GetValueOrDefault(incomingShuttle.thingIDNumber);stage=170;ticks=0;
    }
    private static void ShuttleFallStep()
    {
        if(stage==170 && ticks>150 && !owner.Combat.Blasts.Any(b=>!b.Explosion.Destroyed) && !map.listerThings.AllThings.OfType<Projectile>().Any())
        {
            if(shuttlePlan.Spawned)shuttlePlan.Destroy(DestroyMode.Cancel);
            shuttlePlan=ShuttleBlueprint();stage=171;ticks=0;
        }
        else if(stage==171 && ticks>100)
        {
            Check("waiting-shuttle-retries-preserve-new-blueprint",shuttlePlan.Spawned && !crashedShuttle.Spawned);
            Check("waiting-shuttle-does-not-replay-explosion",skyImpactCounts.GetValueOrDefault(incomingShuttle.thingIDNumber)==shuttleCommits);
            shuttlePlan.Destroy(DestroyMode.Cancel);shuttleFrame=FrameFor(ThingDefOf.Wall,At(2,38,75),5);
            SkyPlatform(38,75,true);stage=173;ticks=0;
        }
        else if(stage==173 && ticks>100)
        {
            Check("shuttle-near-fallback-never-wipes-material-bearing-frame",shuttleFrame.Spawned && shuttleFrame.resourceContainer.TotalStackCount==5 && !crashedShuttle.Spawned);
            shuttleSteel=map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount);
            shuttleFrame.Destroy(DestroyMode.Cancel);shuttlePlan=ShuttleBlueprint();stage=172;ticks=0;
        }
        else if(stage==172 && ticks>120)
        {
            Check("real-shuttle-lands-whole-footprint-on-one-layer",crashedShuttle.Spawned && crashedShuttle.OccupiedRect().All(c=>owner.TryDecode(c,out var layer) && layer.LayerId==2 && owner.Structure.HasSurface(c)),
                $"spawned={crashedShuttle.Spawned}, destroyed={crashedShuttle.Destroyed}, pending={incomingShuttle.Spawned}, ready={SkyPayload.ReadyForShuttle(incomingShuttle)}, rotation={crashedShuttle.Rotation}, surfaces={GenAdj.OccupiedRect(At(2,38,75),crashedShuttle.Rotation,crashedShuttle.def.Size).Count(owner.Structure.HasSurface)}, nativeCanSpawn={GenSpawn.CanSpawnAt(crashedShuttle.def,At(2,38,75),map,crashedShuttle.Rotation)}");
            Check("successful-shuttle-commit-performs-native-blueprint-cancellation",shuttlePlan.Destroyed && incomingShuttle.Destroyed);
            Check("shuttle-commit-preserves-native-frame-refund",map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(t=>t.stackCount)==shuttleSteel+5);
            Check("shuttle-preserves-real-identity-and-one-impact",map.listerThings.AllThings.Count(t=>t==crashedShuttle)==1 && skyImpactCounts.GetValueOrDefault(incomingShuttle.thingIDNumber)==shuttleCommits);
            LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(At(2,38,75).ToVector3Shifted(),10);
            pendingScreenshot=Path.Combine(directory,"shuttle-fall.png");screenshotHold=3;stage=11;ticks=0;
        }
        if(ticks>1600){Check("shuttle-fall-stage-completes",false,$"stage={stage},projectiles={map.listerThings.AllThings.OfType<Projectile>().Count()}");Finish();}
    }
}
