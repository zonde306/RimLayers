using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;
using RimWorld;
using RimWorld.Planet;

namespace RimLayers.Presentation;

public static class LayerView
{
    public static LayerMapComponent? Current => CurrentMapComponent();
    private static LayerMapComponent? CurrentMapComponent() =>
        Verse.Current.ProgramState == ProgramState.Playing && !WorldRendererUtility.WorldSelected ?
        LayerMapComponent.For(Find.CurrentMap) : null;

    private static readonly AccessTools.FieldRef<DesignatorManager, DesignationDragger> Dragger =
        AccessTools.FieldRefAccess<DesignatorManager, DesignationDragger>("dragger");

    internal static void EndDrag(DesignatorManager manager) => Dragger(manager).EndDrag();

    /// <summary>
    /// A floor switch deselects every non-Pawn so a leftover build tool cannot act on the floor you
    /// just left. An object that belongs to no single floor is the exception: an airborne aircraft
    /// rides the view with you, so dropping the selection would make it impossible to steer it
    /// across floors. Compatibility modules register a predicate here instead of this file learning
    /// about third-party types.
    /// </summary>
    internal static readonly List<Func<object, bool>> SelectionKeepers = new();

    private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPos =
        AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

    private static readonly AccessTools.FieldRef<CameraDriver,CameraPanner> Panner=AccessTools.FieldRefAccess<CameraDriver,CameraPanner>("panner");
    private static readonly AccessTools.FieldRef<CameraDriver,Vector3> Velocity=AccessTools.FieldRefAccess<CameraDriver,Vector3>("velocity");
    internal static Vector3 CameraRoot => RootPos(Find.CameraDriver);
    internal static void CancelCameraMotion()
    {Panner(Find.CameraDriver).Moving=false;Velocity(Find.CameraDriver)=Vector3.zero;}

    public static bool SwitchRelative(LayerMapComponent component,int direction) => component.Layout!=null&&component.Layout.TryGetAdjacentLayer(component.ViewedLayer,direction,out int target)&&Switch(component,target);

    public static bool Switch(LayerMapComponent component, int layer)
    {
        if (component != Current || component.Layout == null || (uint)layer >= (uint)component.Layout.LayerCount) return false;
        if (!LayerTransition.Committing) LayerTransition.Cancel();
        int old = component.ViewedLayer;
        if (old == layer) return true;
        var camera = Find.CameraDriver;
        Vector3 position = RootPos(camera);
        position.z += (layer - old) * component.Layout.Stride;
        CancelCameraMotion();
        component.SetViewedLayer(layer);
        // Actors remain selected so a floor switch can actually be used to give a cross-floor order.
        // A targeting command also owns a stable caster reference (including a selected turret).
        // Install/reinstall resolves its source through SingleSelectedThing on every update.
        // Keep that selection and the existing tool instance (including rotation/material).
        var installSource = Find.DesignatorManager.SelectedDesignator is Designator_Install
            ? Find.Selector.SingleSelectedThing : null;
        if(!Find.Targeter.IsTargeting)
            foreach(object selected in Find.Selector.SelectedObjectsListForReading.ToList())
                if(selected is not Pawn && !ReferenceEquals(selected, installSource) &&
                    !SelectionKeepers.Any(keep => keep(selected))) Find.Selector.Deselect(selected);
        // Only the old drag belongs to the outgoing floor; a new placement uses the new floor.
        EndDrag(Find.DesignatorManager);
        camera.SetRootPosAndSize(position, camera.RootSize);
        return true;
    }

    internal static Rect ControlsRect(LayerMapComponent component) => new(UI.screenWidth - 238f, 160f, 226f,
        (component.Elevation(component.ViewedLayer)>0?106f:70f)+(component.map.GetComponent<RimLayers.Vehicles.VehicleTraffic>().RecoveryCount>0?34f:0f)+(RimLayers.Vehicles.VehicleWorldTravel.Current.HasWaiting(component.map)?34f:0f));
    internal static bool MouseOverFloorControls(LayerMapComponent component) => RimLayersMod.Settings.FloorPanel && ControlsRect(component).Contains(Event.current.mousePosition);

    internal static void DrawControls(LayerMapComponent component)
    {
        if (Current != component || !RimLayersMod.Settings.FloorPanel) return;
        Rect panel = ControlsRect(component);
        Widgets.DrawWindowBackground(panel);
        if (Widgets.ButtonText(new Rect(panel.x + 8, panel.y + 8, 34, 28), "−"))
            LayerTransition.RequestRelative(component, -1);
        Widgets.Label(new Rect(panel.x + 50, panel.y + 13, 126, 28),
            component.LevelName(component.ViewedLayer));
        if (Widgets.ButtonText(new Rect(panel.xMax - 42, panel.y + 8, 34, 28), "+"))
            LayerTransition.RequestRelative(component, 1);
        if(RimLayersMod.Settings.KeyHints) Widgets.Label(new Rect(panel.x + 8, panel.y + 42, panel.width - 16, 24),
            "RL_KeyHints".Translate(RimLayersDefOf.RL_LayerDown.MainKeyLabel,RimLayersDefOf.RL_LayerUp.MainKeyLabel));
        TooltipHandler.TipRegion(new Rect(panel.x,panel.y,panel.width,70), "RL_SwitchHelp".Translate());
        if(component.Elevation(component.ViewedLayer)>0&&Widgets.ButtonText(new Rect(panel.x+8,panel.y+72,panel.width-16,26),"RL_MountainMigration".Translate()))
            Find.WindowStack.Add(new RimLayers.Mountains.Dialog_MountainMigration(component));
        var traffic=component.map.GetComponent<RimLayers.Vehicles.VehicleTraffic>();
        bool landingWaiting=RimLayers.Vehicles.VehicleWorldTravel.Current.HasWaiting(component.map);
        if(traffic.RecoveryCount>0)traffic.DrawRecoveryControl(new Rect(panel.x+8,panel.yMax-32-(landingWaiting?34:0),panel.width-16,26));
        if(landingWaiting)RimLayers.Vehicles.VehicleWorldTravel.Current.DrawRecovery(new Rect(panel.x+8,panel.yMax-32,panel.width-16,26),component.map);
        var mouse=UI.MouseCell();
        if(component.IsViewedCell(mouse)&&!mouse.Fogged(component.map)&&component.Mountains.IsNaturalFloor(mouse))
            Widgets.Label(new Rect(panel.x+8,panel.yMax+5,panel.width-16,52),"RL_NaturalFloorImmutable".Translate());
    }

    internal static void ShowTarget(IntVec3 cell, Map map)
    {
        var component = LayerMapComponent.For(map);
        if (component != null && component.TryDecode(cell, out var logical))
        {
            if(component==Current)Switch(component,logical.LayerId);
            else component.SetViewedLayer(logical.LayerId);
        }
    }
}

[HarmonyPatch(typeof(CameraDriver), "ApplyPositionToGameObject")]
internal static class CameraClampPatch
{
    static void Prefix(ref Vector3 ___rootPos, ref float ___rootSize, ref float ___desiredSize, ref Vector3 ___velocity, ref int ___lastViewRectGetFrame)
    {
        var component = LayerView.Current;
        if (component?.Layout == null) return;
        var layout = component.Layout;
        float aspect = (float)Math.Max(UI.screenWidth, 1) / Math.Max(UI.screenHeight, 1);
        float maxSize = Math.Max(0.1f, Math.Min((layout.Width - 4f) / (2f * aspect), (layout.Height - 4f) / 2f));
        ___rootSize = Math.Clamp(___rootSize, 0.1f, maxSize);
        ___desiredSize = Math.Clamp(___desiredSize, 0.1f, maxSize);
        float halfWidth = ___rootSize * aspect;
        float x = Math.Clamp(___rootPos.x, halfWidth + 1, layout.Width - halfWidth - 1);
        float offset = component.ViewedLayer * layout.Stride;
        float z = Math.Clamp(___rootPos.z, offset + ___rootSize + 1, offset + layout.Height - ___rootSize - 1);
        if (x != ___rootPos.x) ___velocity.x = 0;
        if (z != ___rootPos.z) ___velocity.z = 0;
        ___rootPos.x = x; ___rootPos.z = z;
        ___lastViewRectGetFrame = -1;
    }
}

[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
internal static class ViewRectPatch
{
    static void Postfix(ref CellRect __result)
    {
        var c = LayerView.Current;
        if (c == null) return;
        CellRect bounds = c.LayerRect(c.ViewedLayer);
        __result.minX = Math.Max(__result.minX, bounds.minX);
        __result.maxX = Math.Min(__result.maxX, bounds.maxX);
        __result.minZ = Math.Max(__result.minZ, bounds.minZ);
        __result.maxZ = Math.Min(__result.maxZ, bounds.maxZ);
    }
}

[HarmonyPatch(typeof(Section), nameof(Section.DrawSection))]
internal static class SectionViewPatch
{
    static bool Prefix(Section __instance)
    {
        var c = LayerMapComponent.For(__instance.map);
        return c == null || c.LayerRect(c.ViewedLayer).Overlaps(__instance.CellRect) ||
            c.map==Find.CurrentMap && WorldComponent_GravshipController.GravshipRenderInProgess &&
            WorldComponent_GravshipController.GravshipRenderBounds.Overlaps(__instance.CellRect);
    }
}

[HarmonyPatch(typeof(UI), nameof(UI.MouseCell))]
internal static class MouseCellPatch
{
    static void Postfix(ref IntVec3 __result)
    {
        var c = LayerView.Current;
        if (c != null && !c.IsViewedCell(__result)) __result = IntVec3.Invalid;
    }
}

[HarmonyPatch(typeof(GenUI), nameof(GenUI.ThingsUnderMouse))]
internal static class PickThingsPatch
{
    static bool Prefix(Vector3 clickPos, ref List<Thing> __result)
    {
        var c = LayerView.Current;
        if (LowerFloorBackdrop.Picking || c == null || c.IsViewedCell(clickPos.ToIntVec3())) return true;
        __result = new List<Thing>();
        return false;
    }
    static void Postfix(List<Thing> __result,Vector3 clickPos,float pawnWideClickRadius,TargetingParameters clickParams,ITargetingSource source)
    {
        if(LowerFloorBackdrop.Picking)return;
        var c = LayerView.Current;
        if (c == null)return;
        __result.RemoveAll(t => t.MapHeld != c.map || !c.IsViewedCell(t.PositionHeld));
        if(Find.Targeter.IsTargeting)__result.AddRange(LowerFloorBackdrop.Pick(c,clickPos,pawnWideClickRadius,clickParams,source));
    }
}

[HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.ProcessInputEvents))]
internal static class InputBoundaryPatch
{
    static bool Prefix(DesignatorManager __instance)
    {
        var c = LayerView.Current;
        if (c == null || c.IsViewedCell(UI.MouseCell()) || Event.current.type is not (EventType.MouseDown or EventType.MouseUp))
            return true;
        // Release an active drag even when its mouse-up is outside the logical layer.
        if (Event.current.type == EventType.MouseUp && Event.current.button == 0) LayerView.EndDrag(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(CameraJumper), "JumpLocalInternal")]
internal static class JumpLayerPatch
{
    static void Prefix(IntVec3 localCell,ref CameraJumper.MovementMode mode)
    {
        LayerTransition.Cancel();
        var owner=LayerView.Current;
        if(owner==null||!owner.TryDecode(localCell,out var target))return;
        // Switch before the native camera jump/clamp. Cross-floor pans cannot
        // interpolate through reserved storage bands; same-floor smoothing stays native.
        if(owner.ViewedLayer!=target.LayerId)
        {LayerView.CancelCameraMotion();owner.SetViewedLayer(target.LayerId);mode=CameraJumper.MovementMode.Cut;}
    }
}

[HarmonyPatch(typeof(Selector), nameof(Selector.Select))]
internal static class SelectionLayerPatch
{
    static void Postfix(object obj)
    {
        if (obj is Thing thing && thing.MapHeld == Find.CurrentMap)
            LayerView.ShowTarget(thing.PositionHeld, thing.MapHeld);
    }
}
