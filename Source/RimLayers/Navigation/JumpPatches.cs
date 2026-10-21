using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Navigation;

[HarmonyPatch(typeof(Verb_Jump),nameof(Verb_Jump.CanHitTargetFrom))]
internal static class LayerJumpTargetPatch
{
    static bool Prefix(Verb_Jump __instance,IntVec3 root,LocalTargetInfo targ,ref bool __result)
    {
        if(JumpService.Cross(__instance.CasterPawn,root,targ.Cell)==null)return true;
        __result=JumpService.Solve(__instance.CasterPawn,root,targ.Cell,__instance.EffectiveRange,out _,out _);return false;
    }
}
[HarmonyPatch(typeof(Verb_Jump),nameof(Verb_Jump.OrderForceTarget))]
internal static class LayerJumpOrderPatch
{
    static bool Prefix(Verb_Jump __instance,LocalTargetInfo target)
    {
        var pawn=__instance.CasterPawn;
        if(JumpService.Cross(pawn,pawn.Position,target.Cell)==null)return true;
        if(!__instance.ValidateTarget(target))return false;
        var job=JobMaker.MakeJob(JobDefOf.CastJump,target);job.verbToUse=__instance;
        var previous=JumpService.HandlingPawn;JumpService.HandlingPawn=pawn;
        try
        {
            if(pawn.jobs.TryTakeOrderedJob(job,JobTag.Misc))FleckMaker.Static(target.Cell,pawn.Map,FleckDefOf.FeedbackGoto);
        }
        finally{JumpService.HandlingPawn=previous;}
        return false;
    }
}
[HarmonyPatch(typeof(Verb_Jump),"TryCastShot")]
internal static class LayerJumpLaunchPatch
{
    static bool Prefix(Verb_Jump __instance,ref bool __result)
    {
        var pawn=__instance.CasterPawn;
        if(JumpService.Cross(pawn,pawn.Position,__instance.CurrentTarget.Cell)==null)return true;
        __result=JumpService.Launch(__instance);return false;
    }
}
[HarmonyPatch(typeof(Verb_Jump),nameof(Verb_Jump.DrawHighlight))]
internal static class LayerJumpHighlightPatch
{
    static bool Prefix(Verb_Jump __instance,LocalTargetInfo target)
    {
        var pawn=__instance.CasterPawn;var owner=LayerMapComponent.For(pawn?.Map);
        if(owner==null||!owner.TryDecode(pawn!.Position,out var source)||source.LayerId==owner.ViewedLayer)return true;
        double dh=3*(owner.Elevation(owner.ViewedLayer)-owner.Elevation(source.LayerId));
        double squared=__instance.EffectiveRange*__instance.EffectiveRange-dh*dh;
        if(squared>=0)GenDraw.DrawRadiusRing(owner.ToStorage(new LayerCell(owner.ViewedLayer,source.X,source.Z)),(float)Math.Sqrt(squared));
        if(target.IsValid&&owner.IsViewedCell(target.Cell))
        {
            bool valid=JumpService.Solve(pawn,pawn.Position,target.Cell,__instance.EffectiveRange,out var arc,out _);
            GenDraw.DrawFieldEdges(new List<IntVec3>{target.Cell},valid?Color.green:Color.red);
            if(valid)
                for(int i=0;i<32;i++)
                {
                    var a=arc.Point((double)i/32);var b=arc.Point((double)(i+1)/32);
                    foreach(var span in RimLayers.Combat.ShotGeometry.Cells(a,b,3))
                    {
                        if(span.Layer!=owner.Elevation(owner.ViewedLayer))continue;
                        Vector3 Project(RimLayers.Combat.ShotPoint p)=>new((float)p.X,.1f,(float)p.Z+owner.ViewedLayer*owner.Layout!.Stride);
                        GenDraw.DrawLineBetween(Project(a.At(b,span.Start)),Project(a.At(b,span.End)),SimpleColor.Green,.12f);
                    }
                }
        }
        return false;
    }
}
[HarmonyPatch(typeof(Verb_Jump),nameof(Verb_Jump.OnGUI))]
internal static class LayerJumpReadoutPatch
{
    static void Postfix(Verb_Jump __instance,LocalTargetInfo target)
    {
        var pawn=__instance.CasterPawn;
        if(JumpService.Cross(pawn,pawn.Position,target.Cell)==null)return;
        bool valid=JumpService.Solve(pawn,pawn.Position,target.Cell,__instance.EffectiveRange,out _,out var reason);
        var rect=new Rect(16,140,360,66);
        Widgets.DrawWindowBackground(rect);
        var old=GUI.color;GUI.color=valid?Color.white:new Color(1,.65f,.45f);
        Widgets.Label(rect.ContractedBy(10),reason.Translate());GUI.color=old;
    }
}

// Keep native despawn/respawn job cleanup from dropping cargo between holder transfers.
// Lease carrying only during this cross-layer order, launch or landing transaction.
[HarmonyPatch(typeof(Pawn_JobTracker),"StartJob")]
internal static class LayerJumpKeepCarryStartPatch
{
    static void Prefix(Pawn_JobTracker __instance,ref bool? keepCarryingThingOverride)
    {
        if(JumpService.HandlingPawn!=null&&__instance==JumpService.HandlingPawn.jobs)keepCarryingThingOverride=true;
    }
}
[HarmonyPatch(typeof(Pawn_JobTracker),"CleanupCurrentJob")]
internal static class LayerJumpKeepCarryEndPatch
{
    static void Prefix(Pawn_JobTracker __instance,ref bool? carryThingAfterJobOverride)
    {
        if(JumpService.HandlingPawn!=null&&__instance==JumpService.HandlingPawn.jobs)carryThingAfterJobOverride=true;
    }
}
[HarmonyPatch(typeof(PawnFlyer),nameof(PawnFlyer.MakeFlyer))]
internal static class LayerJumpCarriedFlyerPatch
{
    static void Prefix(ThingDef flyingDef,ref bool flyWithCarriedThing)
    {
        if(flyingDef.thingClass==typeof(LayerJumpFlyer))flyWithCarriedThing=true;
    }
}

[HarmonyPatch(typeof(Pawn),nameof(Pawn.GetGizmos))]
internal static class LayerJumpHeldPawnGizmoPatch
{
    static void Postfix(Pawn __instance,ref IEnumerable<Gizmo> __result)
    {
        // Native selection remains on the held colonist, not on the ethereal carrier.
        if(__instance.ParentHolder is LayerJumpFlyer {Waiting:true} flyer)
            __result=__result.Concat(new[]{flyer.RetryGizmo()});
    }
}
