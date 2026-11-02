using HarmonyLib;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Stops a relocated hull part from stripping its own terrain as it leaves.
/// </summary>
/// <remarks>
/// <c>CompShipCachePart.PostDeSpawn</c> walks every cell under a departing part, removes the top terrain
/// layer and spawns that terrain's <c>CostListAdjusted</c> as loose items. That is exactly right for a
/// hull being torn apart: the plating is gone, so its material should be on the floor. It is wrong for a
/// hull being rotated in place, where the player finds a ring of steel and plasteel around the empty
/// footprint - the deck did not leave, it moved.
/// <para>
/// Suppressing the terrain *effects* around the despawn is not enough, which is why this patch exists.
/// <c>RemoveTopLayer</c> is called directly by this method, not through <c>DoTerrainChangedEffects</c>,
/// so the mod's effect scope never sees it; the layer is still removed and the items still spawn.
/// </para>
/// <para>
/// Scoped to <see cref="Sos2Compatibility.Relocating"/>, the bracket a relocation already raises around
/// its own writes. Outside it - a part genuinely destroyed, a hull genuinely dismantled - the vanilla
/// behaviour is left alone, because there the leavings are the point.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2RelocatingPartPatch
{
    private static System.Reflection.MethodBase? TargetMethod()
    {
        System.Type? part = Sos2Compatibility.ShipCachePartType;
        return part == null ? null : AccessTools.Method(part, "PostDeSpawn", new[] { typeof(Map), typeof(DestroyMode) });
    }

    private static bool Prefix()
    {
        // True skips vanilla entirely: no layer removal, no resource spawn, no roof removal.
        return !Sos2Compatibility.Relocating;
    }
}
