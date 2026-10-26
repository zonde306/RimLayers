using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimLayers.Generation;

// Allocation/shrink/restore strategy adapted from MIT ProjectZ. See ThirdPartyNotices.md.
internal sealed class GenerationScope
{
    [ThreadStatic] internal static GenerationScope? Active;
    internal GenerationScope? Previous;
    internal required LayerLayout Layout;
    internal Map? Map;
    internal bool Shrunk;
    internal IntVec3 GroundSize => new(Layout.Width, 1, Layout.Height);
    internal IntVec3 FullSize => new(Layout.Width, 1, Layout.StorageHeight);
}

[HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
internal static class GenerateMapPatch
{
    static void Prefix(ref IntVec3 mapSize, MapParent parent, MapGeneratorDef mapGenerator,
        bool isPocketMap, bool stepDebugger, out GenerationScope? __state)
    {
        __state = null;
        bool incoming = RimLayers.Ships.ShipLayers.IsLayerShip(RimLayers.Ships.ShipLayers.Incoming) && RimLayers.Ships.ShipLayers.RequiredLayers(RimLayers.Ships.ShipLayers.Incoming!)>1;
        bool fixture=GenCommandLine.CommandLineArgPassed("rimlayerstest") || GenCommandLine.CommandLineArgPassed("rimlayersv1test") && V1Preallocated();
        if(!incoming&&!fixture)return;
        if (parent == null || isPocketMap || stepDebugger || mapGenerator == null || mapGenerator.isUnderground ||
            mapSize.x <= 0 || mapSize.z <= 0 || (!incoming && (parent.Faction != Faction.OfPlayer || parent.Tile.Layer.Def.isSpace))) return;
        var layout = new LayerLayout(mapSize.x, mapSize.z, incoming?RimLayers.Ships.ShipLayers.RequiredLayers(RimLayers.Ships.ShipLayers.Incoming!):LayerLayout.PrototypeLayers, LayerLayout.CalculateStride(mapSize.z));
        __state = new GenerationScope { Layout = layout, Previous = GenerationScope.Active };
        GenerationScope.Active = __state;
        mapSize = __state.FullSize;
    }

    private static bool V1Preallocated()
    {
        GenCommandLine.TryGetCommandLineArg("rimlayerscase",out string scenario);
        if(scenario=="vehicle-ground"&&GenCommandLine.CommandLineArgPassed("rimlayersvehicleflat"))return false;
        return !scenario.StartsWith("environment")&&scenario is not ("stairs-bootstrap" or "incremental" or "mountains" or "underground" or "shaft-extension" or "deep-resources" or "dubs" or "vefpipes");
    }

    static Exception? Finalizer(Exception? __exception, GenerationScope? __state)
    {
        if (__state != null)
        {
            if (__state.Shrunk && __state.Map != null) __state.Map.info.Size = __state.FullSize;
            GenerationScope.Active = __state.Previous;
        }
        return __exception;
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.ConstructComponents))]
internal static class ConstructComponentsPatch
{
    static void Postfix(Map __instance)
    {
        var scope = GenerationScope.Active;
        if (scope == null || scope.Map != null || MapGenerator.mapBeingGenerated != __instance) return;
        scope.Map = __instance;
        __instance.GetComponent<LayerMapComponent>().Configure(scope.Layout);
        EmptySpace.Fill(__instance, scope.Layout);
        __instance.pathing.RecalculateAllPerceivedPathCosts();
        __instance.info.Size = scope.GroundSize;
        scope.Shrunk = true;
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
internal static class FinalizeMapPatch
{
    [HarmonyPriority(Priority.First)]
    static void Prefix(Map __instance)
    {
        var scope = GenerationScope.Active;
        if (scope?.Map != __instance || !scope.Shrunk) return;
        __instance.info.Size = scope.FullSize;
        scope.Shrunk = false;
        __instance.cellsInRandomOrder = new MapCellsInRandomOrder(__instance);
        int repaired = GridCapacityRepair.Fix(__instance, scope.Layout.Width * scope.Layout.Height);
        RemapRiver(__instance, scope.Layout);
        EmptySpace.Fill(__instance, scope.Layout);
        __instance.GetComponent<LayerMapComponent>().Mountains.InitializeAddedLayers(1,new List<Thing>());
        // Same notification as a runtime expansion, so adapters that resize their own buffers see one
        // code path for "a map now stores more rows than a ground map".
        LiveMapExpansion.NotifyCommitted(__instance,scope.Layout);
        LayerExpansionQuota.Current?.Allocated(__instance);
        Log.Message($"[RimLayers] Generated map {__instance.uniqueID}: {scope.Layout.Width}x{scope.Layout.Height}, {scope.Layout.LayerCount} layers, stride {scope.Layout.Stride}, repaired grids {repaired}.");
    }

    internal static void RemapRiver(Map map, LayerLayout layout)
    {
        var flow = map.waterInfo?.riverFlowMap;
        if (flow == null || flow.Count == 0) return;
        int expected = checked(layout.Width * layout.Height * 2);
        if (flow.Count != expected)
            throw new InvalidOperationException($"[RimLayers] Unexpected river flow size {flow.Count}, expected {expected}.");
        float[] full = new float[checked(layout.Width * layout.StorageHeight * 2)];
        for (int x = 0; x < layout.Width; x++)
            for (int z = 0; z < layout.Height; z++)
            {
                int from = (x * layout.Height + z) * 2;
                int to = (x * layout.StorageHeight + z) * 2;
                full[to] = flow[from];
                full[to + 1] = flow[from + 1];
            }
        map.waterInfo!.riverFlowMap = new List<float>(full);
    }
}

internal static class EmptySpace
{
    internal static void Fill(Map map, LayerLayout layout,int startZ=-1)
    {
        var component = map.GetComponent<LayerMapComponent>();
        for (int z = startZ<0?layout.Height:startZ; z < layout.StorageHeight; z++)
            for (int x = 0; x < layout.Width; x++)
            {
                IntVec3 cell = new(x, 0, z);
                int index = map.cellIndices.CellToIndex(cell);
                // Initial generation fills all new bands; runtime callers pass the old storage end.
                map.terrainGrid.topGrid[index] = component.TryDecode(cell, out _) ?
                    RimLayersDefOf.RL_OpenAir : RimLayersDefOf.RL_Isolation;
                if (map.fogGrid.IsFogged(index)) map.fogGrid.Unfog(cell);
            }
    }
}

[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[] {
    typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?),
    typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
internal static class GenerationPathSizePatch
{
    private static readonly AccessTools.FieldRef<PathFinder, Map> MapRef = AccessTools.FieldRefAccess<PathFinder, Map>("map");
    static void Prefix(PathFinder __instance, out GenerationScope? __state)
    {
        __state = null;
        var scope = GenerationScope.Active;
        if (scope?.Map == MapRef(__instance) && scope.Shrunk && scope.Map.Size == scope.GroundSize)
        {
            __state = scope;
            scope.Map.info.Size = scope.FullSize;
        }
    }
    static Exception? Finalizer(Exception? __exception, GenerationScope? __state)
    {
        if (__state?.Map != null && __state.Shrunk) __state.Map.info.Size = __state.GroundSize;
        return __exception;
    }
}

[HarmonyPatch(typeof(Area), MethodType.Constructor, new[] { typeof(AreaManager) })]
internal static class GenerationAreaPatch
{
    private static readonly AccessTools.FieldRef<Area, BoolGrid> Grid = AccessTools.FieldRefAccess<Area, BoolGrid>("innerGrid");
    static void Postfix(Area __instance, AreaManager areaManager)
    {
        var scope = GenerationScope.Active;
        if (scope is { Shrunk: true, Map: not null } && scope.Map == areaManager?.map)
            Grid(__instance)?.ClearAndResizeTo(scope.Layout.Width, scope.Layout.StorageHeight);
    }
}
