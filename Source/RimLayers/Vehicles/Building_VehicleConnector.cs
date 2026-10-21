using RimLayers.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Vehicles;

public sealed class Building_VehicleConnector:Building
{
    public bool IsLift=>def.GetModExtension<VehicleConnectorProperties>()?.lift==true;
    public bool Powered=>!IsLift||this.TryGetComp<CompPowerTrader>()?.PowerOn==true;
    internal VehicleTraffic Traffic=>Map.GetComponent<VehicleTraffic>();
    public override void SpawnSetup(Map map,bool respawningAfterLoad)
    {base.SpawnSetup(map,respawningAfterLoad);map.GetComponent<VehicleTraffic>().Register(this);VehicleFacilitySpace.InstallDeck(this);}
    public override void DeSpawn(DestroyMode mode=DestroyMode.Vanish)
    {
        var oldMap=Map;var cells=Spawned&&IsLift?this.OccupiedRect().ToArray():Array.Empty<IntVec3>();
        if(Spawned)Traffic.Remove(this);base.DeSpawn(mode);
        if(oldMap!=null)VehicleFacilitySpace.RemoveDeck(oldMap,cells);
    }
    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach(var gizmo in base.GetGizmos())yield return gizmo;
        if(!Spawned||!IsLift||Faction!=Faction.OfPlayer)yield break;
        yield return new Command_Action
        {
            defaultLabel="RL_VehicleRecover".Translate(),defaultDesc="RL_VehicleRecoverHelp".Translate(),
            icon=TexCommand.Install,action=()=>
            {if(!Traffic.RecoverAt(this))Messages.Message("RL_VehicleRecoverBlocked".Translate(),this,MessageTypeDefOf.RejectInput,false);}
        };
        if(Traffic.ShaftTrips(this).Any())yield return new Command_Action
        {
            defaultLabel="RL_VehicleCancel".Translate(),defaultDesc="RL_VehicleCancelHelp".Translate(),icon=TexCommand.ClearPrioritizedWork,
            action=()=>Find.WindowStack.Add(new FloatMenu(Traffic.ShaftTrips(this).Select(t=>new FloatMenuOption(t.Vehicle.LabelShortCap,()=>Traffic.CancelOrder(t.Vehicle))).ToList()))
        };
    }
    public override string GetInspectString()
    {
        var text=base.GetInspectString()+"\n"+(IsLift?(Powered?"RL_VehicleLiftPowered":"RL_VehicleLiftPaused"):"RL_VehicleRampWidth").Translate();
        if(Spawned&&IsLift&&Traffic.StationStatus(this) is {Length:>0} status)text+="\n"+status;
        return text.Trim();
    }
}

public sealed class VehicleConnectorProperties:DefModExtension {public bool lift;}

public sealed class PlaceWorker_VehicleConnector:PlaceWorker
{
    public override bool IsBuildDesignatorVisible(BuildableDef def)=>VehicleCompatibility.Adapter!=null;
    public override AcceptanceReport AllowsPlacing(BuildableDef def,IntVec3 loc,Rot4 rot,Map map,Thing? thingToIgnore=null,Thing? thing=null)
    {
        if(VehicleCompatibility.Adapter==null)return "RL_VehicleUnavailable".Translate();
        if(LayerMapComponent.For(map) is not {} owner)return "RL_RequiresLayerMap".Translate();
        return owner.Structure.Validate(def,loc,rot,planning:true);
    }
    public override void DrawGhost(ThingDef def,IntVec3 center,Rot4 rot,Color color,Thing? thing=null)
    {
        var owner=LayerMapComponent.For(Find.CurrentMap);
        if(owner==null||!owner.TryDecode(center,out var at))return;
        var footprint=GenAdj.OccupiedRect(center,rot,def.size).ToList();
        GenDraw.DrawFieldEdges(footprint,color);
        var rect=GenAdj.OccupiedRect(center,rot,def.size);
        var blocked=new List<IntVec3>();
        if(def.GetModExtension<VehicleConnectorProperties>()?.lift==true)
        {
            foreach(var cell in rect)
            {
                if(owner.Elevation(at.LayerId)>0&&(owner.map.terrainGrid.TerrainAt(cell)!=RimLayersDefOf.RL_OpenAir||
                    owner.Adjacent(cell,-1) is var below&&below.IsValid&&owner.map.roofGrid.RoofAt(below)!=null)||
                    cell.GetThingList(owner.map).Any(t=>t is Pawn||t.def.passability==Traversability.Impassable))blocked.Add(cell);
            }
            var supports=new[]{new IntVec3(rect.minX-1,0,rect.minZ),new IntVec3(rect.maxX+1,0,rect.minZ),
                new IntVec3(rect.minX-1,0,rect.maxZ),new IntVec3(rect.maxX+1,0,rect.maxZ)};
            foreach(var support in supports)GenDraw.DrawFieldEdges(new(){support},VehicleFacilitySpace.NativeSurface(owner,support)?Color.green:Color.red);
        }
        else
        {
            var entrances=rect.Where(c=>!rect.Contains(c-rot.FacingCell)).Select(c=>c-rot.FacingCell).ToList();
            var exits=rect.Where(c=>!rect.Contains(c+rot.FacingCell)).Select(c=>c+rot.FacingCell).ToList();
            GenDraw.DrawFieldEdges(entrances,Color.green);
            foreach(var cell in exits)
            {
                var upper=owner.Adjacent(cell,1);
                GenDraw.DrawFieldEdges(new(){cell},upper.IsValid&&owner.Structure.HasSurface(upper)?Color.cyan:Color.red);
            }
            foreach(var cell in rect)
            {
                var upper=owner.Adjacent(cell,1);
                if(!upper.IsValid||owner.map.roofGrid.RoofAt(cell)!=null||owner.Structure.HasSurface(upper)||
                    upper.GetEdifice(owner.map) is {def.passability:Traversability.Impassable})blocked.Add(cell);
            }
        }
        if(blocked.Count>0)GenDraw.DrawFieldEdges(blocked,Color.red);
    }
}
