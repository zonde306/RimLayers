using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Presentation;

internal static class CombatReadout
{
    private static readonly Func<Targeter,bool,LocalTargetInfo> UnderMouse=
        AccessTools.MethodDelegate<Func<Targeter,bool,LocalTargetInfo>>(AccessTools.Method(typeof(Targeter),"CurrentTargetUnderMouse"));
    internal static bool Query(Targeter targeter,out LayerMapComponent owner,out Verb verb,out FireSolution solution)
    {
        owner=null!;verb=null!;solution=default;
        if(!targeter.IsTargeting)return false;
        verb=targeter.targetingSource as Verb ??
            (targeter.targetingSource as Building_TurretGun)?.AttackVerb!;
        if(verb is not Verb_LaunchProjectile || LayerMapComponent.For(verb.caster?.Map) is not {} map)return false;
        var target=UnderMouse(targeter,false);
        if(!target.IsValid || !map.TryDecode(target.Cell,out _) ||
            CombatCoordinates.Cross(map.map,verb.caster!.Position,target.Cell)==null&&!ShotProfiles.Curved(verb))return false;
        owner=map;solution=owner.Combat.SolveWeapon(verb.caster,verb.caster!.Position,target,verb);
        return solution.Samples!=null;
    }
    internal static string TextFor(LayerMapComponent owner,Verb verb,FireSolution solution)
    {
        string status=solution.LaunchBlocked?"RL_CombatLaunchBlocked".Translate().ToString():
            !solution.InRange?"RL_CombatOutOfRange".Translate().ToString():
            !solution.Clear?"RL_CombatBlocked".Translate().ToString():
            solution.Curved&&solution.Obstruction!=null?"RL_CombatIntercepted".Translate().ToString():
            "RL_CombatClear".Translate().ToString();
        string Label(ShotObstruction hit) => hit.ThingKey!=0 && owner.AtElevation(hit.Layer,hit.X,hit.Z).GetThingList(owner.map).FirstOrDefault(t=>t.thingIDNumber==hit.ThingKey) is {} thing
            ? thing.LabelCap.ToString() : ("RL_Combat"+hit.Kind).Translate().ToString();
        string obstacle=solution.Obstruction is {} hit
            ? "RL_CombatObstacle".Translate(Label(hit),owner.LevelName(owner.Layout!.LayerAt(hit.Layer)),hit.X,hit.Z).ToString():"";
        return "RL_CombatRange".Translate(owner.LevelName(solution.AimCell.LayerId),solution.EffectiveRange.ToString("F1"),
            (solution.EffectiveRange/verb.EffectiveRange).ToString("P0"))+"\n"+
            (solution.Curved?"RL_CombatArc".Translate().ToString():"RL_CombatExposure".Translate(solution.Exposure.ToString("P0")).ToString())+
            "\n"+status+(obstacle.Length>0?"\n"+obstacle:"");
    }
}

[HarmonyPatch(typeof(Targeter),nameof(Targeter.TargeterOnGUI))]
internal static class LayerCombatReadoutPatch
{
    static void Postfix(Targeter __instance)
    {
        if(!RimLayersMod.Settings.CombatInfo || !CombatReadout.Query(__instance,out var owner,out var verb,out var solution))return;
        var rect=new Rect(16,140,340,116);
        Widgets.DrawWindowBackground(rect);
        var font=Text.Font;var color=GUI.color;
        try{Text.Font=GameFont.Small;GUI.color=Color.white;Widgets.Label(rect.ContractedBy(10),CombatReadout.TextFor(owner,verb,solution));}
        finally{Text.Font=font;GUI.color=color;}
    }
}

[HarmonyPatch(typeof(Targeter),nameof(Targeter.TargeterUpdate))]
internal static class LayerTrajectoryOverlayPatch
{
    static void Postfix(Targeter __instance)
    {
        if(!RimLayersMod.Settings.Trajectory || !CombatReadout.Query(__instance,out var owner,out _,out var solution))return;
        int pieces=solution.Curved?48:24;
        // Use the clear sample for direct fire; curves show their predicted first collision.
        double until=solution.Curved||!solution.Clear ? solution.Obstruction?.Time??1 : 1;
        var color=solution.GeometricallyPossible?SimpleColor.Green:SimpleColor.Red;
        for(int i=0;i<pieces;i++)
        {
            var a=ExposureGeometry.Point(solution.Start,solution.Aim,solution.ArcHeight,until*i/pieces);
            var b=ExposureGeometry.Point(solution.Start,solution.Aim,solution.ArcHeight,until*(i+1)/pieces);
            foreach(var span in ShotGeometry.Cells(a,b,3))
            {
                if(span.Layer!=owner.Elevation(owner.ViewedLayer))continue;
                var left=a.At(b,span.Start);var right=a.At(b,span.End);
                Vector3 Project(ShotPoint p)=>new((float)p.X,0.1f,(float)p.Z+owner.ViewedLayer*owner.Layout!.Stride);
                GenDraw.DrawLineBetween(Project(left),Project(right),color,0.12f);
            }
        }
        var center=owner.ToStorage(new LayerCell(owner.ViewedLayer,(int)solution.Start.X,(int)solution.Start.Z));
        GenDraw.DrawRadiusRing(center,solution.EffectiveRange);
        if(solution.Curved && solution.Obstruction is {} impact && impact.Layer==owner.Elevation(owner.ViewedLayer))
            GenDraw.DrawTargetHighlight(owner.AtElevation(impact.Layer,impact.X,impact.Z));
    }
}
