using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Drops the view to the ground floor when SOS2 opens a ship-move or ship-landing ghost.
/// </summary>
/// <remarks>
/// The move/landing blueprint is an <c>Install</c> designator holding one fake minified ship, and it
/// can only be placed on the floor the hull stands on: <c>PlaceWorker_MoveShip</c> validates the
/// bottom band against the destination. Everything about that works only if the mouse is on that
/// floor, and RimLayers deliberately treats a cell outside the viewed floor as no cell at all
/// (<c>MouseCellPatch</c> returns <c>IntVec3.Invalid</c>). A player who launched from upstairs and is
/// still looking upstairs therefore gets a designator that cannot accept any cell, with nothing on
/// screen saying why.
/// <para>
/// Switching the view is the honest fix: the ghost has exactly one floor it can go on, so the tool
/// should put the player on it rather than let them fail invisibly.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2SketchFloorPatch
{
    private static MethodBase? TargetMethod()
    {
        System.Type? cache = AccessTools.TypeByName("SaveOurShip2.SpaceShipCache");
        MethodInfo? original = cache == null
            ? null
            : AccessTools.Method(cache, "CreateShipSketch", new[]
            {
                typeof(Map), typeof(byte), typeof(bool), typeof(float)
            });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static void Postfix(Map targetMap)
    {
        if (!Sos2Compatibility.Active) return;
        EnsureGroundFloor(targetMap);
    }

    /// <summary>
    /// Moves the viewed floor to the map's ground level when it is not already there. Returns true
    /// when the view actually moved, so the behaviour can be asserted without driving the UI.
    /// </summary>
    internal static bool EnsureGroundFloor(Map? map)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return false;
        if (!layout.TryGetLayerAtElevation(0, out int ground)) return false;
        if (owner.ViewedLayer == ground) return false;
        // Switch refuses a component that is not the currently viewed map, which is the case while the
        // game is still pointed elsewhere. The caller has already moved CurrentMap by this point, so a
        // refusal here means the view is genuinely not ours to move.
        if (!LayerView.Switch(owner, ground)) return false;
        // The ghost is one cell tall on screen but the hull it stands for is not; keep the camera on the
        // floor too, otherwise the switch moves the world out from under the cursor.
        CameraJumper.TryJump(owner.ToStorage(new LayerCell(ground, owner.Layout.Width / 2, owner.Layout.Height / 2)), map);
        return true;
    }
}
