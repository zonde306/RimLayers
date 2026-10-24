using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimLayers.Work;

public static class QueryGeometry
{
    public static Map? CurrentMap { get=>LogicalQueryContext.Map;set=>LogicalQueryContext.Map=value; }
    public static IntVec3 LogicalDifference(IntVec3 a, IntVec3 b)
    {
        var owner = LayerMapComponent.For(CurrentMap);
        if (owner != null && owner.TryDecode(a, out var left) && owner.TryDecode(b, out var right))
            return new IntVec3(left.X - right.X, 0, left.Z - right.Z);
        return a - b;
    }
    public static float WorkDistanceLimit() => LayerMapComponent.For(CurrentMap) != null ? float.PositiveInfinity : 99999f;
    public static float DistanceSquared(LayerMapComponent owner, IntVec3 a, IntVec3 b)
    {
        if (!owner.TryDecode(a, out var left) || !owner.TryDecode(b, out var right)) return float.PositiveInfinity;
        int dx = left.X - right.X, dz = left.Z - right.Z;
        return dx * dx + dz * dz;
    }
}

[HarmonyPatch]
internal static class WorkQueryScopePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var pair in new[] {
            (typeof(JobGiver_Work), "TryIssueJobPackage"),
            (typeof(WorkGiver_DoBill), "JobOnThing"),
            (typeof(RestUtility), "FindBedFor"),
            (typeof(JoyGiver_InteractBuilding), "FindBestGame"),
            (typeof(FoodUtility), "SpawnedFoodSearchInnerScan") })
            foreach (var method in pair.Item1.GetMethods(flags).Where(m => m.Name == pair.Item2))
                yield return method;
    }
    static void Prefix(object[] __args, out Map? __state)
    {
        __state = QueryGeometry.CurrentMap;
        Map? map = __args.OfType<Pawn>().FirstOrDefault(p => p.Spawned)?.Map;
        QueryGeometry.CurrentMap = LayerMapComponent.For(map) == null ? null : map;
    }
    static Exception? Finalizer(Exception? __exception, Map? __state)
    {
        QueryGeometry.CurrentMap = __state;
        return __exception;
    }
}

/// <summary>Only arithmetic in candidate scoring/ordering is projected. Targets stay real storage cells.</summary>
[HarmonyPatch]
internal static class CandidateDistancePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type root in new[] { typeof(JobGiver_Work), typeof(GenClosest), typeof(WorkGiver_DoBill), typeof(FoodUtility) })
        {
            foreach (Type type in new[] { root }.Concat(root.GetNestedTypes(flags)))
                foreach (MethodInfo method in type.GetMethods(flags))
                    if (method.Name.Contains("TryIssueJobPackage") || method.Name.Contains("ClosestThing_Global") ||
                        method.Name.Contains("TryFindBestIngredientsInSet_NoMixHelper") ||
                        method.Name.Contains("TryFindBestIngredientsInSet_AllowMix") ||
                        method.Name.Contains("SpawnedFoodSearchInnerScan"))
                        yield return method;
        }
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var subtract = AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) });
        var logical = AccessTools.Method(typeof(QueryGeometry), nameof(QueryGeometry.LogicalDifference));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(subtract)) instruction.operand = logical;
            if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value && value == 99999f)
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(QueryGeometry), nameof(QueryGeometry.WorkDistanceLimit)); }
            yield return instruction;
        }
    }
}

internal static class LayerCandidates
{
    internal static Thing? Closest(IntVec3 root, Map map, IEnumerable<Thing>? searchSet,
        PathEndMode mode, TraverseParms parms, float maxDistance, Predicate<Thing>? validator,
        Func<Thing,float>? priority, bool containers)
    {
        var owner = LayerMapComponent.For(map)!;
        Thing? best = null;
        float bestPriority = float.NegativeInfinity, bestCost = float.PositiveInfinity;
        var seen = new HashSet<Thing>();
        // A container contributes one entry per held thing, all with the same parent position, so the
        // same path was computed repeatedly. The leg cost depends only on the parent cell (fixed
        // start, fixed end, fixed parms), so one lookup per distinct position is equivalent.
        var legCost = new Dictionary<IntVec3, float>();
        float limit = maxDistance * maxDistance;
        float Cost(Thing parent)
        {
            IntVec3 at = parent.PositionHeld;
            if (legCost.TryGetValue(at, out float cached)) return cached;
            float cost;
            using (var path = map.pathFinder.FindPathNow(root, parent, parms, null, mode))
                cost = path.Found ? path.TotalCost : float.PositiveInfinity;
            legCost[at] = cost;
            return cost;
        }
        void Consider(Thing thing, Thing parent)
        {
            if (!seen.Add(thing) || parent.MapHeld != map ||
                QueryGeometry.DistanceSquared(owner, root, parent.PositionHeld) > limit ||
                validator != null && !validator(thing)) return;
            float score = priority?.Invoke(thing) ?? 0;
            if (float.IsNaN(score) || score < bestPriority) return;
            float cost = Cost(parent);
            if (!float.IsFinite(cost)) return;
            if (score > bestPriority || score == bestPriority && cost < bestCost)
            { best = thing; bestPriority = score; bestCost = cost; }
        }
        foreach (var item in searchSet ?? Array.Empty<Thing>())
        {
            if (item?.Spawned != true) continue;
            Consider(item, item);
            if (containers && item is IHaulSource source && (parms.pawn?.IsColonist != true || source.HaulSourceEnabled))
                foreach (Thing held in source.GetDirectlyHeldThings()) Consider(held, item);
        }
        return best;
    }
}

[HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable))]
internal static class ReachableCandidatePatch
{
    static bool Prefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams,
        float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet,
        int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool lookInHaulSources,
        ref Thing? __result)
    {
        var owner = LayerMapComponent.For(map);
        if (owner == null || !owner.Connectors.HasAny || thingReq.IsUndefined && customGlobalSearchSet == null || traversableRegionTypes != RegionType.Set_Passable ||
            searchRegionsMax >= 0 && !forceAllowGlobalSearch && QueryGeometry.CurrentMap != map) return true;
        __result = LayerCandidates.Closest(root, map, customGlobalSearchSet ?? map.listerThings.ThingsMatching(thingReq),
            peMode, traverseParams, maxDistance, validator, null, lookInHaulSources);
        return false;
    }
}

[HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThing_Global_Reachable))]
internal static class GlobalCandidatePatch
{
    static bool Prefix(IntVec3 center, Map map, IEnumerable<Thing> searchSet, PathEndMode peMode,
        TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator,
        Func<Thing,float> priorityGetter, bool canLookInHaulableSources, ref Thing? __result)
    {
        var owner = LayerMapComponent.For(map);
        if (owner == null || !owner.Connectors.HasAny) return true;
        __result = LayerCandidates.Closest(center, map, searchSet, peMode, traverseParams, maxDistance,
            validator, priorityGetter, canLookInHaulableSources);
        return false;
    }
}

/// <summary>Replace only collection. Native recipe/mixing/count selection remains the passed delegate.</summary>
[HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestIngredientsHelper")]
internal static class BillCandidatePatch
{
    static bool Prefix(Predicate<Thing> thingValidator, Predicate<List<Thing>> foundAllIngredientsAndChoose,
        List<IngredientCount> ingredients, Pawn pawn, Thing billGiver, List<ThingCount> chosen,
        float searchRadius, ref bool __result)
    {
        var owner = LayerMapComponent.For(pawn.Map);
        if (owner == null || !owner.Connectors.HasAny && !RimLayers.Cargo.CargoNetwork.For(pawn.Map).Hoists.Any() || billGiver is Pawn) return true;
        var cargoCostScope=RimLayers.Cargo.CargoBillCosts.Begin(pawn);
        Map? previous = QueryGeometry.CurrentMap;
        QueryGeometry.CurrentMap = pawn.Map;
        try
        {
            chosen.Clear();
            if (ingredients.Count == 0) { __result = true; return false; }
            var candidates = new List<Thing>();
            var seen = new HashSet<Thing>();
            if (billGiver is Building_WorkTableAutonomous automatic)
                foreach (Thing t in automatic.innerContainer) { candidates.Add(t); seen.Add(t); }
            if (foundAllIngredientsAndChoose(candidates)) { __result = true; return false; }
            void Add(Thing item, Thing parent, bool requireSpawned)
            {
                if (RimLayers.Cargo.CargoNetwork.OwnerOf(item) is {} hoist)
                {
                    var port=RimLayers.Cargo.CargoNetwork.For(pawn.Map).BestPort(hoist,pawn,pawn.Position,out _);
                    if(port==null)return;
                    parent=port;
                }
                if (!seen.Add(item) || requireSpawned && !item.Spawned || !thingValidator(item) ||
                    item.IsForbidden(pawn) || !pawn.CanReserve(item) ||
                    QueryGeometry.DistanceSquared(owner, parent.PositionHeld, billGiver.Position) >= searchRadius * searchRadius ||
                    !pawn.CanReach(parent, PathEndMode.ClosestTouch, Danger.Deadly)) return;
                candidates.Add(item);
            }
            foreach (IHaulSource source in pawn.Map.haulDestinationManager.AllHaulSourcesListForReading)
            {
                if (!source.HaulSourceEnabled || source is not Thing { Spawned: true } parent ||
                    parent.IsForbidden(pawn)) continue;
                var held = new List<Thing>();
                ThingOwnerUtility.GetAllThingsRecursively(source, held);
                foreach (Thing item in held) Add(item, parent, false);
            }
            foreach (Thing thing in pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                Add(thing, thing, true);
            __result = foundAllIngredientsAndChoose(candidates);
            return false;
        }
        finally { QueryGeometry.CurrentMap = previous; RimLayers.Cargo.CargoBillCosts.End(cargoCostScope); }
    }
}

[HarmonyPatch(typeof(Toils_JobTransforms), "IngredientPlaceCellsInOrder")]
internal static class BillPlacementLayerPatch
{
    static IEnumerable<IntVec3> Postfix(IEnumerable<IntVec3> __result, Thing destination)
    {
        Thing? thing = destination;
        var owner = LayerMapComponent.For(thing?.Map);
        if (owner == null || thing == null || !owner.TryDecode(thing.Position, out var table)) return __result;
        return __result.Where(cell => owner.TryDecode(cell, out var logical) && logical.LayerId == table.LayerId &&
            owner.Structure.HasSurface(cell));
    }
}
