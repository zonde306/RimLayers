using System.Diagnostics;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Mountains;

public sealed class MountainLayer : IExposable
{
    public int Id;
    public Dictionary<IntVec3,ThingDef> Ore = new(); // Logical planar coordinates, never storage distance.
    public HashSet<IntVec3> Processed = new();
    public void ExposeData()
    {
        Scribe_Values.Look(ref Id,"layer");
        Scribe_Collections.Look(ref Ore,"orePlan",LookMode.Value,LookMode.Def);
        Scribe_Collections.Look(ref Processed,"processed",LookMode.Value);
        if(Scribe.mode==LoadSaveMode.PostLoadInit){Ore??=new();Processed??=new();}
    }
    internal MountainLayer Copy()=>new(){Id=Id,Ore=new(Ore),Processed=new(Processed)};
}

public sealed class MountainState : IExposable
{
    public int Version=1,Seed;
    public bool Initialized;
    public Dictionary<IntVec3,ThingDef> Columns=new();
    public HashSet<IntVec3> Floors=new();
    public List<MountainLayer> Layers=new();
    public void ExposeData()
    {
        Scribe_Values.Look(ref Version,"version",1);Scribe_Values.Look(ref Seed,"seed");Scribe_Values.Look(ref Initialized,"initialized");
        Scribe_Collections.Look(ref Columns,"columns",LookMode.Value,LookMode.Def);
        Scribe_Collections.Look(ref Floors,"naturalFloors",LookMode.Value);
        Scribe_Collections.Look(ref Layers,"layers",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)
        {Columns??=new();Floors??=new();Layers??=new();if(Version!=1)throw new InvalidOperationException("Unsupported mountain data version.");}
    }
    internal MountainState Copy()=>new(){Version=Version,Seed=Seed,Initialized=Initialized,Columns=new(Columns),Floors=new(Floors),Layers=Layers.Select(x=>x.Copy()).ToList()};
}

/// <summary>Geology is immutable; Mineables, their damage and yield remain native Things.</summary>
public sealed class MountainService
{
    private readonly LayerMapComponent owner;
    private MountainState state=new();
    private static readonly AccessTools.FieldRef<TerrainGrid,TerrainDef[]> Under=AccessTools.FieldRefAccess<TerrainGrid,TerrainDef[]>("underGrid");
    public int FloorCount=>state.Floors.Count;
    public bool Initialized=>state.Initialized;
    public IReadOnlyDictionary<IntVec3,ThingDef> Columns=>state.Columns;
    public MountainService(LayerMapComponent owner)=>this.owner=owner;
    public bool IsNaturalFloor(IntVec3 cell)=>state.Floors.Contains(cell)||owner.Underground.IsNaturalFloor(cell);
    public bool TryGetRock(IntVec3 cell,out ThingDef rock)
    {
        rock=null!;
        return owner.TryDecode(cell,out var c)&&state.Columns.TryGetValue(new(c.X,0,c.Z),out rock!);
    }
    public void ExposeData(){Scribe_Deep.Look(ref state,"mountains");state??=new();}
    internal MountainState Snapshot()=>state.Copy();
    internal void Restore(MountainState snapshot)=>state=snapshot;

    private MountainState Geology()
    {
        if(state.Initialized)return state;
        var result=state.Copy();result.Initialized=true;result.Seed=Gen.HashCombineInt(owner.map.ConstantRandSeed,0x524c4d54);
        var l=owner.Layout!;var map=owner.map;
        var types=Find.World.NaturalRockTypesIn(map.Tile).Where(r=>r.building?.naturalTerrain!=null).OrderBy(r=>r.defName,StringComparer.Ordinal).ToArray();
        // Multi-source flood determines the nearest real rock without reinitializing global RockNoises.
        var nearest=new Dictionary<IntVec3,ThingDef>();var queue=new Queue<IntVec3>();
        foreach(var c in owner.LayerRect(0).Cells)
        {
            var rock=c.GetEdifice(map)?.def;
            if(rock?.building?.isNaturalRock!=true||rock.building.isResourceRock||rock.building.naturalTerrain==null)
            {var terrain=map.terrainGrid.TerrainAt(c);rock=types.FirstOrDefault(r=>r.building.naturalTerrain==terrain||r.building.naturalTerrain.smoothedTerrain==terrain);}
            if(rock?.building?.naturalTerrain!=null){nearest[c]=rock;queue.Enqueue(c);}
        }
        while(queue.TryDequeue(out var c))foreach(var d in GenAdj.CardinalDirections)
        {var n=c+d;if(n.x>=0&&n.z>=0&&n.x<l.Width&&n.z<l.Height&&nearest.TryAdd(n,nearest[c]))queue.Enqueue(n);}
        foreach(var c in owner.LayerRect(0).Cells)
            if(map.roofGrid.RoofAt(c)==RoofDefOf.RoofRockThick)
            {
                if(!nearest.TryGetValue(c,out var rock))rock=types.FirstOrDefault();
                if(rock==null)throw new NotSupportedException("Mountain columns require a native stone type with naturalTerrain.");
                result.Columns[c]=rock;
            }
        return result;
    }

    private sealed class OrePicker:GenStep_ScatterLumpsMineable
    {public ThingDef? Pick()=>ChooseThingDef();}
    private MountainLayer Plan(MountainState data,int layer)
    {
        var existing=data.Layers.FirstOrDefault(x=>x.Id==layer);if(existing!=null)return existing;
        var plan=new MountainLayer{Id=layer};data.Layers.Add(plan);
        var map=owner.map;var l=owner.Layout!;
        var profile=map.generatorDef?.genSteps.Select(s=>s.genStep).OfType<GenStep_RocksFromGrid>().FirstOrDefault();
        if(profile==null||map.TileInfo.WaterCovered||data.Columns.Count==0)return plan;
        float max=(float)AccessTools.Field(typeof(GenStep_RocksFromGrid),"maxMineableValue").GetValue(profile)!;
        bool ignore=(bool)AccessTools.Field(typeof(GenStep_RocksFromGrid),"ignoreMaxIfGravship").GetValue(profile)!;
        if(ModsConfig.OdysseyActive&&ignore&&map.wasSpawnedViaGravShipLanding)max=float.MaxValue;
        float density=(float?)AccessTools.Field(typeof(GenStep_RocksFromGrid),"overrideBlotchesPer10kCells").GetValue(profile)??GenStep_RocksFromGrid.GetResourceBlotchesPer10KCellsForMap(map);
        if(!map.IsStartingMap)density*=Current.Game.storyteller.difficulty.nomadicMineableResourcesFactor;
        if(density<=0)return plan;
        // Native helper assumes a square. Use the actual logical area, not the expanded Map.
        int count=Mathf.RoundToInt((float)(l.Width*l.Height)/Math.Max(1,Mathf.RoundToInt(10000f/density)));
        Rand.PushState(Gen.HashCombineInt(data.Seed,layer));
        try
        {
            var picker=new OrePicker{maxValue=max};
            var candidates=data.Columns.Keys.OrderBy(c=>c.z).ThenBy(c=>c.x).ToList();candidates.Shuffle();
            var centers=new List<IntVec3>();
            foreach(var center in candidates)
            {
                if(centers.Count>=count)break;
                if(plan.Ore.ContainsKey(center)||centers.Any(c=>(c-center).LengthHorizontalSquared<25))continue;
                var ore=picker.Pick();if(ore==null)break;
                var storage=owner.ToStorage(new(layer,center.x,center.z));
                var lump=GridShapeMaker.IrregularLump(storage,map,ore.building.mineableScatterLumpSizeRange.RandomInRange,
                    c=>owner.TryDecode(c,out var q)&&q.LayerId==layer&&data.Columns.ContainsKey(new(q.X,0,q.Z))&&!plan.Ore.ContainsKey(new(q.X,0,q.Z)));
                foreach(var c in lump){owner.TryDecode(c,out var q);plan.Ore[new(q.X,0,q.Z)]=ore;}
                centers.Add(center);
            }
        }
        finally{Rand.PopState();}
        return plan;
    }

    public sealed record Preview(int Layer,MountainState Data,List<IntVec3> Eligible,List<IntVec3> Skipped);
    public Preview PreviewMigration(int layer)
    {
        if(layer<1||layer>=owner.Layout!.LayerCount||owner.Elevation(layer)<=0)throw new ArgumentOutOfRangeException(nameof(layer));
        var data=Geology().Copy();var plan=Plan(data,layer);var yes=new List<IntVec3>();var no=new List<IntVec3>();
        foreach(var c in data.Columns.Keys.OrderBy(c=>c.z).ThenBy(c=>c.x))
        {if(plan.Processed.Contains(c))continue;var at=owner.ToStorage(new(layer,c.x,c.z));(Unused(at)?yes:no).Add(at);}
        return new(layer,data,yes,no);
    }
    private bool Unused(IntVec3 c)
    {
        var map=owner.map;
        return !state.Floors.Contains(c)&&map.terrainGrid.TerrainAt(c)==RimLayersDefOf.RL_OpenAir&&
            map.terrainGrid.FoundationAt(c)==null&&map.terrainGrid.UnderTerrainAt(c)==null&&map.terrainGrid.TempTerrainAt(c)==null&&
            map.roofGrid.RoofAt(c)==null&&c.GetThingList(map).Count==0&&map.zoneManager.ZoneAt(c)==null&&
            !map.areaManager.AllAreas.Any(a=>a[c])&&!map.planManager.AllPlans.Any(p=>p.ContainsCell(c))&&
            !map.designationManager.AllDesignations.Any(d=>d.target.Cell==c);
    }
    public int CommitMigration(Preview preview,Action? beforeCommit=null)
    {
        if(preview.Layer>=owner.Layout!.LayerCount)throw new InvalidOperationException("Migration layout changed.");
        // A stale preview must never replace a newer plan or processed state.
        if(state.Initialized&&(!state.Columns.OrderBy(x=>x.Key.z).ThenBy(x=>x.Key.x).SequenceEqual(preview.Data.Columns.OrderBy(x=>x.Key.z).ThenBy(x=>x.Key.x))||state.Seed!=preview.Data.Seed))
            throw new InvalidOperationException("Mountain geology changed; preview again.");
        AccessTools.Method(typeof(PathFinder),"ForceCompleteScheduledJobs").Invoke(owner.map.pathFinder,null);
        var old=Snapshot();var spawned=new List<Thing>();var touched=new List<IntVec3>();
        var previousFog=new HashSet<IntVec3>();
        var map=owner.map;var fog=Enumerable.Range(0,map.cellIndices.NumGridCells).Select(i=>map.fogGrid.IsFogged(i)).ToArray();
        var collapseQueue=map.roofCollapseBuffer.CellsMarkedToCollapse.ToList();
        var climate=map.regionGrid.AllRooms.Where(r=>r.CellCount>0).Select(r=>(Cell:r.Cells.First(),Temp:r.Temperature,Vacuum:r.Vacuum)).ToList();
        var candidateData=state.Initialized?state.Copy():preview.Data.Copy();
        if(candidateData.Layers.All(l=>l.Id!=preview.Layer))candidateData.Layers.Add(preview.Data.Layers.Single(l=>l.Id==preview.Layer).Copy());
        state=candidateData;
        try
        {
            var plan=state.Layers.Single(l=>l.Id==preview.Layer);
            foreach(var c in preview.Eligible)
            {
                owner.TryDecode(c,out var q);var column=new IntVec3(q.X,0,q.Z);
                if(plan.Processed.Contains(column)||!Unused(c))continue;
                if(c.Fogged(owner.map))previousFog.Add(c);
                touched.Add(c);Install(c,column,plan,spawned);
            }
            RevealEdges(touched);Rebuild();beforeCommit?.Invoke();return touched.Count;
        }
        catch
        {
            RemoveSpawned(spawned);state=old;
            foreach(var c in touched){owner.map.roofGrid.SetRoof(c,null);owner.map.terrainGrid.SetTerrain(c,RimLayersDefOf.RL_OpenAir);if(previousFog.Contains(c))owner.map.fogGrid.Refog(CellRect.SingleCell(c));else owner.map.fogGrid.Unfog(c);}
            for(int i=0;i<fog.Length;i++)if(map.fogGrid.IsFogged(i)!=fog[i])
            {var c=map.cellIndices.IndexToCell(i);if(fog[i])map.fogGrid.Refog(CellRect.SingleCell(c));else map.fogGrid.Unfog(c);}
            map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();map.roofCollapseBuffer.CellsMarkedToCollapse.AddRange(collapseQueue);
            Rebuild();foreach(var item in climate)if(item.Cell.GetRoom(map) is {} room){room.Temperature=item.Temp;room.Vacuum=item.Vacuum;}throw;
        }
    }
    internal void InitializeAddedLayers(int firstLayer,List<Thing> spawned)
    {
        var watch=Stopwatch.StartNew();state=Geology();int count=0;
        for(int layer=Math.Max(1,firstLayer);layer<owner.Layout!.LayerCount;layer++)
        {
            if(owner.Elevation(layer)<=0)continue;
            var plan=Plan(state,layer);var touched=new List<IntVec3>();
            foreach(var column in state.Columns.Keys.OrderBy(c=>c.z).ThenBy(c=>c.x))
            {
                if(plan.Processed.Contains(column))continue;
                var c=owner.ToStorage(new(layer,column.x,column.z));
                if(!Unused(c))continue;
                Install(c,column,plan,spawned);touched.Add(c);count++;
            }
            RevealEdges(touched);
        }
        Log.Message($"[RimLayers] Mountain initialization: {count} cells, {spawned.Count} rocks/minerals, {watch.Elapsed.TotalMilliseconds:F2}ms.");
    }
    private void Install(IntVec3 c,IntVec3 column,MountainLayer plan,List<Thing> spawned)
    {
        var rock=state.Columns[column];state.Floors.Add(c);plan.Processed.Add(column);
        owner.map.terrainGrid.SetTerrain(c,rock.building.naturalTerrain);
        // Generated rough stone can itself be layerable. It is the native base here, not a cover over Sand/OpenAir.
        Under(owner.map.terrainGrid)[owner.map.cellIndices.CellToIndex(c)]=null!;
        owner.map.roofGrid.SetRoof(c,RoofDefOf.RoofRockThick);
        var thing=ThingMaker.MakeThing(plan.Ore.GetValueOrDefault(column)??rock);spawned.Add(thing);GenSpawn.Spawn(thing,c,owner.map);
        owner.map.fogGrid.Refog(CellRect.SingleCell(c));
        owner.Invalidate(c,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Light);
    }
    private void RevealEdges(List<IntVec3> cells)
    {
        foreach(var c in cells)
            if(GenAdj.CardinalDirections.Any(d=>owner.TryDecode(c+d,out var q)&&owner.TryDecode(c,out var here)&&q.LayerId==here.LayerId&&!(c+d).Fogged(owner.map)&&(c+d).CanBeSeenOverFast(owner.map)))owner.map.fogGrid.Unfog(c);
    }
    private void Rebuild()
    {
        owner.map.pathing.RecalculateAllPerceivedPathCosts();owner.map.reachability.ClearCache();
        owner.map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();owner.Connectors.Invalidate();owner.Sky.Rebuild();
        owner.map.mapDrawer.RegenerateEverythingNow();
    }
    internal static void RemoveSpawned(List<Thing> spawned)
    {
        // These are transaction-owned rocks in a discarded band. Ordinary removal floods fog
        // and schedules roof-collapse work after every rock; WillReplace suppresses both.
        foreach(var t in spawned.AsEnumerable().Reverse())if(t.Spawned)t.Destroy(t is Mineable?DestroyMode.WillReplace:DestroyMode.Vanish);
    }
}
