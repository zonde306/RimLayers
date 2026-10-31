using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Puts the camera on the hull after a launch instead of on the map's arithmetic centre.
/// </summary>
/// <remarks>
/// <c>ShipInteriorMod2.LaunchShip</c> ends with <c>CameraJumper.TryJump(map.Center, map)</c>. On a
/// layered map <c>Map.Center</c> is <c>(Width / 2, 0, StorageHeight / 2)</c>, and the storage height
/// spans every band, so that point sits in the gap between bands rather than on the ship. Adding the
/// launch offset on top then lands the camera off the hull entirely - what the player reports as "the
/// camera does not follow the ship when it takes off".
/// <para>
/// The hull's own cells are the only stable answer: they are already in storage coordinates on the
/// destination map once <c>MoveShip</c> returns.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2LaunchFocusPatch
{
    private static MethodBase? TargetMethod()
    {
        MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipInteriorType, "LaunchShip", new[] { typeof(Building) });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static void Postfix()
    {
        if (!Sos2Compatibility.Active) return;
        Map? map = Find.CurrentMap;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (map == null || owner?.Layout is not { LayerCount: > 1 }) return;
        if (!TryCentre(map, out IntVec3 centre)) return;
        CameraJumper.TryJump(centre, map);
    }

    /// <summary>
    /// Averages the ship cells that live in the ground band, so the camera lands on the deck the
    /// player is looking at rather than in the middle of the storage range.
    /// </summary>
    private static bool TryCentre(Map map, out IntVec3 centre)
    {
        centre = default;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { } layout || Sos2Compatibility.ShipCellsOf(map) is not { } cells) return false;
        long sumX = 0, sumZ = 0;
        int count = 0;
        foreach (object? key in cells.Keys)
        {
            if (key is not IntVec3 cell || !owner.TryDecode(cell, out Layout.LayerCell logical)) continue;
            if (layout.ElevationOf(logical.LayerId) != 0) continue;
            sumX += logical.X;
            sumZ += logical.Z;
            count++;
        }
        if (count == 0) return false;
        centre = owner.ToStorage(new Layout.LayerCell(owner.ViewedLayer, (int)(sumX / count), (int)(sumZ / count)));
        return centre.InBounds(map);
    }
}
