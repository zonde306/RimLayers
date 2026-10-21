using System.Text.Json;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn controlledMech=null!;
    private static JobDriver mechDriver=null!;
    private static readonly HashSet<int> mechLayers=new();
    private static bool mechJobRetained=true,mechSaved;
    private static IntVec3 mechRingCenter=IntVec3.Invalid;
    private static bool mechRendering;
    private static int mechRingCells,mechRingEdges;
    private static readonly Dictionary<int,string> mechBoundaryFacts=new();
    private static bool mechCenterAllowed;
    private static readonly HashSet<int> renderedMechRangeFloors=new();
    private static void MechRenderBeginning()=>mechRendering=true;
    private static void MechRenderEnded()=>mechRendering=false;
    private static void MechRadiusObserved(IntVec3 __0,float __1,Func<IntVec3,bool> predicate)
    {
        if(Math.Abs(__1-24.9f)>=0.001f)return;
        mechRingCenter=__0;
        if(mechRendering&&owner!=null&&owner.TryDecode(__0,out var c))
        {
            renderedMechRangeFloors.Add(c.LayerId);mechCenterAllowed=predicate(__0);
            mechRingCells=Math.Max(mechRingCells,((System.Collections.ICollection)HarmonyLib.AccessTools.Field(typeof(GenDraw),"ringDrawCells").GetValue(null)!).Count);
            mechRingEdges=Math.Max(mechRingEdges,((System.Collections.ICollection)HarmonyLib.AccessTools.Field(typeof(GenDraw),"instancingMatrices").GetValue(null)!).Count);
            var cells=(List<IntVec3>)HarmonyLib.AccessTools.Field(typeof(GenDraw),"ringDrawCells").GetValue(null)!;
            var matrices=(List<Matrix4x4>)HarmonyLib.AccessTools.Field(typeof(GenDraw),"instancingMatrices").GetValue(null)!;
            mechBoundaryFacts[c.LayerId]=$"center={__0}; cells={cells.Count}, bounds="+(cells.Count==0?"empty":$"{cells.Min(p=>p.x)}..{cells.Max(p=>p.x)}/{cells.Min(p=>p.z)}..{cells.Max(p=>p.z)}")+
                $"; edges={matrices.Count}; first="+(matrices.Count==0?"empty":$"{matrices[0].m03},{matrices[0].m13},{matrices[0].m23}");
        }
    }
    private static void BeginMechanitor()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        pawn.health.AddHediff(DefDatabase<HediffDef>.GetNamed("MechlinkImplant"),pawn.health.hediffSet.GetBrain());
        Check("real-mechlink-creates-native-mechanitor-tracker",pawn.mechanitor!=null);
        controlledMech=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Mech_Scyther"),Faction.OfPlayer);
        GenSpawn.Spawn(controlledMech,At(0,54,56),map);
        controlledMech.relations.AddDirectRelation(PawnRelationDefOf.Overseer,pawn);
        controlledMech.drafter.Drafted=true;controlledMech.jobs.StopAll();
        CheckMechFacts();CheckMechRange();
        // A legal, revealed terrace intersects the radio boundary. Native fog would hide
        // a ring lying entirely in unexplored air, so keep that rule and inspect it here.
        foreach(var c in new CellRect(75,50,8,9))
        {
            var ground=At(0,c.x,c.z);
            foreach(Thing thing in ground.GetThingList(map).ToList())
                if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
            map.terrainGrid.SetTerrain(ground,TerrainDefOf.Concrete);
            map.roofGrid.SetRoof(ground,RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,c.x,c.z),RimLayersDefOf.RL_StructuralFloor);
            map.fogGrid.Unfog(At(1,c.x,c.z));
        }
        foreach(var c in new[]{At(0,76,51),At(0,81,56)})
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),c,map);
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>owner.TryDecode(c,out var loc)&&loc.X>=75&&loc.X<=82&&loc.Z>=50&&loc.Z<=58);
        destination=At(2,62,62);expectedJob=JobMaker.MakeJob(JobDefOf.Goto,destination);
        controlledMech.jobs.TryTakeOrderedJob(expectedJob);expectedJobId=expectedJob.loadID;mechDriver=controlledMech.jobs.curDriver;
        Check("native-controlled-mech-accepts-three-floor-order",controlledMech.CurJob==expectedJob);
        stage=210;ticks=0;
    }
    private static void CheckMechFacts()
    {
        Check("one-native-overseer-and-control-group",controlledMech.GetOverseer()==pawn&&pawn.mechanitor.ControlledPawns.Count(p=>p==controlledMech)==1);
    }
    private static void CheckMechRange()
    {
        Check("same-floor-native-range-boundary",pawn.mechanitor.CanCommandTo(At(0,78,54))&&!pawn.mechanitor.CanCommandTo(At(0,79,54)));
        Check("cross-floor-native-range-boundary",pawn.mechanitor.CanCommandTo(At(2,78,54))&&!pawn.mechanitor.CanCommandTo(At(2,79,54)));
        Check("native-overseer-range-caller-accepts-other-floor-order",MechanitorUtility.InMechanitorCommandRange(controlledMech,At(2,62,62)));
        Check("command-range-rejects-storage-gap-and-outside",!pawn.mechanitor.CanCommandTo(new IntVec3(54,0,owner.Layout!.Height))&&!pawn.mechanitor.CanCommandTo(IntVec3.Invalid));
        LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.Selector.Select(controlledMech);LayerView.Switch(owner,2);
        mechRingCenter=IntVec3.Invalid;pawn.mechanitor.DrawCommandRadius();
        Check("native-command-radius-projects-to-viewed-floor",mechRingCenter==At(2,54,54),mechRingCenter.ToString());
        LayerView.Switch(owner,0);
    }
    private static void RestoreMechanitor()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"mechanitor-baseline.json")));
        var b=baseline.RootElement;
        pawn=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("controller").GetString());
        controlledMech=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==b.GetProperty("mech").GetString());
        destination=At(2,62,62);expectedJob=controlledMech.CurJob;expectedJobId=b.GetProperty("job").GetInt32();mechDriver=controlledMech.jobs.curDriver;
        Check("restart-keeps-native-mech-job-and-final-target",expectedJob?.loadID==expectedJobId&&expectedJob.targetA.Cell==destination);
        Check("restart-starts-at-middle-floor",owner.TryDecode(controlledMech.Position,out var cell)&&cell.LayerId==1);
        CheckMechFacts();CheckMechRange();mechSaved=true;stage=210;ticks=0;
    }
    private static void MechanitorStep()
    {
        if(stage==211)
        {
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,54,54).ToVector3Shifted(),28);
            mechRingCenter=IntVec3.Invalid;pawn.mechanitor.DrawCommandRadius();
            Check("radio-boundary-overlay-uses-viewed-terrace-not-mech-floor",mechRingCenter==At(1,54,54));
            pendingScreenshot=Path.Combine(directory,mode+"-mechanitor-range.png");screenshotHold=3;stage=214;ticks=0;return;
        }
        if(stage==214)
        {
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,77,54).ToVector3Shifted(),8);
            pendingScreenshot=Path.Combine(directory,mode+"-mechanitor-remote-offscreen.png");screenshotHold=3;stage=212;ticks=0;return;
        }
        if(stage==212)
        {
            LayerView.Switch(owner,0);Find.CameraDriver.SetRootPosAndSize(At(0,54,54).ToVector3Shifted(),28);
            pendingScreenshot=Path.Combine(directory,mode+"-mechanitor-native-ground.png");screenshotHold=3;stage=213;ticks=0;return;
        }
        if(stage==213)
        {
            pawn.Position=At(1,54,54);pawn.Notify_Teleported();
            LayerView.Switch(owner,1);Find.CameraDriver.SetRootPosAndSize(At(1,54,54).ToVector3Shifted(),28);
            pendingScreenshot=Path.Combine(directory,mode+"-mechanitor-native-upper.png");screenshotHold=3;stage=215;ticks=0;return;
        }
        if(stage==215)
        {
            Find.CameraDriver.SetRootPosAndSize(At(1,77,54).ToVector3Shifted(),8);
            pendingScreenshot=Path.Combine(directory,mode+"-mechanitor-native-offscreen.png");screenshotHold=3;stage=11;ticks=0;return;
        }
        owner.TryDecode(controlledMech.Position,out var cell);mechLayers.Add(cell.LayerId);
        if(controlledMech.Position!=destination&&(controlledMech.CurJob!=expectedJob||controlledMech.jobs.curDriver!=mechDriver))mechJobRetained=false;
        if(!mechSaved&&cell.LayerId==1)
        {
            GameDataSaveLoader.SaveGame("RimLayers_V1Mechanitor");
            File.WriteAllText(Path.Combine(directory,"mechanitor-baseline.json"),JsonSerializer.Serialize(new {controller=pawn.ThingID,mech=controlledMech.ThingID,job=expectedJobId}));
            mechSaved=true;Check("saved-native-mech-job-at-middle-floor",true);
        }
        if(controlledMech.Position==destination)
        {
            Check("controlled-mech-actually-crosses-to-third-floor",mechLayers.Contains(1)&&mechLayers.Contains(2)&&(mode=="load"||mechLayers.Contains(0)));
            Check("stairs-retain-native-mech-job-and-driver",mechJobRetained);
            CheckMechFacts();
            Check("arrived-mech-still-accepts-native-command-range",MechanitorUtility.InMechanitorCommandRange(controlledMech,destination));
            LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.Selector.Select(controlledMech);
            Find.CameraDriver.SetRootPosAndSize(At(2,58,58).ToVector3Shifted(),16);
            pendingScreenshot=Path.Combine(directory,mode+"-mechanitor.png");screenshotHold=3;stage=211;ticks=0;
        }
        else if(ticks>3000){Check("controlled-mech-reaches-third-floor",false,$"pos={controlledMech.Position},job={controlledMech.CurJob}");Finish();}
    }
}
