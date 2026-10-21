using System.Reflection;
using System.Reflection.Emit;
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
        if (owner == null || !owner.Connectors.HasAny) return true;
        if (slotGroup == null || !slotGroup.Settings.AllowedToAccept(t)) return false;
        IntVec3 start = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;
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
    [ThreadStatic] private static Pawn? carrierContext;
    static void Prefix(Pawn carrier, out Pawn? __state)
    {
        __state = carrierContext;
        carrierContext = carrier;
    }
    static Exception? Finalizer(Exception? __exception, Pawn? __state)
    {
        carrierContext = __state;
        return __exception;
    }
    public static int Distance(IntVec3 start, IntVec3 end)
    {
        Pawn? carrier = carrierContext;
        var owner = LayerMapComponent.For(carrier?.MapHeld);
        if (owner == null || !owner.Connectors.HasAny) return start.DistanceToSquared(end);
        using var path = owner.map.pathFinder.FindPathNow(start, end, TraverseParms.For(carrier), null, PathEndMode.ClosestTouch);
        if (!path.Found) return int.MaxValue;
        double cost = path.TotalCost;
        return (int)Math.Min(int.MaxValue - 1d, cost * cost);
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo original = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceToSquared));
        MethodInfo replacement = AccessTools.Method(typeof(StorageContainerDistancePatch), nameof(Distance));
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original)) { instruction.operand = replacement; count++; }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("StoreUtility container distance changed; expected exactly one scoring call.");
    }
}
