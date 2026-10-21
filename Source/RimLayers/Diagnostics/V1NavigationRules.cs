using System.IO;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Unity.Collections;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private sealed class TestOffsets:PathRequest.IPathGridCustomizer,IDisposable
    {
        private NativeArray<ushort> grid;
        internal TestOffsets(int count,int penalized){grid=new NativeArray<ushort>(count,Allocator.Persistent);grid[penalized]=20000;}
        public NativeArray<ushort> GetOffsetGrid()=>grid;
        public void Dispose()=>grid.Dispose();
    }
    private static Building_Stairs navBlocked=null!,navAlternate=null!;
    private static JobDriver navRuleDriver=null!;
    private static bool navExitBlocked,navAlternateEntered;
    private static Building_Stairs? FirstGroundStair(PawnPath path)=>LayerRouter.HasMetadata(path,out var data)?data.Steps.Values.FirstOrDefault(e=>owner.IsGroundCell(e.From)).Stairs:null;
    private static void BeginNavigationRules()
    {
        destination=At(1,54,54);
        using(var normal=map.pathFinder.FindPathNow(pawn.Position,destination,pawn))
        {
            navBlocked=FirstGroundStair(normal)!;
            Check("native-default-route-has-connector",normal.Found && navBlocked!=null);
        }
        if(navBlocked==null){Finish();return;}
        navAlternate=map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<Building_Stairs>().Single(s=>owner.IsGroundCell(s.Position) && s!=navBlocked);
        using(var offsets=new TestOffsets(map.cellIndices.NumGridCells,map.cellIndices.CellToIndex(navBlocked.UpperCell)))
        using(var biased=map.pathFinder.FindPathNow(pawn.Position,destination,TraverseParms.For(pawn),null,PathEndMode.OnCell,offsets))
        {
            Check("native-customizer-offset-changes-cross-layer-route",biased.Found && FirstGroundStair(biased)==navAlternate);
            Check("customized-route-still-rejects-storage-gap",biased.Found && biased.NodesReversed.All(c=>owner.TryDecode(c,out _) && owner.Structure.HasSurface(c)));
        }
        navBlocked.SetForbidden(true);
        using(var forbidden=map.pathFinder.FindPathNow(pawn.Position,destination,pawn))
            Check("native-pawn-forbiddance-excludes-connector",forbidden.Found && FirstGroundStair(forbidden)==navAlternate);
        navBlocked.SetForbidden(false);
        expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;
        navRuleDriver=pawn.jobs.curDriver;retainedJob=true;navExitBlocked=false;navAlternateEntered=false;stage=150;ticks=0;
    }
    private static void NavigationRulesStep()
    {
        if(!navExitBlocked && CrossingNow())
        {
            Check("path-is-about-to-use-selected-exit",pawn.pather.nextCell==navBlocked.UpperCell);
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),navBlocked.UpperCell,map);
            Check("real-wall-blocks-landing-but-keeps-stair",navBlocked.Spawned && !navBlocked.UpperCell.Walkable(map));
            navExitBlocked=true;
        }
        if(pawn.Position==navAlternate.UpperCell)navAlternateEntered=true;
        if(pawn.Position==destination)
        {
            Check("blocked-exit-replans-and-arrives",navExitBlocked && navAlternateEntered);
            Check("exit-replan-keeps-job-and-driver",retainedJob);
            Check("blocked-landing-was-not-entered",pawn.Position!=navBlocked.UpperCell);
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(destination.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"navigation-rules.png");screenshotHold=3;stage=11;ticks=0;return;
        }
        if(pawn.Position==navBlocked.UpperCell){Check("never-enters-blocked-exit",false);Finish();return;}
        if(!ExpectedJobMatches() || !ReferenceEquals(navRuleDriver,pawn.jobs.curDriver))retainedJob=false;
        if(ticks>4500){Check("exit-replan-arrives",false,DescribePawn());Finish();}
    }
}
