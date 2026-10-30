using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Keeps SOS2 from rewriting the terrain RimLayers is about to replace.
/// </summary>
/// <remarks>
/// SOS2's <c>RecreateShipTile</c> postfix hangs off <c>TerrainGrid.DoTerrainChangedEffects</c> and, for
/// any cell listed in <c>MapShipCells</c>, writes the hull's own floor back through
/// <c>CompShipCachePart.SetShipTerrain</c>. That floor (<c>FakeFloorInsideShip</c>) is
/// <c>layerable=true</c>, so the write also refills the cell's under layer.
/// <para>
/// That is correct for SOS2's own bookkeeping and wrong for anyone replacing the cell with a
/// foundation: <c>TerrainGrid.SetFoundation</c> refuses a cell that still has an under terrain, so the
/// deck RimLayers is laying is rejected, the under terrain reappears in between two of our calls, and
/// the only way to observe it is the engine's own error log
/// ("Tried to set foundation at ... but there is an under terrain there").
/// </para>
/// <para>
/// The flag below is the narrow fix: while RimLayers owns the write, SOS2's restore is skipped. It is
/// set for the duration of one terrain write, not for a tick, so SOS2's normal repairs are untouched.
/// </para>
/// <para>
/// Skipping only SOS2's own restore is not enough once other mods are loaded. <c>RemoveFoundation</c>
/// and <c>SetTerrain</c> end in <c>DoTerrainChangedEffects</c>, which is a public patch point: Vehicle
/// Framework's <c>Patch_VehiclePathing.SetTerrainAndUpdateVehiclePathCosts</c> hangs off it and
/// dereferences per-map vehicle pathing state that a ship move has already torn down
/// (<c>NullReferenceException</c> inside <c>MoveShip</c>). That call site is a separate problem with a
/// separate owner - the ship move - and is not what this flag is for.
/// </para>
/// </remarks>
internal static class Sos2TerrainPatches
{
    /// <summary>True while RimLayers is writing terrain that SOS2 must not restore underneath.</summary>
    internal static bool Suppress { get; private set; }

    /// <summary>
    /// True while RimLayers is writing terrain whose change effects must not fire at all.
    /// </summary>
    /// <remarks>
    /// <see cref="Suppress"/> only silences SOS2's restore. That is the right scope for anything that
    /// still wants vanilla's bookkeeping - blueprint removal, floor designations, region and district
    /// invalidation - because <c>SetFoundation</c> depends on it. This wider flag exists for the one
    /// call site that leaves the map: tearing a deck off a hull that is departing, where the map is
    /// about to be disposed and the only thing the effect chain can still do is crash. Vehicle
    /// Framework's <c>Patch_VehiclePathing</c> is the current example - it dereferences per-map vehicle
    /// pathing state and throws on a map that never had a vehicle path grid.
    /// </remarks>
    internal static bool SuppressEffects { get; private set; }

    internal static IDisposable Suppressed() => new Scope(restore: false);

    /// <summary>Skips the whole <c>DoTerrainChangedEffects</c> body for the duration of the scope.</summary>
    internal static IDisposable SuppressedEffects() => new Scope(restore: true);

    private sealed class Scope : IDisposable
    {
        private readonly bool previous;
        private readonly bool previousEffects;
        private readonly bool restoreEffects;

        internal Scope(bool restore)
        {
            previous = Suppress;
            previousEffects = SuppressEffects;
            restoreEffects = restore;
            Suppress = true;
            if (restoreEffects) SuppressEffects = true;
        }

        public void Dispose()
        {
            Suppress = previous;
            if (restoreEffects) SuppressEffects = previousEffects;
        }
    }

    /// <summary>
    /// Skips SOS2's ship-tile restore for the duration of a suppression scope.
    /// </summary>
    /// <remarks>
    /// The restore is a postfix on <c>TerrainGrid.DoTerrainChangedEffects</c>. SOS2 declares it in
    /// the <c>SaveOurShip2</c> assembly but it is a Harmony patch class, not a member of anything, so
    /// it is resolved by type name and then by its <c>Postfix</c> method - asking
    /// <c>AccessTools.Method(typeof(ShipInteriorMod2), ...)</c> would return null and leave the guard
    /// silently inert.
    /// <para>
    /// This used to skip <c>DoTerrainChangedEffects</c> itself. That is too broad: the native body also
    /// destroys blueprints whose terrain no longer supports them, drops floor designations, and
    /// notifies the region and district caches, and a fixture that lays a deck over a blueprint then
    /// finds the blueprint still there while the district map is stale. Suppressing one postfix keeps
    /// vanilla's own bookkeeping intact.
    /// </para>
    /// </remarks>
    [HarmonyPatch]
    private static class RecreateShipTilePatch
    {
        private static MethodBase? TargetMethod()
        {
            Type? patchClass = AccessTools.TypeByName("SaveOurShip2.RecreateShipTile");
            return patchClass == null ? null : AccessTools.Method(patchClass, "Postfix");
        }

        private static bool Prefix() => !Suppress;
    }

    /// <summary>
    /// Skips the entire <c>DoTerrainChangedEffects</c> body, including every patch other mods attach to
    /// it, for the duration of <see cref="SuppressedEffects"/>.
    /// </summary>
    /// <remarks>
    /// <c>Priority.First</c> is what makes this work: Harmony runs prefixes in priority order and a
    /// prefix that returns false short-circuits the rest, so a default-priority third-party prefix never
    /// gets the chance to throw. The flag is only open where the caller has already decided the map is
    /// being abandoned, so skipping vanilla's bookkeeping there costs nothing.
    /// </remarks>
    [HarmonyPatch]
    [HarmonyPriority(Priority.First)]
    private static class SkipTerrainChangedEffectsPatch
    {
        private static MethodBase? TargetMethod() =>
            AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects");

        private static bool Prefix() => !SuppressEffects;
    }
}
