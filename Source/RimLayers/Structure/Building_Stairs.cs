using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Generation;
using Verse;
using RimWorld;

namespace RimLayers.Structure;

public class Building_Stairs : Building
{
    public virtual int Direction=>ConnectorDefs.Direction(def);
    public bool ConnectsAdjacent=>Direction!=0;
    public virtual bool ExtendsUp=>Direction>0;
    public virtual IntVec3 UpperCell
    {
        get
        {
            var owner = LayerMapComponent.For(Map);
            return owner==null||!ConnectsAdjacent?IntVec3.Invalid:owner.Adjacent(Position,Direction);
        }
    }

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        var owner = LayerMapComponent.For(map);
        if (owner == null)
        {
            if(!ConnectsAdjacent){map.GetComponent<LayerMapComponent>().Connectors.Register(this);return;}
            map.GetComponent<LayerMapComponent>().StairBootstrapPending=true;
            return;
        }
        if(ConnectsAdjacent&&!UpperCell.IsValid)owner.StairBootstrapPending=true;
        InitializeConnection(createLanding:!respawningAfterLoad);
    }

    internal void InitializeConnection(bool createLanding)
    {
        var owner=LayerMapComponent.For(Map);
        if(owner==null)return;
        if(Direction<0&&!RimLayers.Underground.UndergroundService.TerrainAllows(Map,Position).Accepted)return;
        if (ConnectsAdjacent && createLanding && UpperCell.IsValid && owner.Structure.HasRequiredRoof(UpperCell))
        {
            owner.Structure.EnsureLanding(UpperCell);
            if(Direction<0&&UpperCell.GetEdifice(Map) is not Mineable){Map.fogGrid.Unfog(UpperCell);Map.fogGrid.FloodUnfogAdjacent(UpperCell);}
        }
        owner.Connectors.Register(this);
    }

    public override string GetInspectString()
    {
        var result=base.GetInspectString();
        if(Direction<0&&Spawned&&RimLayers.Underground.UndergroundService.TerrainAllows(Map,Position) is var report&&!report.Accepted)return result+"\n"+report.Reason;
        return result+(ConnectsAdjacent&&RimLayers.Mountains.VerticalMining.Ready(this)?"\n"+"RL_WaitingVerticalMine".Translate():"");
    }

    public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
    {
        Map?.GetComponent<LayerMapComponent>()?.Connectors.Remove(this);
        // Removing the stair removes the reason it was stuck: a rebuild must be allowed to try again.
        StairLayerBootstrap.ClearBlocked(Map, Position);
        // An independently supported landing is structural floor, not a disposable teleport marker.
        base.DeSpawn(mode);
    }
}

public sealed class PlaceWorker_LayerSupport : PlaceWorker
{
    public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot,
        Map map, Thing? thingToIgnore = null, Thing? thing = null)
    {
        var owner = LayerMapComponent.For(map);
        if(owner!=null)return owner.Structure.Validate(checkingDef, loc, rot, planning:true);
        if(checkingDef.defName=="RL_LiftVertical"&&LiveMapExpansion.CanExpand(map))return true;
        return ConnectorDefs.IsAdjacent(checkingDef)?StairLayerBootstrap.Validate(map,loc,rot,checkingDef):"RL_RequiresLayerMap".Translate();
    }
}
