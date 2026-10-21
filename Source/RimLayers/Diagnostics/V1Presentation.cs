using System.Text.Json;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Pawn displayHigh=null!,displayLow=null!;
    private static Corpse displayCorpse=null!,coveredCorpse=null!;
    private static readonly Dictionary<string,float> weaponAngles=new();
    private static float displayStarted;
    private static int displayPicture;
    private static bool projectedWeapon,customAimProbe,nanamiWorkerDrawn;
    private static void ConfigurePresentation(Harmony harmony)
    {
        var worker=AccessTools.TypeByName("NanamiStartrail.PawnRenderNodeWorker_Startrail");
        if(worker!=null)harmony.Patch(AccessTools.Method(worker,"PostDraw"),postfix:new HarmonyMethod(typeof(V1Integration),nameof(DisplayNanamiWorkerObserved)));
        harmony.Patch(AccessTools.Method(typeof(PawnRenderUtility),nameof(PawnRenderUtility.DrawEquipmentAiming)),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(DisplayAimObserved)){priority=Priority.Last});
        harmony.Patch(AccessTools.Method(typeof(Graphics),"DrawMeshCore"),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(DisplayMeshObserved)){priority=Priority.Last});
    }
    private static void DisplayNanamiWorkerObserved(PawnDrawParms parms)
    {if(parms.pawn==displayHigh&&!parms.Portrait&&!parms.Cache)nanamiWorkerDrawn=true;}
    private static void DisplayMeshObserved(Matrix4x4 matrix,Material material,int renderQueue)
    {
        if(!BackgroundPass.Active||displayLow?.equipment?.Primary==null)return;
        var eq=displayLow.equipment.Primary;
        if(material!=BackgroundPass.MaterialFor(eq.Graphic.MatSingleFor(eq),owner.ViewedLayer))return;
        var point=matrix.MultiplyPoint3x4(Vector3.zero);var expected=LowerFloorBackdrop.Project(owner,displayLow.DrawPos);
        projectedWeapon|=Math.Abs(point.x-expected.x)<2&&Math.Abs(point.z-expected.z)<2&&point.y<0&&renderQueue<2000;
    }
    private static void CheckCustomAim()
    {
        // Nanami's render worker calls this shared entry with 70/290 degrees.
        // Exercise that contract without a compile-time dependency on its assembly.
        customAimProbe=true;
        PawnRenderUtility.DrawEquipmentAiming(displayHigh.equipment.Primary,displayHigh.DrawPos,70);
        Check("custom-render-worker-fixed-angle-corrected-only-for-cross-floor-aim",weaponAngles.TryGetValue("high",out var angle)&&Math.Abs(Mathf.DeltaAngle(angle,90))<1);
        var previous=LogicalAiming.SuppressEquipmentAim;
        try
        {
            LogicalAiming.SuppressEquipmentAim=true;
            PawnRenderUtility.DrawEquipmentAiming(displayHigh.equipment.Primary,displayHigh.DrawPos,70);
            Check("portrait-never-aim-scope-preserves-custom-carry-angle",weaponAngles.TryGetValue("high",out angle)&&angle==70);
        }
        finally{LogicalAiming.SuppressEquipmentAim=previous;customAimProbe=false;}
    }
    private static void DisplayAimObserved(Thing eq,float aimAngle)
    {
        if(LogicalAiming.SuppressEquipmentAim&&!customAimProbe)return;
        if(displayHigh!=null&&eq==displayHigh.equipment.Primary)weaponAngles["high"]=aimAngle;
        if(displayLow!=null&&eq==displayLow.equipment.Primary)weaponAngles["low"]=aimAngle;
    }
    private static void BeginPresentation()
    {
        displayHigh=map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>p.def.defName=="NST_Nanami")??pawn;displayHigh.jobs.StopAll();displayHigh.drafter.Drafted=true;
        displayHigh.Position=At(2,64,58);displayHigh.Notify_Teleported();
        foreach(var cell in new CellRect(65,58,11,1))
            foreach(var thing in At(0,cell.x,cell.z).GetThingList(map).ToList())
                if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
        displayLow=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
        GenSpawn.Spawn(displayLow,At(0,74,58),map);displayLow.drafter.Drafted=true;
        foreach(var cell in new CellRect(57,54,5,9))map.terrainGrid.SetTerrain(At(2,cell.x,cell.z),TerrainDefOf.WoodPlankFloor);
        // Visible and covered corpses exercise the same real entities on different floors.
        var animal=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Cow"));
        GenSpawn.Spawn(animal,At(0,71,64),map);animal.Kill(null);displayCorpse=animal.Corpse;
        var covered=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Cow"));
        GenSpawn.Spawn(covered,At(0,58,58),map);covered.Kill(null);coveredCorpse=covered.Corpse;
        foreach(var p in new[]{displayHigh,displayLow})
        {
            if(p.equipment.Primary!=null)p.equipment.DestroyAllEquipment();
            p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle")));
            p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_Combat),JobCondition.InterruptForced);
            p.stances.SetStance(new Stance_Warmup(60000,p==displayHigh?displayLow:displayHigh,p.equipment.PrimaryEq.PrimaryVerb));
            p.rotationTracker.UpdateRotation();
        }
        Check("visual-fixture-both-native-verbs-have-unblocked-cross-floor-targets",displayHigh.equipment.PrimaryEq.PrimaryVerb.CanHitTargetFrom(displayHigh.Position,displayLow)&&displayLow.equipment.PrimaryEq.PrimaryVerb.CanHitTargetFrom(displayLow.Position,displayHigh));
        Check("pawn-facing-uses-local-east-west-not-band-distance",displayHigh.Rotation==Rot4.East&&displayLow.Rotation==Rot4.West);
        bool jumps=true;
        foreach(var target in new[]{displayHigh,displayLow})for(int from=0;from<3;from++)
        {
            LayerView.Switch(owner,from);Find.CameraDriver.SetRootPosAndSize(At(from,60,60).ToVector3Shifted(),9);
            // This is the same native entry point invoked by the colonist portrait.
            bool cross=!owner.IsViewedCell(target.Position);
            if(cross)Find.CameraDriver.PanToMapLoc(At(from,80,80));
            CameraJumper.TryJump(target,cross?CameraJumper.MovementMode.Pan:CameraJumper.MovementMode.Cut);
            Find.CameraDriver.Update();
            var root=(Vector3)AccessTools.Field(typeof(CameraDriver),"rootPos").GetValue(Find.CameraDriver)!;
            jumps&=owner.IsViewedCell(target.Position)&&Math.Abs(root.x-target.Position.x-.5f)<.01f&&Math.Abs(root.z-target.Position.z-.5f)<.01f;
        }
        Check("first-native-portrait-jump-and-next-camera-update-center-from-every-floor",jumps);
        LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.Selector.Select(displayHigh);
        Find.CameraDriver.SetRootPosAndSize(At(2,65,60).ToVector3Shifted(),13);
        map.fogGrid.Unfog(displayLow.Position);map.fogGrid.Unfog(displayCorpse.Position);
        Check("real-wood-finish-retains-structural-underfloor",owner.Structure.HasSurface(At(2,59,58))&&map.terrainGrid.UnderTerrainAt(At(2,59,58))==RimLayersDefOf.RL_StructuralFloor);
        Check("corpse-visibility-respects-every-higher-floor",LowerFloorBackdrop.Exposed(owner,displayCorpse.Position)&&!LowerFloorBackdrop.Exposed(owner,coveredCorpse.Position));
        var native=RimLayersDefOf.RL_StructuralFloor.DrawMatSingle;int queue=native.renderQueue;
        var background=BackgroundPass.MaterialFor(native,1);
        Check("background-queue-is-before-foreground-without-mutating-native-material",background!=native&&background.renderQueue<2000&&native.renderQueue==queue);
        Check("mist-is-spatially-drifting-not-screen-filter",DriftingMist.Center(2,2,1,30)!=DriftingMist.Center(2,2,1,0));
        File.WriteAllText(Path.Combine(directory,"presentation-baseline.json"),JsonSerializer.Serialize(new{high=displayHigh.ThingID,low=displayLow.ThingID,corpse=displayCorpse.ThingID,covered=coveredCorpse.ThingID}));
        GameDataSaveLoader.SaveGame("RimLayers_V1Presentation");
        CheckCustomAim();
        displayStarted=Time.realtimeSinceStartup;stage=290;ticks=0;
    }
    private static void RestorePresentation()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"presentation-baseline.json")));
        string Id(string key)=>baseline.RootElement.GetProperty(key).GetString()!;
        displayHigh=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==Id("high"));displayLow=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==Id("low"));
        displayCorpse=(Corpse)map.listerThings.AllThings.Single(t=>t.ThingID==Id("corpse"));coveredCorpse=(Corpse)map.listerThings.AllThings.Single(t=>t.ThingID==Id("covered"));
        Check("restart-keeps-real-corpses-and-floor-finish",displayCorpse.InnerPawn.Dead&&coveredCorpse.InnerPawn.Dead&&map.terrainGrid.TerrainAt(At(2,59,58))==TerrainDefOf.WoodPlankFloor);
        LayerView.Switch(owner,2);Find.CameraDriver.SetRootPosAndSize(At(2,65,60).ToVector3Shifted(),13);
        CheckCustomAim();
        displayStarted=Time.realtimeSinceStartup;stage=290;ticks=0;
    }
    private static void PresentationStep()
    {
        if(displayPicture==0&&Time.realtimeSinceStartup-displayStarted>1)
        {
            var root=(Vector3)AccessTools.Field(typeof(CameraDriver),"rootPos").GetValue(Find.CameraDriver)!;
            Check("visual-camera-remains-centered-after-real-camera-updates",Math.Abs(root.x-65.5f)<.1f&&Math.Abs(root.z-At(2,65,60).z-.5f)<.1f,root.ToString());
            Check("native-upper-and-background-lower-weapon-draws-have-correct-angle",weaponAngles.TryGetValue("high",out var high)&&weaponAngles.TryGetValue("low",out var low)&&Math.Abs(Mathf.DeltaAngle(high,90))<1&&Math.Abs(Mathf.DeltaAngle(low,270))<1,string.Join(";",weaponAngles.Select(p=>$"{p.Key}={p.Value}"))+$"; stances={displayHigh.stances.curStance.GetType().Name}/{displayLow.stances.curStance.GetType().Name}");
            if(displayHigh.def.defName=="NST_Nanami")Check("real-nanami-custom-render-worker-draws-cross-floor-equipped-weapon",nanamiWorkerDrawn);
            Check("background-weapon-final-mesh-is-projected-once-and-behind-floor",projectedWeapon);
            Check("real-corpse-is-submitted-by-native-dynamic-draw",LowerFloorBackdrop.LastCorpses>0&&LowerFloorBackdrop.LastDynamic>LowerFloorBackdrop.LastActors,$"corpses={LowerFloorBackdrop.LastCorpses},dynamic={LowerFloorBackdrop.LastDynamic},pawns={LowerFloorBackdrop.LastActors}");
            Check("real-textured-mist-patches-render",DriftingMist.LastPatches>0,$"patches={DriftingMist.LastPatches}");
            pendingScreenshot=Path.Combine(directory,mode+"-presentation-a.png");screenshotHold=3;displayPicture=1;
        }
        else if(displayPicture==1&&Time.realtimeSinceStartup-displayStarted>9)
        {
            pendingScreenshot=Path.Combine(directory,mode+"-presentation-b.png");screenshotHold=3;displayPicture=2;stage=11;ticks=0;
        }
    }
}
