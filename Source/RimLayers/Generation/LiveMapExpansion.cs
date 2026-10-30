using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Generation;

internal sealed record ExpansionResult(IntVec3 Before,IntVec3 After,double Milliseconds,IReadOnlyList<string> Buffers,IReadOnlyDictionary<string,double>? Phases=null);

/// <summary>Append-only runtime conversion of one existing Map; never re-spawns or serializes its entities.</summary>
internal static class LiveMapExpansion
{
    private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    private sealed record Resume(Pawn Pawn,LocalTargetInfo Target,PathEndMode End);
    private sealed record Climate(IntVec3 Cell,float Temperature,float Vacuum);

    /// <summary>
    /// Raised exactly once, after a map's storage size grew and every swap was committed but before
    /// the original owners are released. Subscribers may assume the final layout and the final
    /// cellCount are already live (so per-cell buffers can be resized and networks rebuilt), and
    /// that the transaction can no longer roll back.
    /// </summary>
    internal static event Action<Map,LayerLayout>? Committed;

    /// <summary>Notifies subscribers; one failing adapter must not stop the others or the expansion.</summary>
    internal static void NotifyCommitted(Map map,LayerLayout layout)
    {
        var listeners=Committed;if(listeners==null)return;
        foreach(var listener in listeners.GetInvocationList())
        {
            try {((Action<Map,LayerLayout>)listener)(map,layout);}
            catch(Exception exception) {Log.Error($"[RimLayers] A layer-expansion subscriber failed and was skipped; the rest keeps working.\n{exception}");}
        }
    }

    internal static bool CanExpand(Map map)=>!map.Disposed&&!map.IsPocketMap&&map.generatorDef?.isUnderground!=true&&map.Size.y==1&&map.Size.x>0&&map.Size.z>0;

    // beforeCommit is an isolation-test fault/validation seam.
    internal static ExpansionResult Expand(Map map,Action? beforeCommit=null)=>EnsureLayerCount(map,LayerLayout.PrototypeLayers,beforeCommit);

    internal static ExpansionResult EnsureLayerCount(Map map,int requiredCount,Action? beforeCommit=null)
    {
        if(requiredCount<1||requiredCount>LayerLayout.MaxElevation+1)throw new ArgumentOutOfRangeException(nameof(requiredCount));
        if(requiredCount==1)return new(map.Size,map.Size,0,Array.Empty<string>());
        return EnsureElevation(map,requiredCount-1,beforeCommit);
    }

    internal static ExpansionResult EnsureElevation(Map map,int elevation,Action? beforeCommit=null)
    {
        var timer=Stopwatch.StartNew();var oldSize=map.Size;var phases=new Dictionary<string,double>();double last=0;
        void Lap(string name){double now=timer.Elapsed.TotalMilliseconds;phases[name]=now-last;last=now;}
        if(elevation<LayerLayout.MinElevation||elevation>LayerLayout.MaxElevation)throw new ArgumentOutOfRangeException(nameof(elevation));
        var previous=LayerMapComponent.For(map)?.Layout;
        if(elevation==0||previous?.TryGetLayerAtElevation(elevation,out _)==true)return new(oldSize,oldSize,0,Array.Empty<string>());
        var quota=LayerExpansionQuota.For(map,elevation);
        if(!quota.Accepted)throw new InvalidOperationException(quota.Reason);
        if(!CanExpand(map))
            throw new NotSupportedException("This map cannot acquire surface layers.");
        if(map.cellIndices.NumGridCells!=checked(oldSize.x*oldSize.z))throw new InvalidOperationException("Map index dimensions are inconsistent.");
        var owner=map.GetComponent<LayerMapComponent>();
        var levels=previous?.Elevations.ToList()??new List<int>{0};
        for(int e=elevation>0?levels.Max()+1:levels.Min()-1;elevation>0?e<=elevation:e>=elevation;e+=Math.Sign(elevation))levels.Add(e);
        var layout=new LayerLayout(oldSize.x,previous?.Height??oldSize.z,levels,previous?.Stride??LayerLayout.CalculateStride(oldSize.z));
        var full=new IntVec3(layout.Width,1,layout.StorageHeight);int oldCount=oldSize.x*oldSize.z,newCount=full.x*full.z;
        var changes=new ExpansionBuffers();
        var oldRoofCollapses=map.roofCollapseBuffer.CellsMarkedToCollapse.ToList();
        var environmentState=owner.Environment.Snapshot();
        var mountainState=owner.Mountains.Snapshot();var undergroundState=owner.Underground.Snapshot();var mountainThings=new List<Thing>();
        var oldFinder=map.pathFinder;var oldPathing=map.pathing;var oldDrawer=map.mapDrawer;
        PathFinder? newFinder=null;Pathing? newPathing=null;MapDrawer? newDrawer=null;
        bool applied=false,configured=false;
        var paths=map.mapPawns.AllPawnsSpawned.Where(p=>p.pather.Moving).Select(p=>new Resume(p,p.pather.Destination,
            (PathEndMode)AccessTools.Field(typeof(Pawn_PathFollower),"peMode").GetValue(p.pather)!)).ToList();
        var climates=map.regionGrid.AllRooms.Where(r=>r.CellCount>0).Select(r=>new Climate(r.Cells.First(),r.Temperature,r.Vacuum)).ToList();
        using var vehicleExpansion=RimLayers.Vehicles.VehiclePathingExpansion.Begin(map);
        try
        {
            // Native path jobs retain map buffer views. Complete them before preparing any replacement.
            AccessTools.Method(typeof(PathFinder),"ForceCompleteScheduledJobs").Invoke(oldFinder,null);Lap("wait-path-jobs");
            PrepareNative(map,changes,oldCount,newCount);
            PrepareManaged(map,changes,oldSize,full,oldCount,newCount);
            changes.Set(map.autoBuildRoofAreaSetter,"queuedGenerateRooms",new List<Room>());
            PrepareRiver(map,changes,oldSize,layout);Lap("prepare-buffers");
            quota=LayerExpansionQuota.For(map,elevation);
            if(!quota.Accepted)throw new InvalidOperationException(quota.Reason);
            foreach(var p in map.mapPawns.AllPawnsSpawned)p.pather.StopDead();
            applied=true;changes.Apply();
            map.info.Size=full;map.cellIndices=new CellIndices(map);
            // FloodFiller uses -1, not zero, for unvisited cells. It is scratch state.
            changes.Install(map,"floodFiller",new FloodFiller(map));
            // Keep the entire original graph alive for rollback, including its link database.
            changes.Install(map,"regionGrid",new RegionGrid(map));
            changes.Install(map,"regionLinkDatabase",new RegionLinkDatabase());
            changes.Install(map,"regionMaker",new RegionMaker(map));
            changes.Install(map,"regionDirtyer",new RegionDirtyer(map));
            // Bulk terrain/roof callbacks must not build a half-populated region graph.
            changes.Install(map,"regionAndRoomUpdater",new RegionAndRoomUpdater(map){Enabled=false});
            newPathing=new Pathing(map);map.pathing=newPathing;
            newFinder=new PathFinder(map);map.pathFinder=newFinder;
            newDrawer=new MapDrawer(map);map.mapDrawer=newDrawer;
            owner.ReplaceLayout(previous,layout);owner.Sky.Rebuild();configured=true;Lap("install-derivatives");
            vehicleExpansion?.Install();
            EmptySpace.Fill(map,layout,oldSize.z);Lap("fill-new-band");
            owner.Mountains.InitializeAddedLayers(previous?.LayerCount??1,mountainThings);
            var caveHives=owner.Underground.InitializeAddedLayers(previous?.LayerCount??1,mountainThings);Lap("geology");
            map.cellsInRandomOrder=new MapCellsInRandomOrder(map);
            map.pathing.RecalculateAllPerceivedPathCosts();
            map.reachability.ClearCache();map.regionAndRoomUpdater.Enabled=true;map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            RestoreClimate(map,climates);owner.Underground.InitializeClimate(previous?.LayerCount??1);Lap("regions");
            // Native hive Pawn/Lord creation needs complete regions, unlike bulk rock placement.
            var beforeHives=map.listerThings.AllThings.ToHashSet();
            try {foreach(var spawnHive in caveHives)spawnHive();}
            finally {mountainThings.AddRange(map.listerThings.AllThings.Where(t=>!beforeHives.Contains(t)));}
            owner.FinalizeInit();
            map.glowGrid.DevDirtyRect(new CellRect(0,0,full.x,full.z));
            map.mapDrawer.RegenerateEverythingNow();
            map.glowGrid.GlowGridUpdate_First();Lap("drawing");
            vehicleExpansion?.Rebuild();
            Validate(map,layout);
            Lap("validate");beforeCommit?.Invoke();
        }
        catch
        {
            if(applied)
            {
                RimLayers.Mountains.MountainService.RemoveSpawned(mountainThings);owner.Mountains.Restore(mountainState);owner.Underground.Restore(undergroundState);owner.Environment.Restore(environmentState);
                map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();map.roofCollapseBuffer.CellsMarkedToCollapse.AddRange(oldRoofCollapses);
                newFinder?.Dispose();newPathing?.Dispose();DisposeDrawer(newDrawer);
                if(configured)owner.ReplaceLayout(layout,previous);
                changes.Restore();map.info.Size=oldSize;map.cellIndices=new CellIndices(map);owner.Sky.Rebuild();
                vehicleExpansion?.Restore();
                map.pathFinder=oldFinder;map.pathing=oldPathing;map.mapDrawer=oldDrawer;
                // The retained finder still receives map events while the temporary larger graph is live.
                // Recompute its sources after rollback instead of feeding appended-cell deltas into old arrays.
                AccessTools.Method(typeof(PathFinderMapData),"Notify_MapDirtied").Invoke(oldFinder.MapData,null);
                map.pathing.RecalculateAllPerceivedPathCosts();map.reachability.ClearCache();
                RestoreClimate(map,climates);
                map.glowGrid.DevDirtyRect(new CellRect(0,0,oldSize.x,oldSize.z));
                ResumePaths(paths);
            }
            changes.Discard();throw;
        }
        // All persistent state and derivatives are now usable. Release old owners exactly once.
        oldFinder.Dispose();oldPathing.Dispose();
        try {map.info.Size=oldSize;DisposeDrawer(oldDrawer);}finally {map.info.Size=full;}
        changes.Commit();owner.Underground.CommitGeneration();NotifyCommitted(map,layout);ResumePaths(paths);
        LayerExpansionQuota.Current?.Allocated(map);
        Log.Message($"[RimLayers] Expanded existing map {map.uniqueID}: {oldSize} -> {full}, {changes.Inventory.Count} field swaps, {timer.Elapsed.TotalMilliseconds:F1}ms.");
        return new(oldSize,full,timer.Elapsed.TotalMilliseconds,changes.Inventory,phases);
    }

    private static void PrepareNative(Map map,ExpansionBuffers p,int oldCount,int newCount)
    {
        p.GrowBits(map.fogGrid,"fogGrid",oldCount,newCount);
        p.GrowBits(map.glowGrid,"lightBlockers",oldCount,newCount);
        p.GrowBits(map.glowGrid,"dirtyCells",oldCount,newCount,true);
        p.GrowNative<Color32>(map.glowGrid,"accumulatedGlow",oldCount,newCount);
        p.GrowNative<Color32>(map.glowGrid,"accumulatedGlowNoCavePlants",oldCount,newCount);
        p.GrowNative<float>(map.snowGrid,"depthGrid",oldCount,newCount);
        if(map.sandGrid!=null)p.GrowNative<float>(map.sandGrid,"depthGrid",oldCount,newCount);
        p.GrowNative<byte>(map.avoidGrid,"grid",oldCount,newCount);
    }
    private static IEnumerable<FieldInfo> AllFields(Type type)
    {
        for(Type? t=type;t!=null;t=t.BaseType)foreach(var f in t.GetFields(Fields))yield return f;
    }
    private static void PrepareManaged(Map map,ExpansionBuffers p,IntVec3 oldSize,IntVec3 full,int oldCount,int newCount)
    {
        p.GrowStorageGroups(map.haulDestinationManager,oldSize.x,oldSize.z,full.z);
        var seen=new HashSet<object>(ReferenceEqualityComparer.Instance);
        var roots=new List<object>();
        foreach(var f in AllFields(typeof(Map)))
        {
            if(f.GetValue(map) is not {} value||value is PathFinder or Pathing or MapDrawer or FloodFiller or RegionGrid or RegionLinkDatabase or RegionMaker or RegionDirtyer or RegionAndRoomUpdater or Map or Def||value.GetType().IsValueType)continue;
            roots.Add(value);
        }
        roots.AddRange(map.areaManager.AllAreas);roots.AddRange(map.zoneManager.AllZones);
        roots.AddRange(map.planManager.AllPlans);roots.AddRange(map.components);
        // A mod can hold an Area it dropped from AreaManager.AllAreas. Dubs Bad Hygiene's
        // RefreshAreas() removes duplicates with `x != cleanWater` for all three of its area types,
        // so its dirty-water and ocean areas are always removed while the component keeps writing to
        // them. Those areas never appear in AllAreas, so they are found through components instead.
        foreach(var holder in map.components)
            foreach(var field in AllFields(holder.GetType()))
                if(typeof(Area).IsAssignableFrom(field.FieldType)&&field.GetValue(holder) is Area orphan)roots.Add(orphan);
        // Only map-owned managers/components and their grid/drawer members, never Thing or other Map graphs.
        for(int index=0;index<roots.Count;index++)
        {
            var root=roots[index];if(!seen.Add(root))continue;
            // A registered third-party component must settle in-flight work (jobs, pending writes)
            // while its current allocations are still the live ones.
            RimLayers.Compatibility.LayerCellBufferCompatibility.Prepare(root);
            foreach(var f in AllFields(root.GetType()))
            {
                if(p.Has(root,f))continue;
                var value=f.GetValue(root);
                // NativeArray<T> is a struct, so it must not be filtered out by a value-type test;
                // TryGrow itself verifies the field is a declared NativeArray<T> of the exact size.
                bool nativeDeclared=value!=null&&
                    RimLayers.Compatibility.LayerCellBufferCompatibility.TryGrow(p,root,f,value,oldCount,newCount);
                if(nativeDeclared)continue;
                if(value is Array array&&GridCapacityRepair.IsCellBuffer(array,oldCount))
                {
                    // ThingGrid's per-cell lists must not stay null in the appended band.
                    if(array.Rank==1&&root is ThingGrid)p.GrowArray(root,f,array,newCount);
                    else p.GrowCells(root,f,array,oldCount,newCount);
                }
                else if(value is CellIndices ci&&ci.SizeX==oldSize.x&&ci.SizeZ==oldSize.z)p.Set(root,f,new CellIndices(full.x,full.z));
                else if(value is BoolGrid or ByteGrid or IntGrid or CellGrid or CellBoolDrawer)roots.Add(value);
                else if(value!=null&&value.GetType().Namespace?.StartsWith("Unity.Collections",StringComparison.Ordinal)==true)
                {
                    // A released or never-allocated native buffer owns no cells, so there is nothing to
                    // carry into the appended rows and nothing to index past the old size. Adapters
                    // use exactly this shape to hand a lazily allocated grid back to its owner, and
                    // Vector/bit containers already expose IsCreated, so treat it as the live test.
                    bool live=value.GetType().GetProperty("IsCreated")?.GetValue(value) is not bool created||created;
                    var length=live?value.GetType().GetProperty("Length")?.GetValue(value):null;
                    if(length is int n&&n>=oldCount&&n<=oldCount+63)
                        throw new NotSupportedException($"Unmapped native per-cell buffer: {root.GetType().FullName}.{f.Name}. Expansion was not applied.");
                }
            }
            // An Area grid (Verse.Area.innerGrid) is sized from map.Size when the Area is constructed,
            // which is before the storage extent exists, so it is still ground-sized at this point and
            // matches neither oldSize nor the new size. The copy length is therefore taken from the
            // buffer itself; the test is the buffer's own consistency, not the map's old rectangle.
            if(root is BoolGrid b&&b.Width==full.x&&b.Height!=full.z)
            {
                var backing=AccessTools.Field(typeof(BoolGrid),"arr");
                if(backing?.GetValue(b)is bool[] cells)
                {
                    int keep=Math.Min(cells.Length,b.Width*b.Height);
                    p.GrowCells(b,backing,cells,keep,full.x*full.z);
                }
                p.Set(root,"height",full.z);
            }
            // Same flat layout and the same ground-vs-storage size hazard; growing only the size field
            // would leave the storage short and the next CellToIndex write would throw.
            if(root is ByteGrid or IntGrid or CellGrid)
            {
                var z=AccessTools.Field(root.GetType(),"mapSizeZ");
                var backing=AccessTools.Field(root.GetType(),"grid");
                int x=(int)AccessTools.Field(root.GetType(),"mapSizeX").GetValue(root)!;
                int height=(int)z.GetValue(root)!;
                if(x==full.x&&height!=full.z&&backing?.GetValue(root)is Array grid)
                {
                    int keep=Math.Min(grid.Length,x*height);
                    p.GrowCells(root,backing,grid,keep,full.x*full.z);
                    p.Set(root,z,full.z);
                }
            }
            if(root is CellBoolDrawer)
            {
                if(AccessTools.Field(typeof(CellBoolDrawer),"mapSizeZ")is {} z)p.Set(root,z,full.z);
                if(AccessTools.Field(typeof(CellBoolDrawer),"dirty")is {} dirty)p.Set(root,dirty,true);
            }
        }
        // Third-party cell grids keep their buffers one level below the component: Bad Hygiene and
        // Rimefeller both nest a `GridLayer`, and Rimefeller nests an `OilGrid`, so the root walk
        // above never reaches them. They are grown here, inside the transaction, because the redraw
        // at the end of it regenerates section overlays that index those buffers directly.
        var oldSections=Sections(oldSize);var fullSections=Sections(full);
        foreach(MapComponent component in map.components)
            if(component!=null)NestedGrids(component,p,oldCount,newCount,oldSections,fullSections,full);
        // These are randomized/derived views, not saved map facts.
        p.Set(map.gasGrid,"cellsInRandomOrder",null);
        p.Set(map.pollutionGrid,"allPollutableCells",null);
        p.Set(map, "cellsInRandomOrder",new MapCellsInRandomOrder(map));
        // Cache is nested rather than a direct Map field.
        var savedClimate=AccessTools.Field(typeof(TemperatureVacuumCache),"TemperatureVacuumSaveLoad").GetValue(map.TemperatureVacuumCache)!;
        foreach(var f in AllFields(savedClimate.GetType()))
            if(f.GetValue(savedClimate) is Array a&&a.Rank==1&&a.Length==oldCount)p.GrowArray(savedClimate,f,a,newCount);
    }
    private static IntVec2 Sections(IntVec3 size)=>
        new(Mathf.CeilToInt(size.x/17f),Mathf.CeilToInt(size.z/17f));

    /// <summary>True when <paramref name="holder"/> declares an array keyed by the per-cell index.</summary>
    private static bool HoldsCells(object holder,int cells)
    {
        foreach(var field in AllFields(holder.GetType()))
        {
            if(!field.FieldType.IsArray)continue;
            object? value;try {value=field.GetValue(holder);}catch {continue;}
            if(value is Array array&&GridCapacityRepair.IsCellBuffer(array,cells))return true;
        }
        return false;
    }

    /// <summary>
    /// Grows the per-cell buffers of a component's own grid objects, plus the section-indexed dirty
    /// rectangles and drawer extents that are derived from the old storage height.
    /// </summary>
    private static void NestedGrids(MapComponent component,ExpansionBuffers p,int oldCount,int newCount,
        IntVec2 oldSections,IntVec2 fullSections,IntVec3 full)
    {
        foreach(var field in AllFields(component.GetType()))
        {
            object? holder;try {holder=field.GetValue(component);}catch {continue;}
            if(holder==null||holder is Array or string or Def||holder.GetType().IsValueType)continue;
            // A collection or a Thing/Def graph is never a per-cell grid owner; growing its internal
            // array would corrupt it without updating its own element count.
            if(holder is IEnumerable||holder is Thing)continue;
            if(!HoldsCells(holder,oldCount))continue;
            foreach(var member in AllFields(holder.GetType()))
            {
                if(p.Has(holder,member))continue;
                object? value;try {value=member.GetValue(holder);}catch {continue;}
                if(value is Array array&&GridCapacityRepair.IsCellBuffer(array,oldCount))
                    p.GrowCells(holder,member,array,oldCount,newCount);
                else if(value is bool[,] flags
                    &&flags.GetLength(0)==oldSections.x&&flags.GetLength(1)==oldSections.z
                    &&(fullSections.x!=oldSections.x||fullSections.z!=oldSections.z))
                    p.Set(holder,member,new bool[fullSections.x,fullSections.z]);
                else if(value is CellBoolDrawer drawer)
                {
                    if(AccessTools.Field(typeof(CellBoolDrawer),"mapSizeZ")is {} z)p.Set(drawer,z,full.z);
                    if(AccessTools.Field(typeof(CellBoolDrawer),"dirty")is {} dirty)p.Set(drawer,dirty,true);
                }
            }
        }
    }

    private static void PrepareRiver(Map map,ExpansionBuffers p,IntVec3 oldSize,LayerLayout layout)
    {
        var water=map.waterInfo;var flow=water?.riverFlowMap;
        if(water==null||flow==null||flow.Count==0)return;
        if(flow.Count!=layout.Width*oldSize.z*2)throw new InvalidOperationException("Unexpected existing river flow dimensions.");
        float[] next=new float[layout.Width*layout.StorageHeight*2];
        for(int x=0;x<layout.Width;x++)for(int z=0;z<oldSize.z;z++)
        {int a=(x*oldSize.z+z)*2,b=(x*layout.StorageHeight+z)*2;next[b]=flow[a];next[b+1]=flow[a+1];}
        p.Set(water,"riverFlowMap",new List<float>(next));
        var textureField=AccessTools.Field(water.GetType(),"riverFlowTexture");
        var oldTexture=(Texture2D?)textureField.GetValue(water);
        p.OnCommit(()=>{if(oldTexture!=null)UnityEngine.Object.Destroy(oldTexture);});
        p.OnRestore(()=>{var current=(Texture2D?)textureField.GetValue(water);if(current!=null&&current!=oldTexture)UnityEngine.Object.Destroy(current);});
        p.Set(water,"riverFlowTexture",null);p.Set(water,"flowMapPixels",null);
    }
    private static void Validate(Map map,LayerLayout layout)
    {
        if(!layout.MatchesStorage(map.Size.x,map.Size.z)||map.cellIndices.NumGridCells!=layout.Width*layout.StorageHeight)
            throw new InvalidOperationException("Expanded map dimensions do not match.");
        var end=new IntVec3(layout.Width-1,0,layout.StorageHeight-1);
        _=map.thingGrid.ThingsListAt(end);_=map.roofGrid.RoofAt(end);_=map.gasGrid.GetDirect(end);
        _=map.fogGrid.IsFogged(end);_=map.glowGrid.VisualGlowAt(end);_=map.terrainGrid.TerrainAt(end);
    }
    private static void RestoreClimate(Map map,List<Climate> rooms)
    {
        foreach(var info in rooms)if(info.Cell.GetRoom(map) is {} room){room.Temperature=info.Temperature;room.Vacuum=info.Vacuum;}
    }
    private static void ResumePaths(List<Resume> paths)
    {foreach(var p in paths)if(p.Pawn.Spawned&&!p.Pawn.Destroyed)p.Pawn.pather.StartPath(p.Target,p.End);}
    private static void DisposeDrawer(MapDrawer? drawer)
    {
        if(drawer==null)return;
        // A failed regeneration can leave a partially populated section matrix.
        if(AccessTools.Field(typeof(MapDrawer),"sections").GetValue(drawer) is Array sections)
            foreach(var section in sections)if(section!=null)AccessTools.Method(section.GetType(),"Dispose").Invoke(section,null);
        if(AccessTools.Field(typeof(MapDrawer),"global").GetValue(drawer) is IEnumerable layers)
            foreach(var layer in layers)if(layer!=null)AccessTools.Method(layer.GetType(),"Dispose").Invoke(layer,null);
    }
}
