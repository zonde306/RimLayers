using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Structure;

/// <summary>
/// The only way back from a failed layer expansion. A failure is recorded instead of retried, because
/// every attempt resizes the whole map and then rolls it back: repeating that automatically is a freeze,
/// not a repair. This button is the player's explicit "try again now that I fixed the cause".
/// </summary>
internal static class BlockedStairGizmos
{
    private static readonly Texture2D RetryIcon = ContentFinder<Texture2D>.Get("Things/Building/RimLayers/Stairs");

    internal static IEnumerable<Gizmo> For(Thing thing)
    {
        if (thing is not (Frame or Building_Stairs) || !thing.Spawned || thing.Faction != Faction.OfPlayer)
            return Enumerable.Empty<Gizmo>();
        if (!ConnectorDefs.IsAdjacent(thing.def.entityDefToBuild ?? thing.def)) return Enumerable.Empty<Gizmo>();
        if (!StairLayerBootstrap.IsBlocked(thing.Map, thing.Position)) return Enumerable.Empty<Gizmo>();
        return Buttons(thing);
    }

    private static IEnumerable<Gizmo> Buttons(Thing thing)
    {
        yield return new Command_Action
        {
            defaultLabel = "RL_StairRetry".Translate(),
            defaultDesc = "RL_StairRetryHelp".Translate(),
            icon = RetryIcon,
            action = () => Run(thing),
        };
    }

    private static void Run(Thing thing)
    {
        Map map = thing.Map;
        IntVec3 position = thing.Position;
        BuildableDef definition = thing.def.entityDefToBuild ?? thing.def;
        StairLayerBootstrap.ClearBlocked(map, position);
        // Prepare records the cell again and explains the refusal itself when it fails, so a failed
        // retry needs no extra message here.
        if (StairLayerBootstrap.Prepare(map, position, thing.Rotation, definition, report: true))
            Messages.Message("RL_StairRetryOk".Translate(), MessageTypeDefOf.PositiveEvent, false);
    }
}
