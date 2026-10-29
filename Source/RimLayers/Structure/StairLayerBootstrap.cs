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
        try
        {
            int elevation=LayerMapComponent.For(map) is {} owner&&owner.TryDecode(position,out var here)?owner.Elevation(here.LayerId):0;
            LiveMapExpansion.EnsureElevation(map,elevation+ConnectorDefs.Direction(definition));return true;
        }
        catch(Exception e)
        {
            Log.Error($"[RimLayers] Stair-triggered map expansion failed on map {map.uniqueID}: {e}");
            if(report)
                RejectionReporter.Reject(RejectionAction.Stairs,e,true,"RL_StairExpansionFailed".Translate(),
                    definition.defName,definition.LabelCap,map,position);
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
            var trigger=stairs.FirstOrDefault(s=>!s.UpperCell.IsValid&&Validate(component.map,s.Position,s.Rotation,s.def).Accepted);
            if(trigger!=null&&!Prepare(component.map,trigger.Position,trigger.Rotation,trigger.def))return;
        }
        foreach(var stair in stairs)stair.InitializeConnection(createLanding:true);
    }
}
