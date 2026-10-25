using RimLayers.Layout;
using RimLayers.Environment;
using RimLayers.Combat;
using RimLayers.Presentation;
using RimLayers.Navigation;
using RimLayers.Structure;
using Verse;

namespace RimLayers.Core;

[Flags]
public enum LayerChangeKind { Structure = 1, Movement = 2, Air = 4, Heat = 8, Power = 16, Light = 32, Sound = 64, Fire = 128, Liquid = 256 }

public sealed class LayerMapComponent : MapComponent
{
    private int dataVersion;
    private int width;
    private int height;
    private int layers;
    private int stride;
    private int viewedLayer;
    private List<int> elevations = new();
    public LayerLayout? Layout { get; private set; }
    internal bool StairBootstrapPending; // Derived request; loaded stairs re-request it in SpawnSetup.
    public bool Managed => Layout != null;
    public int ViewedLayer => viewedLayer;
    public long Revision { get; private set; }
    public event Action<LayerCell, LayerChangeKind>? Changed;
    public HashSet<IntVec3> StructuralCells = new();
    public HashSet<IntVec3> PendingCollapse = new();
    public ConnectorGraph Connectors { get; }
    public RimLayers.Ships.ShipStructureService Ships { get; }
    public RimLayers.Ships.ShipUnderlays Underlays { get; }
    public RimLayers.Ships.ShipRecovery Recovery { get; }
    public RimLayers.Mountains.MountainService Mountains { get; }
    public RimLayers.Underground.UndergroundService Underground { get; }
    public StructuralSupport Structure { get; }
    public MaterialReturns Returns { get; }
    public CollapseProcessor Collapses { get; }
    public SkyfallService Skyfall { get; }
    public CombatService Combat { get; }
    public PowerLinks Power { get; }
    public PipeLinks Pipes { get; }
    public AirLinks Air { get; }
    public SkyExposure Sky { get; }
    public HeatLinks Heat { get; }
    public LayerEnvironment Environment { get; }
    public FireLinks Fire { get; }
    public Acoustics Sound { get; }
    public DangerWarnings Warnings { get; }

    public LayerMapComponent(Map map) : base(map)
    {
        Connectors = new ConnectorGraph(this);
        Mountains = new RimLayers.Mountains.MountainService(this);
        Underground = new RimLayers.Underground.UndergroundService(this);
        Structure = new StructuralSupport(this);
        Ships = new RimLayers.Ships.ShipStructureService(this);
        Underlays = new RimLayers.Ships.ShipUnderlays(this);
        Recovery = new RimLayers.Ships.ShipRecovery(this);
        Returns = new MaterialReturns(this);
        Collapses = new CollapseProcessor(this);
        Skyfall = new SkyfallService(this);
        Combat = new CombatService(this);
        Power = new PowerLinks(this);
        Pipes = new PipeLinks(this);
        Air = new AirLinks(this);
        Sky = new SkyExposure(this);
        Heat = new HeatLinks(this);
        Environment = new LayerEnvironment(this);
        Fire = new FireLinks(this);
        Sound = new Acoustics(this);
        Warnings = new DangerWarnings(this);
    }

    internal void Configure(LayerLayout layout)
    {
        if (Managed) throw new InvalidOperationException("A map layout cannot be replaced.");
        dataVersion = LayerLayout.CurrentVersion;
        width = layout.Width; height = layout.Height; layers = layout.LayerCount; stride = layout.Stride;
        Layout = layout; elevations = layout.Elevations.ToList();
        ClearForCache();
    }

    internal void ReplaceLayout(LayerLayout? expected, LayerLayout? next)
    {
        if(!ReferenceEquals(Layout,expected))throw new InvalidOperationException("Expansion layout ownership changed.");
        if(expected!=null && next!=null && (expected.Width!=next.Width || expected.Height!=next.Height || expected.Stride!=next.Stride))
            throw new InvalidOperationException("Existing layer coordinates cannot change.");
        if(expected!=null&&next!=null&&!next.Extends(expected)&&!expected.Extends(next))throw new InvalidOperationException("Stable layer elevations cannot change.");
        Layout=next; elevations=next?.Elevations.ToList()??new();
        dataVersion=next==null?0:LayerLayout.CurrentVersion;
        width=next?.Width??0;height=next?.Height??0;layers=next?.LayerCount??0;stride=next?.Stride??0;
        Connectors.Invalidate();Environment.Invalidate();
        ClearForCache();
    }

    public bool TryDecode(IntVec3 cell, out LayerCell logical)
    {
        logical = default;
        return Layout != null && Layout.TryDecode(new StorageCell(cell.x, cell.y, cell.z), out logical);
    }

    public IntVec3 ToStorage(LayerCell cell)
    {
        StorageCell result = (Layout ?? throw new InvalidOperationException("Unmanaged map.")).ToStorage(cell);
        return new IntVec3(result.X, result.Y, result.Z);
    }

    public int Elevation(int layer) => Layout!.ElevationOf(layer);
    public IntVec3 AtElevation(int elevation, int x, int z) => ToStorage(new(Layout!.LayerAt(elevation), x, z));
    public IntVec3 Adjacent(IntVec3 at, int direction) => TryDecode(at, out var c) && Layout!.TryGetAdjacentLayer(c.LayerId, direction, out int next) ? ToStorage(new(next, c.X, c.Z)) : IntVec3.Invalid;
    public string LevelName(int id) => Elevation(id)<0 ? "RL_UndergroundLevel".Translate(-Elevation(id)).ToString() : "RL_SurfaceLevel".Translate(Elevation(id)+1).ToString();

    public CellRect LayerRect(int layer) => new(0, layer * Layout!.Stride, Layout.Width, Layout.Height);
    public bool IsViewedCell(IntVec3 cell) => TryDecode(cell, out LayerCell logical) && logical.LayerId == viewedLayer;
    public bool IsGroundCell(IntVec3 cell) => TryDecode(cell, out LayerCell logical) && logical.LayerId == 0;

    public IEnumerable<IntVec3> Column(int x, int z)
    {
        if (Layout == null || !Layout.Contains(new LayerCell(0, x, z))) yield break;
        for (int layer = 0; layer < Layout.LayerCount; layer++) yield return ToStorage(new LayerCell(layer, x, z));
    }

    public void Invalidate(IntVec3 cell, LayerChangeKind kind)
    {
        if (!TryDecode(cell, out LayerCell logical)) return;
        Revision++;
        Changed?.Invoke(logical, kind);
    }

    internal void SetViewedLayer(int layer)
    {
        if (Layout == null || (uint)layer >= (uint)Layout.LayerCount) throw new ArgumentOutOfRangeException(nameof(layer));
        viewedLayer = layer;
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref dataVersion, "rimLayersVersion", 0);
        Scribe_Values.Look(ref width, "logicalWidth", 0);
        Scribe_Values.Look(ref height, "logicalHeight", 0);
        Scribe_Values.Look(ref layers, "layerCount", 0);
        Scribe_Values.Look(ref stride, "layerStride", 0);
        Scribe_Values.Look(ref viewedLayer, "viewedLayer", 0);
        Scribe_Collections.Look(ref elevations, "layerElevations", LookMode.Value);
        Scribe_Collections.Look(ref StructuralCells, "structuralCells", LookMode.Value);
        Scribe_Collections.Look(ref PendingCollapse, "pendingCollapse", LookMode.Value);
        Environment.ExposeData();Combat.ExposeData();Returns.ExposeData();Mountains.ExposeData();Underground.ExposeData();Ships.ExposeData();Underlays.ExposeData();Recovery.ExposeData();
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            StructuralCells ??= new();
            PendingCollapse ??= new();
        }
        if (Scribe.mode == LoadSaveMode.PostLoadInit) RestoreLayout();
    }

    private void RestoreLayout()
    {
        Layout = null;
        Revision = 0;
        if (dataVersion == 0 && width == 0 && height == 0 && layers == 0 && stride == 0) return; // Existing vanilla map.
        if (dataVersion != 1 && dataVersion != LayerLayout.CurrentVersion)
            throw new InvalidOperationException($"[RimLayers] Unsupported layout version {dataVersion}; refusing to reinterpret the save.");
        if(dataVersion==1)elevations=Enumerable.Range(0,layers).ToList();
        if(elevations==null||elevations.Count!=layers)throw new InvalidOperationException("[RimLayers] Missing elevation table.");
        LayerLayout layout = new(width, height, elevations, stride);
        dataVersion=LayerLayout.CurrentVersion;
        if (!layout.MatchesStorage(map.Size.x, map.Size.z))
            throw new InvalidOperationException($"[RimLayers] Layout/storage mismatch on map {map.uniqueID}.");
        Layout = layout;
        viewedLayer = Math.Clamp(viewedLayer, 0, layout.LayerCount - 1);
        // For() may already have memoised "this map is unmanaged" while loading, before ExposeData
        // restored the layout. Drop that negative result or the map stays invisible after a load.
        ClearForCache();
    }

    /// <summary>Drops the For() memo. Must run on every unmanaged&lt;-&gt;managed transition.</summary>
    private static void ClearForCache()
    {
        cachedMap = null; cachedComponent = null;
    }

    public override void FinalizeInit()
    {
        base.FinalizeInit();
        if (Managed) { Combat.RebuildFlights(); Power.Rebuild(); Air.Rebuild(); Sky.Rebuild(); Warnings.Rebuild(); }
        if (Managed && !Layout!.MatchesStorage(map.Size.x, map.Size.z))
            throw new InvalidOperationException("[RimLayers] Finalized map has an invalid storage size.");
        if (Managed)
            for (int z = 0; z < Layout!.StorageHeight; z++)
                if (z % Layout.Stride >= Layout.Height)
                    for (int x = 0; x < Layout.Width; x++)
                        map.glowGrid.LightBlockerAdded(new IntVec3(x, 0, z));
    }

    public override void MapComponentTick()
    {
        base.MapComponentTick();
        if(StairBootstrapPending)StairLayerBootstrap.Process(this);
        if(Managed&&Find.TickManager.TicksGame%30==0)Connectors.TickExtensions();
        if (Managed) { Ships.Tick(); Collapses.Tick();Returns.Tick();Recovery.Tick(); Combat.Tick(); Air.Tick(); Environment.Tick(); Warnings.Tick(); }
    }

    public override void MapComponentDraw()
    {
        base.MapComponentDraw();
        if(Managed&&map==Find.CurrentMap){RimLayers.Environment.LocalWeatherOverlay.DrawResiduals(this);RimLayers.Ships.ShipMigration.Draw(this);RemoteMechanitorRadius.Draw(this);LandingMarkers.Draw(this);RimLayers.Mountains.Dialog_MountainMigration.Draw(this);}
    }

    public override void MapComponentOnGUI()
    {
        base.MapComponentOnGUI();
        if (Managed && map == Find.CurrentMap) { LayerView.DrawControls(this); LandingMarkers.OnGUI(this); }
    }

    // Map.GetComponent<T>() scans Map.components linearly and this lookup sits on per-cell paths
    // (GenSight.LineOfSight, GasCanMoveTo, SkyQueries.Roofed) and per-frame UI paths. Callers repeat
    // it for the same map, so a single-entry memo removes the scan. Configure() is the only
    // unmanaged->managed transition and clears the memo.
    [ThreadStatic] private static Map? cachedMap;
    [ThreadStatic] private static LayerMapComponent? cachedComponent;
    public static long ForCalls { get; private set; }
    public static long ForScans { get; private set; }
    public static void ResetForStatistics() { ForCalls = 0; ForScans = 0; }

    public static LayerMapComponent? For(Map? map)
    {
        if (map == null) return null;
        ForCalls++;
        if (ReferenceEquals(map, cachedMap)) return cachedComponent;
        ForScans++;
        LayerMapComponent? component = map.GetComponent<LayerMapComponent>();
        component = component?.Managed == true ? component : null;
        cachedMap = map;
        cachedComponent = component;
        return component;
    }
}
