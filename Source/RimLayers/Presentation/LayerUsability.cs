using HarmonyLib;
using RimLayers.Core;
using RimLayers.Structure;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Presentation;

[HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
internal static class FloorHotkeys
{
    [HarmonyPriority(Priority.First)]
    static void Prefix()
    {
        LayerTransition.OnGUI();
        var owner=LayerView.Current;
        if(owner==null || Event.current.type!=EventType.KeyDown || GUIUtility.keyboardControl!=0 || Find.WindowStack.Windows.Any(w=>w.forcePause))return;
        int step=RimLayersDefOf.RL_LayerUp.KeyDownEvent?1:RimLayersDefOf.RL_LayerDown.KeyDownEvent?-1:0;
        if(step==0&&Event.current.alt)step=Event.current.keyCode==KeyCode.UpArrow?1:Event.current.keyCode==KeyCode.DownArrow?-1:0;
        if(step!=0){LayerTransition.RequestRelative(owner,step);Event.current.Use();}
    }
}

internal static class LandingMarkers
{
    internal static IEnumerable<Building_Stairs> Visible(LayerMapComponent owner)=>
        owner.map.listerThings.AllThings.OfType<Building_Stairs>().Where(s=>s.ConnectsAdjacent)
            .Where(s=>s.UpperCell.IsValid&&owner.IsViewedCell(s.UpperCell)&&owner.Structure.HasSurface(s.UpperCell));
    internal static void Draw(LayerMapComponent owner)
    {
        if(owner!=LayerView.Current || !RimLayersMod.Settings.LandingMarkers)return;
        foreach(var stair in Visible(owner))
            Graphics.DrawMesh(MeshPool.plane10,stair.UpperCell.ToVector3Shifted().WithY(AltitudeLayer.Building.AltitudeFor()),
                Quaternion.Euler(0,180,0),stair.Graphic.MatSingle,0);
    }
    internal static void OnGUI(LayerMapComponent owner)
    {
        if(owner!=LayerView.Current || !RimLayersMod.Settings.LandingMarkers || LayerTransition.HasPicture)return;
        foreach(var stair in Visible(owner))
        {
            var point=GenMapUI.LabelDrawPosFor(stair.UpperCell);
            var rect=new Rect(point.x-72,point.y+10,144,23);
            owner.TryDecode(stair.Position,out var origin);
            if(Widgets.ButtonText(rect,"RL_ReturnToLevel".Translate(owner.LevelName(origin.LayerId))))LayerTransition.Request(owner,origin.LayerId);
            TooltipHandler.TipRegion(rect,"RL_ReturnToLevelHelp".Translate());
        }
    }
}

[HarmonyPatch(typeof(Designator_Build),nameof(Designator_Build.SelectedUpdate))]
internal static class SlabPlanningOverlay
{
    static void Postfix(Designator_Build __instance)
    {
        var owner=LayerView.Current;
        if(!RimLayersMod.Settings.PlanningBounds||owner==null||__instance.PlacingDef!=RimLayersDefOf.RL_StructuralFloor&&__instance.PlacingDef.defName!="RL_ShipStructuralFloor")return;
        var cells=new List<IntVec3>();
        foreach(var cell in Find.CameraDriver.CurrentViewRect)
            if(owner.IsViewedCell(cell)&&!owner.Structure.HasSurface(cell)&&owner.Structure.Validate(__instance.PlacingDef,cell,Rot4.North,planning:true).Accepted)cells.Add(cell);
        if(cells.Count!=0)GenDraw.DrawFieldEdges(cells,new Color(.3f,.9f,.6f,.8f));
    }
}
