using HarmonyLib;
using RimLayers.Presentation;
using UnityEngine;
using Verse;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static ThingDef? vehiclePreviewDef;
    private static IntVec3 vehiclePreviewCell;
    private static bool vehiclePreviewBound;
    private static void StartVehiclePreview(string defName,IntVec3 cell,string file)
    {
        if(!vehiclePreviewBound)
        {
            new Harmony("rimlayers.vehicles.preview.test").Patch(AccessTools.Method(typeof(MapComponentUtility),nameof(MapComponentUtility.MapComponentOnDraw)),
                postfix:new HarmonyMethod(typeof(V1Integration),nameof(DrawVehiclePreview)));
            vehiclePreviewBound=true;
        }
        vehiclePreviewDef=DefDatabase<ThingDef>.GetNamed(defName);vehiclePreviewCell=cell;
        owner.TryDecode(cell,out var at);LayerView.Switch(owner,at.LayerId);
        Find.Selector.ClearSelection();Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted(),12);
        pendingScreenshot=Path.Combine(directory,file);screenshotHold=4;
    }
    private static void DrawVehiclePreview()
    {
        if(vehiclePreviewDef==null||Find.CurrentMap!=map)return;
        var allowed=owner.Structure.Validate(vehiclePreviewDef,vehiclePreviewCell,Rot4.North,true).Accepted;
        GhostDrawer.DrawGhostThing(vehiclePreviewCell,Rot4.North,vehiclePreviewDef,null!,allowed?Color.green:Color.red,AltitudeLayer.Blueprint);
    }
}
