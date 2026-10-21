using System.IO;
using System.Text.Json;
using RimLayers.Navigation;
using RimLayers.Presentation;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly List<int> mazeLayers = new();
    private static JobDriver? mazeDriver;
    private static bool mazeSaved;

    private static void BeginMaze()
    {
        foreach(var stair in map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).ToList())stair.Destroy(DestroyMode.Vanish);
        // The ground wall spans the whole usable floor; upper empty air cannot bypass its wall.
        for(int z=0;z<owner.Layout!.Height;z++)
        {
            IntVec3 c=At(0,57,z);
            foreach(Thing t in c.GetThingList(map).ToList())if(t is not Pawn)
            {if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),c,map);
        }
        for(int z=50;z<=64;z++)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(1,57,z),map);
        var near=new List<Building_Stairs>();
        foreach(var (x,z) in new[]{(51,51),(54,51),(51,55)})
        {
            near.Add((Building_Stairs)GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(0,x,z),map));
            foreach(IntVec3 neighbor in GenAdj.AdjacentCellsAround)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),At(1,x+neighbor.x,z+neighbor.z),map);
        }
        var fourth=(Building_Stairs)GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(0,54,62),map);
        GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(0,62,52),map);
        GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(1,55,60),map);
        GenSpawn.Spawn(RimLayersDefOf.RL_Stairs,At(1,61,60),map);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        destination=At(0,62,62);
        Check("maze-fourth-stair-farther-than-three-dead-ends",near.All(n=>(n.Position-pawn.Position).LengthHorizontalSquared<(fourth.Position-pawn.Position).LengthHorizontalSquared));
        using(var path=map.pathFinder.FindPathNow(pawn.Position,destination,pawn))
        {
            Check("same-floor-target-has-nonmonotonic-route",path.Found);
            var sequence=path.NodesReversed.AsEnumerable().Reverse().Select(c=>{owner.TryDecode(c,out var l);return l.LayerId;}).Aggregate(new List<int>(),(a,l)=>{if(a.Count==0||a[^1]!=l)a.Add(l);return a;});
            Check("planned-route-reverses-through-all-three-floors",sequence.SequenceEqual(new[]{0,1,2,1,0}),string.Join(",",sequence));
            bool tagged=LayerRouter.HasMetadata(path,out var metadata);
            Check("route-uses-fourth-stair-not-nearest-three",tagged && metadata.Steps.Values.Any(e=>e.Stairs==fourth) &&
                metadata.Steps.Values.All(e=>!near.Contains(e.Stairs)));
            Check("nonmonotonic-route-keeps-real-final-cell",path.Found && path.LastNode==destination);
        }
        Check("reachability-agrees-for-nonmonotonic-route",pawn.CanReach(destination,PathEndMode.OnCell,Danger.Deadly));
        expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);pawn.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;
        mazeDriver=pawn.jobs.curDriver;retainedJob=true;mazeLayers.Clear();mazeSaved=false;stage=110;ticks=0;
    }

    private static void RestoreMaze()
    {
        using var b=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"maze-baseline.json")));
        var data=b.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==data.GetProperty("pawn").GetString());
        destination=At(0,62,62);expectedJob=pawn.CurJob;expectedJobId=data.GetProperty("job").GetInt32();
        mazeDriver=pawn.jobs.curDriver;mazeLayers.Clear();mazeLayers.AddRange(data.GetProperty("layers").EnumerateArray().Select(n=>n.GetInt32()));
        Check("restart-keeps-native-job-id-and-final-cell",ExpectedJobMatches() && expectedJob?.targetA.Cell==destination && mazeDriver!=null);
        retainedJob=true;mazeSaved=true;stage=110;ticks=0;
    }

    private static void MazeStep()
    {
        owner.TryDecode(pawn.Position,out var here);
        if(mazeLayers.Count==0 || mazeLayers[^1]!=here.LayerId)mazeLayers.Add(here.LayerId);
        if(pawn.Position!=destination && (!ExpectedJobMatches() || !ReferenceEquals(mazeDriver,pawn.jobs.curDriver)))retainedJob=false;
        if(mode=="generate" && !mazeSaved && here.LayerId==2 && pawn.pather.Moving &&
            owner.TryDecode(pawn.pather.nextCell,out var next) && next.LayerId==1 &&
            pawn.pather.nextCellCostLeft>0 && pawn.pather.nextCellCostLeft<pawn.pather.nextCellCostTotal)
        {
            GameDataSaveLoader.SaveGame("RimLayers_V1Maze");
            File.WriteAllText(Path.Combine(directory,"maze-baseline.json"),JsonSerializer.Serialize(new{pawn=pawn.ThingID,job=expectedJobId,layers=mazeLayers}));
            Check("saved-during-first-reversed-cross-layer-step",true);mazeSaved=true;
        }
        if(pawn.Position==destination)
        {
            Check("pawn-actually-traverses-nonmonotonic-route",mazeLayers.SequenceEqual(new[]{0,1,2,1,0}),string.Join(",",mazeLayers));
            Check("same-job-and-driver-through-reversal",retainedJob);
            Check("nonmonotonic-midstep-save-was-exercised",mazeSaved);
            Check("same-pawn-remains-spawned-on-same-map",pawn.Spawned && pawn.Map==map);
            LayerView.Switch(owner,0);Find.CameraDriver.SetRootPosAndSize(pawn.Position.ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,mode+"-maze.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(ticks>6500){Check("nonmonotonic-arrival",false,DescribePawn()+", layers="+string.Join(",",mazeLayers));Finish();}
    }
}
