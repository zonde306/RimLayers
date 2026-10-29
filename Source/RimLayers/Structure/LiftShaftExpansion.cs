using RimLayers.Core;
using RimLayers.Diagnostics;
using RimLayers.Generation;
using RimLayers.Underground;
using RimWorld;
using Verse;

namespace RimLayers.Structure;

// Only construction intent is owned here. Blueprints, frames, materials and mining jobs stay native.
internal sealed class LiftExtension:IExposable
{
    public IntVec3 WorkOffset=IntVec3.West;
    public int Stage=1; // 1: clear real rock; 2: native station blueprint/frame placed.
    public void ExposeData(){Scribe_Values.Look(ref Stage,"stage",1);Scribe_Values.Look(ref WorkOffset,"workOffset",IntVec3.West);}
}

public sealed partial class Building_Elevator
{
    private LiftExtension? shaftUp,shaftDown;
    private LiftExtension? Extension(int direction)=>direction>0?shaftUp:shaftDown;
    private void SetExtension(int direction,LiftExtension? value)
    {if(direction>0)shaftUp=value;else shaftDown=value;Map?.GetComponent<LayerMapComponent>()?.Connectors.Invalidate();}
    private void ExposeExtensions()
    {Scribe_Deep.Look(ref shaftUp,"shaftUp");Scribe_Deep.Look(ref shaftDown,"shaftDown");}
    internal bool Expanding(int direction)=>Extension(direction)!=null;
    internal IntVec3 ExtensionExit(int direction)=>LayerMapComponent.For(Map)?.Adjacent(Position,direction)??IntVec3.Invalid;
    private IntVec3 WorkingCell(int direction)=>ExtensionExit(direction) is var exit&&exit.IsValid?exit+(Extension(direction)?.WorkOffset??IntVec3.West):IntVec3.Invalid;
    private IntVec3 PickWorkOffset(int direction)
    {
        var o=LayerMapComponent.For(Map);var exit=ExtensionExit(direction);
        foreach(var offset in GenAdj.CardinalDirections)
        {
            var source=Position+offset;
            if(!source.InBounds(Map)||o!=null&&(!o.TryDecode(source,out var local)||!o.TryDecode(Position,out var here)||local.LayerId!=here.LayerId))continue;
            if(direction>0&&!StructuralSupport.ConnectorRoof(Map.roofGrid.RoofAt(source)))continue;
            if(!exit.IsValid)return offset;
            var work=exit+offset;
            if(!o!.TryDecode(work,out var target)||!o.TryDecode(exit,out var destination)||target.LayerId!=destination.LayerId)continue;
            if(direction>0&&!o.Structure.HasRequiredRoof(work))continue;
            if(o.Elevation(target.LayerId)<0&&!UndergroundService.DryLand(Map.terrainGrid.TerrainAt(work)))continue;
            if(work.GetEdifice(Map) is {} building&&building is not Mineable)continue;
            if(work.GetThingList(Map).Any(t=>t is Blueprint||t is Frame))continue;
            return offset;
        }
        return IntVec3.Invalid;
    }
    private static bool StationAt(Thing? thing)=>thing is Building_Elevator {ConnectsAdjacent:false};
    private bool StationPlan(Thing thing)=>thing.Faction==Faction&&(thing is Blueprint||thing is Frame)&&thing.def.entityDefToBuild==def;

    // Used both by the button and while clearing rock. Changing terrain cannot bypass dig restrictions.
    internal AcceptanceReport ExtensionConditions(int direction)
    {
        if(!Spawned||ConnectsAdjacent||direction is not (-1 or 1))return "RL_RequiresLayerMap".Translate();
        var o=LayerMapComponent.For(Map);
        int elevation=o!=null&&o.TryDecode(Position,out var at)?o.Elevation(at.LayerId):0;
        var quota=LayerExpansionQuota.For(Map,elevation+direction);if(!quota.Accepted)return quota;
        if(direction<0&&elevation<=0)
        {
            var terrain=UndergroundService.TerrainAllows(Map,Position);if(!terrain.Accepted)return terrain;
        }
        else
        {
            if(!LiveMapExpansion.CanExpand(Map))return "RL_StairMapUnsupported".Translate();
            if(o!=null&&!o.Structure.HasSurface(Position))return "RL_NoSurface".Translate();
            var roofCell=direction>0?Position:ExtensionExit(direction);
            if(!roofCell.IsValid||!StructuralSupport.ConnectorRoof(Map.roofGrid.RoofAt(roofCell)))return "RL_NeedsRoof".Translate();
        }
        var exit=ExtensionExit(direction);
        if(exit.IsValid)
        {
            if(direction<0&&elevation<=0&&!UndergroundService.DryLand(Map.terrainGrid.UnderTerrainAt(exit)??Map.terrainGrid.TerrainAt(exit)))return "RL_DigBadExit".Translate();
            if(!o!.Structure.HasRequiredRoof(direction>0?exit:Position))return "RL_NeedsRoof".Translate();
        }
        return true;
    }
    internal AcceptanceReport CanExpandShaft(int direction)
    {
        var conditions=ExtensionConditions(direction);if(!conditions.Accepted)return conditions;
        var exit=ExtensionExit(direction);
        if(exit.IsValid)
        {
            if(StationAt(exit.GetEdifice(Map)))return "RL_ShaftAlreadyConnected".Translate();
            if(exit.GetEdifice(Map) is {} obstruction&&obstruction is not Mineable)return "RL_BlockedLanding".Translate();
            if(exit.GetThingList(Map).Any(t=>t is Blueprint||t is Frame))return "RL_BlockedLanding".Translate();
        }
        return PickWorkOffset(direction).IsValid?true:"RL_ShaftNeedsWorkCell".Translate();
    }
    internal bool StartExtension(int direction)
    {
        if(Expanding(direction))return false;
        int? sourceElevation=RejectionReporter.ElevationAt(Map,Position);
        var report=CanExpandShaft(direction);
        if(!report.Accepted)
        {
            RejectionReporter.Reject(RejectionAction.LiftShaft,report.Reason,true,def.defName,LabelCap,Map,Position,sourceElevation);
            Messages.Message(report.Reason,MessageTypeDefOf.RejectInput,false);
            return false;
        }
        try
        {
            var o=LayerMapComponent.For(Map);int elevation=o!=null&&o.TryDecode(Position,out var at)?o.Elevation(at.LayerId):0;
            LiveMapExpansion.EnsureElevation(Map,elevation+direction);
            // Recheck generated geology and support before opening any access.
            report=CanExpandShaft(direction);
            if(!report.Accepted)
            {
                RejectionReporter.Reject(RejectionAction.LiftShaft,report.Reason,true,def.defName,LabelCap,Map,Position,elevation);
                Messages.Message(report.Reason,MessageTypeDefOf.RejectInput,false);
                return false;
            }
            LayerMapComponent.For(Map)!.Structure.EnsureLanding(ExtensionExit(direction));
            SetExtension(direction,new(){WorkOffset=PickWorkOffset(direction)});
            LayerMapComponent.For(Map)!.Structure.EnsureLanding(WorkingCell(direction));
            TickExtensions();return true;
        }
        catch(Exception e)
        {
            Log.Error($"[RimLayers] Lift shaft expansion failed: {e}");
            RejectionReporter.Reject(RejectionAction.LiftShaft,e,true,"RL_StairExpansionFailed".Translate(),
                def.defName,LabelCap,Map,Position);
            Messages.Message("RL_StairExpansionFailed".Translate(),MessageTypeDefOf.RejectInput,false);
            return false;
        }
    }
    internal void CancelExtension(int direction)
    {
        var request=Extension(direction);var exit=ExtensionExit(direction);
        SetExtension(direction,null);
        if(request?.Stage==2&&exit.IsValid)
            foreach(var plan in exit.GetThingList(Map).Where(StationPlan).ToArray())plan.Destroy(DestroyMode.Cancel);
    }
    internal IntVec3 MiningExit
    {
        get
        {
            foreach(int direction in new[]{-1,1})
            {
                if(Extension(direction)?.Stage!=1||!ExtensionConditions(direction).Accepted)continue;
                foreach(var exit in new[]{ExtensionExit(direction),WorkingCell(direction)})
                    if(exit.IsValid&&exit.GetEdifice(Map) is Mineable)return exit;
            }
            return IntVec3.Invalid;
        }
    }
    internal Mineable? MiningRock=>Spawned&&MiningExit is var exit&&exit.IsValid?exit.GetEdifice(Map) as Mineable:null;
    internal IntVec3 ConstructionExit(int direction)
    {
        var exit=ExtensionExit(direction);
        return Extension(direction)?.Stage==2&&ExtensionConditions(direction).Accepted&&exit.IsValid&&
            !StationAt(exit.GetEdifice(Map))&&exit.GetThingList(Map).Any(StationPlan)?exit:IntVec3.Invalid;
    }
    internal void TickExtensions()
    {
        if(!Spawned||ConnectsAdjacent)return;
        foreach(int direction in new[]{-1,1})
        {
            var request=Extension(direction);if(request==null)continue;
            var exit=ExtensionExit(direction);if(!exit.IsValid)continue;
            if(StationAt(exit.GetEdifice(Map))){SetExtension(direction,null);continue;}
            if(request.Stage==2)
            {if(!exit.GetThingList(Map).Any(StationPlan))SetExtension(direction,null);continue;}
            if(!ExtensionConditions(direction).Accepted||exit.GetEdifice(Map)!=null)continue;
            var work=WorkingCell(direction);
            if(!work.IsValid||work.GetEdifice(Map)!=null)continue;
            var o=LayerMapComponent.For(Map)!;
            if(!o.Structure.HasRequiredRoof(work))continue;
            o.Structure.EnsureLanding(exit);o.Structure.EnsureLanding(work);
            Map.fogGrid.Unfog(work);Map.fogGrid.Unfog(exit);Map.fogGrid.FloodUnfogAdjacent(exit);
            if(!GenConstruct.CanPlaceBlueprintAt(def,exit,Rotation,Map).Accepted)continue;
            GenConstruct.PlaceBlueprintForBuild(def,exit,Map,Rotation,Faction,null);
            request.Stage=2;o.Connectors.Invalidate();
        }
    }
    private IEnumerable<Gizmo> ExpansionGizmos()
    {
        foreach(int direction in new[]{1,-1})
        {
            bool pending=Expanding(direction);
            var command=new Command_Action{
                defaultLabel=(pending?direction>0?"RL_CancelShaftUp":"RL_CancelShaftDown":direction>0?"RL_ExpandShaftUp":"RL_ExpandShaftDown").Translate(),
                defaultDesc="RL_ExpandShaftHelp".Translate(),icon=Graphic.MatSingle.mainTexture,
                action=()=>{if(pending)CancelExtension(direction);else StartExtension(direction);}};
            if(!pending&&CanExpandShaft(direction) is var report&&!report.Accepted)command.Disable(report.Reason);
            yield return command;
        }
    }
    private string ExtensionInspection()
    {
        string result="";
        foreach(int direction in new[]{1,-1})
        {
            var request=Extension(direction);if(request==null)continue;
            var conditions=ExtensionConditions(direction);
            result+="\n"+(direction>0?"RL_ShaftUpStatus":"RL_ShaftDownStatus").Translate(
                conditions.Accepted?(request.Stage==1?"RL_ShaftMining":"RL_ShaftBuilding").Translate().ToString():conditions.Reason);
        }
        return result;
    }
}
