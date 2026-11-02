using HarmonyLib;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Keeps vanilla's power connector search from dereferencing a wall attachment that has no wall.
/// </summary>
/// <remarks>
/// <c>PowerConnectionMaker.TryConnectToAnyPowerNet</c> reads
/// <c>GenConstruct.GetWallAttachedTo(pc.parent.Position, pc.parent.Rotation, pc.parent.Map).Position</c>
/// for an attachment, and <c>GetWallAttachedTo</c> returns <c>null</c> whenever the cell one step along
/// the rotation holds no wall that supports attachments. Vanilla never has to think about that because a
/// wall attachment cannot exist without its wall; a hull that has been moved can, and this mod moves
/// hulls.
/// <para>
/// The failure is not local to the frame it happens on. <c>UpdatePowerNetsAndConnections_First</c> runs
/// the connector loop before <c>delayedActions.RemoveRange(0, count)</c>, so a throw inside that loop
/// leaves every pending action queued and re-throws on every later power update - a save that is stuck
/// in that state never ticks its power grid again. Skipping the one connector is the same outcome
/// vanilla reaches when a connector has nothing to connect to.
/// </para>
/// <para>
/// The relocation itself is also fixed: a thing's <c>Rotation</c> now turns with the hull, so a lamp
/// faces the wall it actually ended up next to. This guard covers the cases the relocation cannot see -
/// a player removing a wall, another mod despawning one, a hull whose parts were never consistent.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(PowerConnectionMaker), nameof(PowerConnectionMaker.TryConnectToAnyPowerNet))]
internal static class Sos2OrphanAttachmentPatch
{
    // The vanilla prefix already reads pc.parent before we get here, and a despawned parent is the one
    // case that must not be touched at all: it is not ours to reconnect.
    private static bool Prefix(CompPower pc)
    {
        if (pc?.parent == null || !pc.parent.Spawned) return true;
        if (pc.parent.def?.building == null || !pc.parent.def.building.isAttachment) return true;
        if (GenConstruct.GetWallAttachedTo(pc.parent.Position, pc.parent.Rotation, pc.parent.Map) != null)
            return true;
        // Nothing to attach to. Vanilla's own "no transmitter found" branch leaves connectParent null,
        // which is the state this connector should be in.
        pc.connectParent = null;
        return false;
    }
}
