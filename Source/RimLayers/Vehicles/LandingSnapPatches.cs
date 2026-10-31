using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Vehicles;

/// <summary>
/// Airstrike-free landing for Vehicle Framework vehicles on a layered map. Two problems, one rule.
///
/// 1. The ghost is drawn on whatever cell the mouse is on, and on a layered map the cursor can easily
///    be on a band that has no floor at all - the open air above a deck, or the space around a hull.
///    The player sees a red ghost and has to guess which floor to switch to. Here the cell is pulled
///    down to the topmost solid floor in its own column, so the ghost sits where the vehicle will
///    actually land.
/// 2. The hard part of a layered column is that a floor above the landing cell is a ceiling for every
///    floor below it. A vehicle cannot punch through a deck, so landing "under" one is a request the
///    map will silently refuse. That case gets its own reason instead of the generic "no floor".
/// </summary>
internal static class LandingSnapPatches
{
    private const string NoRoomBelow = "RL_VehicleLandingNoFloor";

    private static readonly Harmony Id = new("rimlayers.vehicles.landing");

    private static int lastReasonTick = -1000;
    private static string lastReason = "";

    internal static void Install()
    {
        var type = AccessTools.TypeByName("Vehicles.LandingTargeter");
        if (type == null) return;
        var mouse = AccessTools.Method(type, "CurrentTargetUnderMouse", Type.EmptyTypes);
        if (mouse == null) return;
        Id.Patch(mouse, postfix: new HarmonyMethod(typeof(LandingSnapPatches), nameof(SnapDown)));
        Patched = true;
    }

    /// <summary>True once the ghost-snapping postfix is attached; asserted by the V1 scenario.</summary>
    internal static bool Patched { get; private set; }

    /// <summary>
    /// The topmost floor in this column at or below <paramref name="cell"/>, in storage coordinates.
    /// Returns the cell itself when it already stands on a floor, so a legal click is never moved.
    /// </summary>
    internal static IntVec3 Snap(LayerMapComponent owner, IntVec3 cell)
    {
        if (owner.Layout is not { LayerCount: > 1 } layout) return cell;
        if (!owner.TryDecode(cell, out var at)) return cell;
        var structures = owner.Structure;
        if (structures.HasSurface(cell)) return cell;
        for (int layer = at.LayerId - 1; layer >= 0; layer--)
        {
            var candidate = owner.ToStorage(new Layout.LayerCell(layer, at.X, at.Z));
            if (structures.HasSurface(candidate)) return candidate;
        }
        return cell;
    }

    /// <summary>
    /// A landing cell whose own column continues upward with a floor has a ceiling directly above it:
    /// the vehicle would have to come down through that deck. Say so, because the generic refusal
    /// ("this cell has no floor") is the opposite of what the player is looking at.
    /// </summary>
    internal static bool BlockedByFloorAbove(LayerMapComponent owner, IntVec3 cell)
    {
        if (owner.Layout is not { LayerCount: > 1 } layout) return false;
        if (!owner.TryDecode(cell, out var at)) return false;
        if (!owner.Structure.HasSurface(cell)) return false;
        for (int layer = at.LayerId + 1; layer < layout.LayerCount; layer++)
        {
            var above = owner.ToStorage(new Layout.LayerCell(layer, at.X, at.Z));
            if (owner.Structure.HasSurface(above)) return true;
        }
        return false;
    }

    private static void SnapDown(ref LocalTargetInfo __result)
    {
        if (!__result.IsValid) return;
        if (LayerMapComponent.For(Find.CurrentMap) is not { } owner) return;
        var snapped = Snap(owner, __result.Cell);
        if (snapped != __result.Cell) __result = new LocalTargetInfo(snapped);
    }

    /// <summary>
    /// Say why a landing was refused, once per few seconds. Called from the targeter's own validator,
    /// which is the only place that already knows the concrete reason - the ghost itself only knows
    /// red or green.
    /// </summary>
    internal static void Report(string reason, IntVec3 cell)
    {
        if (reason.NullOrEmpty() || reason == "RL_VehicleLandingUnavailable" || reason == "RL_VehicleNoSurface") return;
        if (reason == lastReason && GenTicks.TicksGame - lastReasonTick < 180) return;
        lastReason = reason;
        lastReasonTick = GenTicks.TicksGame;
        Messages.Message(reason.Translate(), MessageTypeDefOf.RejectInput, false);
    }
}
