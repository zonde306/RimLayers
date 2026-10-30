using RimLayers.Core;
using RimLayers.Diagnostics;
using RimLayers.Generation;
using RimWorld;
using Verse;

namespace RimLayers.Structure;

/// <summary>Finished stairs opt a supported, previously single-floor map into layers.</summary>
internal static class StairLayerBootstrap
{
    internal static AcceptanceReport Validate(Map map,IntVec3 position,Rot4 rotation,BuildableDef? definition=null)
    {
        definition??=RimLayersDefOf.RL_Stairs;
        int source=LayerMapComponent.For(map) is {} existing&&existing.TryDecode(position,out var here)?existing.Elevation(here.LayerId):0;
        var quota=LayerExpansionQuota.For(map,source+ConnectorDefs.Direction(definition));
        if(!quota.Accepted)return quota;
        if(LayerMapComponent.For(map) is {} owner)
            return owner.Structure.Validate(definition,position,rotation);
        if(ConnectorDefs.IsDown(definition))return RimLayers.Underground.UndergroundService.TerrainAllows(map,position);
        if(!LiveMapExpansion.CanExpand(map))return "RL_StairMapUnsupported".Translate();
        foreach(var cell in GenAdj.OccupiedRect(position,rotation,RimLayersDefOf.RL_Stairs.Size))
        {
            if(!cell.InBounds(map))return "RL_NoSurface".Translate();
            if(!StructuralSupport.ConnectorRoof(map.roofGrid.RoofAt(cell)))return "RL_NeedsRoof".Translate();
        }
        return true;
    }

    /// <summary>
    /// True when this stair already failed to expand the map. Nothing retries it automatically: a failed
    /// expansion rolls the entire map back, so repeating it every tick is a freeze, not a repair. The
    /// player retries from the stair's own button once the cause is gone.
    /// </summary>
    internal static bool IsBlocked(Map? map,IntVec3 position)=>position.IsValid&&
        LayerMapComponent.Raw(map) is {} owner&&owner.BlockedStairs.Contains(position);

    /// <summary>Forgets a recorded failure so the next completion attempt runs the real expansion.</summary>
    internal static void ClearBlocked(Map? map,IntVec3 position)
    {
        if(!position.IsValid||LayerMapComponent.Raw(map) is not {} owner)return;
        owner.BlockedStairs.Remove(position);
    }

    // Frame completion calls this BEFORE native material destruction and Frame replacement.
    // report: the failure was caused by a player action, so explain it instead of failing silently.
    internal static bool Prepare(Map map,IntVec3 position,Rot4 rotation,BuildableDef? definition=null,bool report=false)
    {
        definition??=RimLayersDefOf.RL_Stairs;
        var check=Validate(map,position,rotation,definition);
        if(!check.Accepted)
        {
            if(report)
                RejectionReporter.Reject(RejectionAction.Stairs,check.Reason,true,definition.defName,definition.LabelCap,
                    map,position,RejectionReporter.ElevationAt(map,position));
            return false;
        }
        // A recorded failure is never retried implicitly: every attempt resizes the whole map and then
        // rolls it back. Only the player's own retry button clears the record first. Refusing silently
        // here made the failure look like nothing happened at all, so it explains itself like any other.
        if(IsBlocked(map,position))
        {
            // The message is unconditional: the window can be suppressed by its own cooldown, and a
            // refusal that says nothing at all is the bug this branch was added to explain.
            if(report)
                RejectionReporter.Reject(RejectionAction.Stairs,"RL_StairExpansionBlocked".Translate(),true,
                    definition.defName,definition.LabelCap,map,position,RejectionReporter.ElevationAt(map,position));
            Messages.Message("RL_StairExpansionBlocked".Translate(),MessageTypeDefOf.RejectInput,false);
            return false;
        }
        try
        {
            int elevation=LayerMapComponent.For(map) is {} owner&&owner.TryDecode(position,out var here)?owner.Elevation(here.LayerId):0;
            LiveMapExpansion.EnsureElevation(map,elevation+ConnectorDefs.Direction(definition));return true;
        }
        catch(Exception e)
        {
            // Remember the failure before reporting it: without this the work giver hands the same
            // frame back to a colonist the moment the job ends, and every attempt re-runs the whole
            // expansion and its rollback.
            LayerMapComponent.Raw(map)?.BlockedStairs.Add(position);
            Log.Error($"[RimLayers] Stair-triggered map expansion failed on map {map.uniqueID}: {e}");
            if(report)
                RejectionReporter.Reject(RejectionAction.Stairs,e,true,"RL_StairExpansionFailed".Translate(),
                    definition.defName,definition.LabelCap,map,position);
            // Unconditional, like every other refusal in the mod: the window is the extra help, not the
            // only feedback. Cooldown can swallow the window, and silence here reads as a dead click.
            Messages.Message("RL_StairExpansionFailed".Translate(),MessageTypeDefOf.RejectInput,false);
            return false;
        }
    }

    // Direct spawning, installation and loading do not all pass through Frame completion.
    // Process after SpawnSetup/native loading has finished, never resize half-initialized grids.
    internal static void Process(LayerMapComponent component)
    {
        component.StairBootstrapPending=false;
        var stairs=component.map.listerThings.AllThings.OfType<Building_Stairs>().Where(s=>s.ConnectsAdjacent).ToArray();
        {
            // Never retry a stair that already failed: one attempt per stair until the player asks again.
            var trigger=stairs.FirstOrDefault(s=>!s.UpperCell.IsValid&&!component.BlockedStairs.Contains(s.Position)
                &&Validate(component.map,s.Position,s.Rotation,s.def).Accepted);
            // This is the path a finished stair takes on a map that already has layers, so it is a real
            // player action and has to explain itself: the frame that follows never reaches Prepare
            // again, because CanConstruct refuses it from here on.
            if(trigger!=null&&!Prepare(component.map,trigger.Position,trigger.Rotation,trigger.def,report:true))return;
        }
        foreach(var stair in stairs)stair.InitializeConnection(createLanding:true);
    }
}
