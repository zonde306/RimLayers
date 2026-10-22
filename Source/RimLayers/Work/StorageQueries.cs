using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Work;

/// <summary>Native priority/acceptance/reservation rules stay in StoreUtility; compare travel costs within a priority.</summary>
[HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterStoreCellForWorker")]
internal static class StorageCellQueryPatch
{
    static bool Prefix(Thing t, Pawn carrier, Map map, Faction faction, ISlotGroup slotGroup,
        ref IntVec3 closestSlot, ref float closestDistSquared, ref StoragePriority foundPriority)
    {
        var owner = LayerMapComponent.For(map);
        var cargo=RimLayers.Cargo.CargoNetwork.OwnerOf(t);
        if (owner == null || !owner.Connectors.HasAny && cargo==null) return true;
        if (slotGroup == null || !slotGroup.Settings.AllowedToAccept(t)) return false;
        IntVec3 start = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;
        if(cargo!=null&&carrier!=null)
        {
            var port=RimLayers.Cargo.CargoNetwork.For(map).BestPort(cargo,carrier,carrier.Position,out _);
            if(port==null)return false;
            start=port.Position;
        }
        foreach (var cell in slotGroup.CellsList)
        {
            if (!owner.Structure.HasSurface(cell) || !StoreUtility.IsGoodStoreCell(cell, map, t, carrier, faction)) continue;
            float cost;
            if (carrier == null) cost = QueryGeometry.DistanceSquared(owner, start, cell);
            else
            {
                using var path = map.pathFinder.FindPathNow(start, cell, TraverseParms.For(carrier), null, PathEndMode.ClosestTouch);
                if (!path.Found) continue;
                cost = path.TotalCost;
            }
            if (cost > closestDistSquared) continue;
            closestSlot = cell;
            closestDistSquared = cost;
            foundPriority = slotGroup.Settings.Priority;
        }
        return false;
    }
}

[HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterNonSlotGroupStorageFor))]
internal static class StorageContainerDistancePatch
{
    static bool Prefix(Thing t,Pawn carrier,Map map,StoragePriority currentPriority,Faction faction,
        ref IHaulDestination? haulDestination,bool acceptSamePriority,bool requiresDestReservation,ref bool __result)
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null||carrier==null)return true;
        var cargo=RimLayers.Cargo.CargoNetwork.OwnerOf(t);
        if(!owner.Connectors.HasAny&&cargo==null&&!RimLayers.Cargo.CargoNetwork.For(map).Hoists.Any())return true;
        haulDestination=null;__result=false;
        IntVec3 start=t.SpawnedOrAnyParentSpawned?t.PositionHeld:carrier.PositionHeld;
        if(cargo!=null)
        {
            var port=RimLayers.Cargo.CargoNetwork.For(map).BestPort(cargo,carrier,carrier.Position,out _);
            if(port==null)return false;
            start=port.Position;
        }
        float bestCost=float.MaxValue;
        StoragePriority foundPriority=StoragePriority.Unstored;
        var destinations=map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
        for(int i=0;i<destinations.Count;i++)
        {
            var destination=destinations[i];
            if(destination is ISlotGroupParent||destination is Building_Grave&&!t.CanBeBuried()||!destination.HaulDestinationEnabled)continue;
            var priority=destination.GetStoreSettings().Priority;
            if(priority<foundPriority||(acceptSamePriority?priority<currentPriority:priority<=currentPriority))break;
            // Native DistanceToSquared was cheap and came before these checks. A full
            // cross-floor path must not run for every rejecting/forbidden/unreachable
            // container in every Haul validator after a floor loses its stockpile.
            if(!destination.Accepts(t))continue;
            var thing=destination as Thing;
            if(thing!=null&&(thing.Faction!=faction||thing.IsForbidden(carrier)))continue;
            if(thing!=null&&requiresDestReservation)
            {
                if(thing is IHaulEnroute enroute)
                {
                    if(!map.reservationManager.OnlyReservationsForJobDef(thing,JobDefOf.HaulToContainer)||enroute.GetSpaceRemainingWithEnroute(t.def)<=0)continue;
                }
                else if(!carrier.CanReserveNew(thing))continue;
            }
            LocalTargetInfo target=thing!=null?thing:destination.Position;
            var parms=TraverseParms.For(carrier);
            if(!map.reachability.CanReach(start,target,PathEndMode.ClosestTouch,parms))continue;
            float cost;
            var cargoDestination=map.thingGrid.ThingAt<RimLayers.Cargo.Building_CargoPort>(destination.Position);
            if(!owner.Connectors.HasAny&&cargo==null&&cargoDestination==null)cost=start.DistanceToSquared(destination.Position);
            else
            {
                using var path=map.pathFinder.FindPathNow(start,destination.Position,parms,null,PathEndMode.ClosestTouch);
                // Reachability above owns eligibility. Preserve the old score sentinel:
                // a synchronous score can be unavailable during same-tick map changes.
                if(!path.Found)cost=int.MaxValue;
                else
                {
                    double travel=path.TotalCost+(cargoDestination?.Master?.EstimatedWait(cargoDestination)??0);
                    cost=(int)Math.Min(int.MaxValue-1d,travel*travel);
                }
            }
            if(cost>bestCost)continue;
            bestCost=cost;foundPriority=priority;haulDestination=destination;
        }
        __result=haulDestination!=null;
        return false;
    }
}
