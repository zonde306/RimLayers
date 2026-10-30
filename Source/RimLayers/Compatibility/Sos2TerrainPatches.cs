using System;
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
/// </remarks>
internal static class Sos2TerrainPatches
{
    /// <summary>True while RimLayers is writing terrain that SOS2 must not restore underneath.</summary>
    internal static bool Suppress { get; private set; }

    internal static IDisposable Suppressed() => new Scope();

    private sealed class Scope : IDisposable
    {
        private readonly bool previous = Suppress;

        public Scope() => Suppress = true;

        public void Dispose() => Suppress = previous;
    }

    [HarmonyPatch]
    private static class RecreateShipTilePatch
    {
        private static System.Reflection.MethodBase? TargetMethod()
        {
            // RecreateShipTile is a top-level Harmony patch class in the SaveOurShip2 namespace, not a
            // member of ShipInteriorMod2: resolving it off ShipInteriorType would return null and the
            // guard would silently never apply.
            Type? patchClass = AccessTools.TypeByName("SaveOurShip2.RecreateShipTile");
            return patchClass == null ? null : AccessTools.Method(patchClass, "Postfix");
        }

        /// <summary>
        /// The prefix returns false to skip SOS2's restore. Everything else - the move flag, the
        /// MapShipCells test, the comp lookup - stays in SOS2's hands, because only the suppression
        /// check is RimLayers' business.
        /// </summary>
        private static bool Prefix() => !Suppress;
    }
}
