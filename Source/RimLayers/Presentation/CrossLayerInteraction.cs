using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimLayers.Presentation;

[HarmonyPatch(typeof(FloatMenuContext), MethodType.Constructor, new[] { typeof(List<Pawn>), typeof(Vector3), typeof(Map) })]
internal static class FloatMenuContextPatch
{
    private static readonly AccessTools.FieldRef<FloatMenuContext, IntVec3> ClickedCellRef =
        AccessTools.FieldRefAccess<FloatMenuContext, IntVec3>("cachedClickedCell");
    private static readonly AccessTools.FieldRef<FloatMenuContext, Room> ClickedRoomRef =
        AccessTools.FieldRefAccess<FloatMenuContext, Room>("cachedClickedRoom");
    private static readonly AccessTools.FieldRef<FloatMenuContext, Zone> ClickedZoneRef =
        AccessTools.FieldRefAccess<FloatMenuContext, Zone>("cachedClickedZone");
    private static readonly AccessTools.FieldRef<FloatMenuContext, List<Thing>> ClickedThingsRef =
        AccessTools.FieldRefAccess<FloatMenuContext, List<Thing>>("cachedClickedThings");

    static void Postfix(FloatMenuContext __instance, Map map)
    {
        var owner = LayerView.Current;
        if (owner == null || owner.map != map || !RimLayersMod.Settings.CrossLayerInteraction) return;

        // If clicking on an exposed lower-floor thing, redirect clicked cell and position to it.
        var clickedThings = ClickedThingsRef(__instance);
        if (clickedThings != null && clickedThings.Count > 0)
        {
            var firstLowerThing = clickedThings.FirstOrDefault(t => t.MapHeld == map && !owner.IsViewedCell(t.PositionHeld) && LowerFloorBackdrop.Exposed(owner, t.PositionHeld));
            if (firstLowerThing != null)
            {
                ClickedCellRef(__instance) = firstLowerThing.PositionHeld;
                __instance.clickPosition = firstLowerThing.DrawPosHeld ?? firstLowerThing.PositionHeld.ToVector3Shifted();
                ClickedRoomRef(__instance) = firstLowerThing.PositionHeld.GetRoom(map);
                ClickedZoneRef(__instance) = firstLowerThing.PositionHeld.GetZone(map);
                return;
            }
        }

        // If clicking through open air (no surface and no roof on the viewed layer), find the topmost exposed lower cell.
        IntVec3 currentClickedCell = ClickedCellRef(__instance);
        if (owner.TryDecode(currentClickedCell, out var topCell))
        {
            bool viewedHasSurface = owner.Structure.HasSurface(currentClickedCell);
            bool viewedHasRoof = map.roofGrid.RoofAt(currentClickedCell) != null;

            // Never penetrate floors or roofs!
            if (!viewedHasSurface && !viewedHasRoof)
            {
                foreach (int floor in owner.Layout!.Below(owner.ViewedLayer))
                {
                    var lowerCell = owner.ToStorage(new LayerCell(floor, topCell.X, topCell.Z));
                    if (!lowerCell.InBounds(map)) continue;
                    if (!LowerFloorBackdrop.Exposed(owner, lowerCell)) continue;

                    if (owner.Structure.HasSurface(lowerCell) || lowerCell.Standable(map) || map.thingGrid.ThingsListAt(lowerCell).Any())
                    {
                        ClickedCellRef(__instance) = lowerCell;
                        __instance.clickPosition = lowerCell.ToVector3Shifted();
                        ClickedRoomRef(__instance) = lowerCell.GetRoom(map);
                        ClickedZoneRef(__instance) = lowerCell.GetZone(map);
                        break;
                    }
                }
            }
        }
    }
}

[HarmonyPatch(typeof(ThingSelectionUtility), nameof(ThingSelectionUtility.MultiSelectableThingsInScreenRectDistinct))]
internal static class MultiSelectableThingsInScreenRectDistinctPatch
{
    static void Postfix(Rect rect, ref IEnumerable<Thing> __result)
    {
        var owner = LayerView.Current;
        if (owner == null || !RimLayersMod.Settings.CrossLayerInteraction) return;

        Vector2 screenLoc = new Vector2(rect.x, (float)UI.screenHeight - rect.y);
        Vector2 screenLoc2 = new Vector2(rect.x + rect.width, (float)UI.screenHeight - (rect.y + rect.height));
        Vector3 v1 = UI.UIToMapPosition(screenLoc);
        Vector3 v2 = UI.UIToMapPosition(screenLoc2);

        int minX = Mathf.Min(Mathf.FloorToInt(v1.x), Mathf.FloorToInt(v2.x));
        int maxX = Mathf.Max(Mathf.FloorToInt(v1.x), Mathf.FloorToInt(v2.x));
        int minZ = Mathf.Min(Mathf.FloorToInt(v1.z), Mathf.FloorToInt(v2.z));
        int maxZ = Mathf.Max(Mathf.FloorToInt(v1.z), Mathf.FloorToInt(v2.z));

        List<Thing>? extra = null;
        var layout = owner.Layout!;

        foreach (int floor in layout.Below(owner.ViewedLayer))
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    IntVec3 topCellStorage = new IntVec3(x, 0, z);
                    if (!owner.TryDecode(topCellStorage, out var topLogical)) continue;

                    // Skip if viewed layer has a surface or roof (do not penetrate floor/roof)
                    if (owner.Structure.HasSurface(topCellStorage) || owner.map.roofGrid.RoofAt(topCellStorage) != null) continue;

                    var lowerCell = owner.ToStorage(new LayerCell(floor, topLogical.X, topLogical.Z));
                    if (!lowerCell.InBounds(owner.map)) continue;
                    if (!LowerFloorBackdrop.Exposed(owner, lowerCell)) continue;

                    var cellThings = owner.map.thingGrid.ThingsListAt(lowerCell);
                    if (cellThings == null || cellThings.Count == 0) continue;
                    for (int i = 0; i < cellThings.Count; i++)
                    {
                        var t = cellThings[i];
                        if (ThingSelectionUtility.SelectableByMapClick(t) && !t.def.neverMultiSelect)
                        {
                            extra ??= new List<Thing>();
                            if (!extra.Contains(t)) extra.Add(t);
                        }
                    }
                }
            }
        }

        if (extra != null && extra.Count > 0)
        {
            __result = __result.Concat(extra);
        }
    }
}

[HarmonyPatch(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.ProcessInputEvents))]
internal static class MultiPawnGotoControllerProcessInputEventsPatch
{
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, bool> ActiveRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, bool>("active");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> StartRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("start");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> EndRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("end");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, int?> LastUpdateTicksRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, int?>("lastUpdateTicks");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<Pawn>> PawnsRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, List<Pawn>>("pawns");

    static bool Prefix(MultiPawnGotoController __instance)
    {
        if (!ActiveRef(__instance)) return true;
        var pawns = PawnsRef(__instance);
        if (pawns.Count == 0 || !pawns[0].Spawned) return true;

        var owner = LayerView.Current;
        if (owner == null) return true;

        IntVec3 start = StartRef(__instance);
        if (!owner.TryDecode(start, out var startLogical)) return true;

        if (startLogical.LayerId == owner.ViewedLayer) return true;

        IntVec3 mouseCell = UI.MouseCell();
        if (!mouseCell.InBounds(pawns[0].Map)) return false;

        if (owner.TryDecode(mouseCell, out var mouseLogical))
        {
            mouseCell = owner.ToStorage(new LayerCell(startLogical.LayerId, mouseLogical.X, mouseLogical.Z));
        }

        int ticksGame = Find.TickManager.TicksGame;
        IntVec3 end = EndRef(__instance);
        int? lastTicks = LastUpdateTicksRef(__instance);

        if (mouseCell != end || !lastTicks.HasValue || (float)ticksGame > (float?)lastTicks + 10f)
        {
            if (mouseCell != end)
            {
                SoundDefOf.DragGoto.PlayOneShotOnCamera();
            }
            EndRef(__instance) = mouseCell;
            LastUpdateTicksRef(__instance) = ticksGame;
            __instance.RecomputeDestinations();
        }
        return false;
    }
}

[HarmonyPatch(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.Draw))]
internal static class MultiPawnGotoControllerDrawPatch
{
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, bool> ActiveRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, bool>("active");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> StartRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("start");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> EndRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("end");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<Pawn>> PawnsRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, List<Pawn>>("pawns");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<IntVec3>> DestsRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, List<IntVec3>>("dests");

    private static Material? circleMatCached;
    private static Material? lineMatCached;
    private static Material GotoCircleMat => circleMatCached ??= (Material)AccessTools.Field(typeof(MultiPawnGotoController), "GotoCircleMaterial").GetValue(null)!;
    private static Material GotoLineMat => lineMatCached ??= (Material)AccessTools.Field(typeof(MultiPawnGotoController), "GotoBetweenLineMaterial").GetValue(null)!;

    static bool Prefix(MultiPawnGotoController __instance)
    {
        if (!ActiveRef(__instance)) return false;
        var owner = LayerView.Current;
        if (owner == null) return true;

        IntVec3 start = StartRef(__instance);
        if (owner.IsViewedCell(start)) return true;

        var pawns = PawnsRef(__instance);
        var dests = DestsRef(__instance);
        IntVec3 end = EndRef(__instance);

        Vector3 s = new Vector3(1.7f, 1f, 1.7f);
        float num = AltitudeLayer.MetaOverlays.AltitudeFor();
        float addedAltitude = num + 0.03658537f;
        float addedAltitude2 = num - 0.03658537f;

        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            IntVec3 c = dests[i];
            if (c.IsValid && pawn.Spawned && !c.Fogged(pawn.Map))
            {
                Vector3 drawLoc = LowerFloorBackdrop.Project(owner, c.ToVector3ShiftedWithAltitude(num));
                pawn.Drawer.renderer.RenderPawnAt(drawLoc, Rot4.South);
                Vector3 pos = LowerFloorBackdrop.Project(owner, c.ToVector3ShiftedWithAltitude(addedAltitude));
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, s), GotoCircleMat, 0);
            }
        }
        Vector3 a = LowerFloorBackdrop.Project(owner, start.ToVector3ShiftedWithAltitude(addedAltitude2));
        Vector3 b = LowerFloorBackdrop.Project(owner, end.ToVector3ShiftedWithAltitude(addedAltitude2));
        GenDraw.DrawLineBetween(a, b, GotoLineMat, 0.9f);
        return false;
    }
}

[HarmonyPatch(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.OnGUI))]
internal static class MultiPawnGotoControllerOnGUIPatch
{
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, bool> ActiveRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, bool>("active");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> StartRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("start");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<Pawn>> PawnsRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, List<Pawn>>("pawns");
    private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<IntVec3>> DestsRef =
        AccessTools.FieldRefAccess<MultiPawnGotoController, List<IntVec3>>("dests");

    static bool Prefix(MultiPawnGotoController __instance)
    {
        if (!ActiveRef(__instance)) return false;
        var owner = LayerView.Current;
        if (owner == null) return true;

        IntVec3 start = StartRef(__instance);
        if (owner.IsViewedCell(start)) return true;

        var pawns = PawnsRef(__instance);
        var dests = DestsRef(__instance);

        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            IntVec3 c = dests[i];
            if (c.IsValid && pawn.Spawned && !c.Fogged(pawn.Map))
            {
                Vector3 worldPos = LowerFloorBackdrop.Project(owner, c.ToVector3Shifted());
                Vector2 screenPos = worldPos.MapToUIPosition();
                Vector2 pos = new Vector2(screenPos.x, screenPos.y + 5f);
                GenMapUI.DrawPawnLabel(pawn, pos, 0.5f);
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
internal static class PawnGotoActionPatch
{
    static void Postfix(IntVec3 gotoLoc, Pawn pawn)
    {
        var owner = LayerView.Current;
        if (owner != null && !owner.IsViewedCell(gotoLoc) && LowerFloorBackdrop.Exposed(owner, gotoLoc))
        {
            Vector3 proj = LowerFloorBackdrop.Project(owner, gotoLoc.ToVector3Shifted());
            FleckMaker.Static(proj, pawn.Map, FleckDefOf.FeedbackGoto);
        }
    }
}

[HarmonyPatch(typeof(GenUI), nameof(GenUI.TargetsAt))]
internal static class TargetsAtPatch
{
    static void Postfix(Vector3 clickPos, TargetingParameters clickParams, bool thingsOnly, ITargetingSource source, ref IEnumerable<LocalTargetInfo> __result)
    {
        if (thingsOnly || !RimLayersMod.Settings.CrossLayerInteraction) return;
        var owner = LayerView.Current;
        if (owner == null) return;

        IntVec3 mouseCell = UI.MouseCell();
        if (!owner.TryDecode(mouseCell, out var topCell)) return;

        if (!owner.Structure.HasSurface(mouseCell) && owner.map.roofGrid.RoofAt(mouseCell) == null)
        {
            foreach (int floor in owner.Layout!.Below(owner.ViewedLayer))
            {
                var lowerCell = owner.ToStorage(new LayerCell(floor, topCell.X, topCell.Z));
                if (!lowerCell.InBounds(owner.map)) continue;
                if (!LowerFloorBackdrop.Exposed(owner, lowerCell)) continue;

                if (clickParams.CanTarget(new TargetInfo(lowerCell, owner.map), source))
                {
                    __result = __result.Concat(new LocalTargetInfo[] { lowerCell });
                    break;
                }
            }
        }
    }
}
