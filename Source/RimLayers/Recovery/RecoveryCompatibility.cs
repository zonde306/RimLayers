using System.Runtime.CompilerServices;
using RimLayers.Core;
using RimLayers.Ships;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Recovery;

/// <summary>Compatibility patches can lease an entity while their own movement transaction owns it.</summary>
public static class RecoveryCompatibility
{
    private sealed class Lease { public int Count, Until; }
    private static readonly ConditionalWeakTable<Thing,Lease> leases=new();
    public static IDisposable BeginHandling(Thing thing)
    {
        var lease=leases.GetOrCreateValue(thing);lease.Count++;
        return new Scope(()=>lease.Count--);
    }
    public static void DeferFor(Thing thing,int ticks=120)
    {var lease=leases.GetOrCreateValue(thing);lease.Until=Math.Max(lease.Until,(Find.TickManager?.TicksGame??0)+Math.Max(1,ticks));}
    public static bool IsClaimed(Thing thing)=>leases.TryGetValue(thing,out var lease)&&(lease.Count>0||lease.Until>(Find.TickManager?.TicksGame??0));
    internal static bool TransactionActive=>Generation.GenerationScope.Active!=null||MapGenerator.mapBeingGenerated!=null||
        ShipLayers.Capture!=null||ShipLayers.Placing!=null||SkyPayload.Current!=null||WorldComponent_GravshipController.CutsceneInProgress;
    internal static bool SupportedKind(Thing thing)=>thing is Pawn or Corpse||thing.def.category==ThingCategory.Item;
    internal static bool ValidFloorConstruction(Thing thing,LayerMapComponent owner,IntVec3 at)
    {
        if(thing is not (Blueprint or Frame))return false;
        if(thing.def.entityDefToBuild is ThingDef connector&&RimLayers.Vehicles.VehicleFacilitySpace.IsConnector(connector))
            return owner.Structure.Validate(connector,at,thing.Rotation,planning:true).Accepted;
        if(thing.def.entityDefToBuild is not TerrainDef floor)return false;
        if(floor!=RimLayersDefOf.RL_StructuralFloor&&floor.defName!="RL_ShipStructuralFloor")return false;
        // A planned slab is meant to occupy air until built. Validate its roof/ship rules,
        // not the walkable surface it will create; distant cells may await adjacent work.
        return owner.Structure.Validate(floor,at,thing.Rotation,planning:true).Accepted;
    }
    internal static bool Excluded(Thing thing,LayerMapComponent owner)
    {
        if(IsClaimed(thing)||thing.Destroyed||thing.BeingTransportedOnGravship||thing is Projectile or Mote or Skyfaller or PawnFlyer or ActiveTransporter)return true;
        if(RimLayersMod.Settings.RecoveryExcludedDefs.Contains(thing.def.defName))return true;
        if(thing is Pawn pawn&&(pawn.Flying||pawn.CurrentBed()!=null))return true;
        if(thing.Spawned&&owner.PendingCollapse.Contains(thing.Position)&&owner.StructuralCells.Contains(thing.Position))return true;
        return false;
    }
    private sealed class Scope:IDisposable
    {
        private Action? finish;
        internal Scope(Action finish)=>this.finish=finish;
        public void Dispose(){var action=finish;finish=null;action?.Invoke();}
    }
}
