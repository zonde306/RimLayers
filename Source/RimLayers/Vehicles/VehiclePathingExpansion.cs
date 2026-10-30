using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using Verse;
using Verse.AI;
namespace RimLayers.Vehicles;

/// <summary>Retains the original native graph until the map-size transaction commits.
/// Native workers finish before any map buffer is changed.</summary>
internal sealed class VehiclePathingExpansion:IDisposable
{
    private readonly Map map;
    private readonly FrameworkVehicleAdapter adapter;
    private readonly object system;
    private readonly FieldInfo dataField,deferredField;
    private readonly object oldData,oldDeferred;
    private readonly MethodInfo construct,regenerate;
    private readonly object allGrids,forced;
    private readonly IDisposable pause;
    private readonly List<(Pawn Pawn,LocalTargetInfo Target,PathEndMode Mode)> paths=new();
    private readonly object? oldMgr;
    private readonly List<(FieldInfo field,Delegate del)> removedEventDelegates=new();
    private bool installed,disposed;
    private static PropertyInfo? ManagerProp(Type type)=>
        type.GetProperty("PathFinderManager",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
    private VehiclePathingExpansion(Map map,FrameworkVehicleAdapter adapter)
    {
        this.map=map;
        this.adapter=adapter;
        system=map.components.Single(c=>c.GetType().FullName=="Vehicles.VehiclePathingSystem");
        var type=system.GetType();
        var mgrProp=ManagerProp(type);
        oldMgr=mgrProp?.GetValue(system);
        dataField=type.GetField("vehicleData",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)
            ??type.GetField("<PathData>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)
            ??FrameworkVehicleAdapter.Field(type,"vehicleData");
        deferredField=FrameworkVehicleAdapter.Field(type,"deferredGridGeneration");
        construct=FrameworkVehicleAdapter.Method(type,"ConstructComponents",typeof(void));
        var selection=type.GetNestedType("GridSelection")!;var deferment=type.GetNestedType("GridDeferment")!;
        regenerate=FrameworkVehicleAdapter.Method(type,"RegenerateGrids",typeof(void),selection,deferment);
        allGrids=Enum.Parse(selection,"All");forced=Enum.Parse(deferment,"Forced");
        oldData=dataField.GetValue(system)!;oldDeferred=deferredField.GetValue(system)!;
        var taskField=adapter.PatherType.GetField("curPathTask",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
        var actors=map.mapPawns.AllPawnsSpawned.Where(adapter.IsVehicle).ToArray();
        if(taskField!=null)
        {
            var tasks=actors.Select(p=>taskField.GetValue(adapter.Pather(p))).OfType<Task>().ToArray();
            // Do not cancel a player's in-flight request merely because expansion was attempted.
            // A timeout rejects expansion with every original buffer and movement state intact.
            if(!Task.WaitAll(tasks,TimeSpan.FromSeconds(5)))throw new TimeoutException("Vehicle path workers are busy; map expansion was not applied.");
        }
        pause=PauseWorker(system);
        try
        {
            var moving=FrameworkVehicleAdapter.Property(adapter.PatherType,"Moving");
            var target=FrameworkVehicleAdapter.Property(adapter.PatherType,"Destination");
            var mode=adapter.PatherType.GetField("peMode",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            var request=FrameworkVehicleAdapter.Property(adapter.PatherType,"RequestStatus");
            foreach(var pawn in actors)
            {
                var pather=adapter.Pather(pawn);
                if((bool)moving.GetValue(pather)!)
                {
                    var endMode=mode!=null?(PathEndMode)mode.GetValue(pather)!:PathEndMode.OnCell;
                    paths.Add((pawn,(LocalTargetInfo)target.GetValue(pather)!,endMode));
                }
                VehicleTraffic.Command(()=>adapter.Stop(pawn));
                if(taskField!=null)taskField.SetValue(pather,null);
                request.SetValue(pather,Enum.Parse(request.PropertyType,"None"));
            }
        }
        catch{pause.Dispose();throw;}
    }
    internal static VehiclePathingExpansion? Begin(Map map)=>VehicleCompatibility.Adapter is FrameworkVehicleAdapter adapter?
        new(map,adapter):null;
    internal void Install()
    {
        installed=true;
        if(oldMgr!=null)UnhookManagerEvents(map,oldMgr,removedEventDelegates);
        deferredField.SetValue(system,Activator.CreateInstance(deferredField.FieldType,system));
        construct.Invoke(system,null);
        RefreshDetachedComponents(map);
    }
    internal void Rebuild()
    {
        if(oldMgr!=null)
        {
            DisposeManager(oldMgr);
            removedEventDelegates.Clear();
        }
        regenerate.Invoke(system,new[]{allGrids,forced});
        RecacheVehiclePatherComponents(map,adapter);
        ReclaimVehiclePositions(map,adapter);
    }
    internal void Restore()
    {
        if(!installed)return;
        var mgrProp=ManagerProp(system.GetType());
        var newMgr=mgrProp?.GetValue(system);
        if(newMgr!=null&&!ReferenceEquals(newMgr,oldMgr))
        {
            UnhookManagerEvents(map,newMgr,null);
            DisposeManager(newMgr);
        }
        mgrProp?.SetValue(system,oldMgr);
        if(oldMgr!=null&&removedEventDelegates.Count>0)
        {
            var eventsField=typeof(Map).GetField("events",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            var mapEvents=eventsField?.GetValue(map);
            if(mapEvents!=null)
            {
                foreach(var (field,del) in removedEventDelegates)
                {
                    var current=field.GetValue(mapEvents) as Delegate;
                    field.SetValue(mapEvents,Delegate.Combine(current,del));
                }
            }
            removedEventDelegates.Clear();
        }
        dataField.SetValue(system,oldData);deferredField.SetValue(system,oldDeferred);installed=false;
        RefreshDetachedComponents(map);
        RecacheVehiclePatherComponents(map,adapter);
        ReclaimVehiclePositions(map,adapter);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        pause.Dispose();
        foreach(var p in paths)
            if(p.Pawn.Spawned&&!p.Pawn.Destroyed)VehicleTraffic.Command(()=>VehicleCompatibility.Resume(p.Pawn,p.Target,p.Mode));
    }
    internal static bool NeedsSync(Map map)
    {
        try
        {
            var compType=AccessTools.TypeByName("Vehicles.VehiclePositionManager");
            if(compType==null)return false;
            var cacheType=AccessTools.TypeByName("SmashTools.ComponentCache");
            if(cacheType==null)return false;
            var getComp=AccessTools.Method(cacheType,"GetDetachedMapComponent")?.MakeGenericMethod(compType);
            if(getComp==null)return false;
            var posMgr=getComp.Invoke(null,new object[]{map});
            if(posMgr==null)return true;
            var gridProp=posMgr.GetType().GetProperty("ThingIdGrid");
            var grid=gridProp?.GetValue(posMgr);
            if(grid==null)return true;
            var lenProp=grid.GetType().GetProperty("Length");
            var len=(int)(lenProp?.GetValue(grid)??0);
            if(len!=map.cellIndices.NumGridCells)return true;

            var sys=map.components.FirstOrDefault(c=>c.GetType().FullName=="Vehicles.VehiclePathingSystem");
            if(sys!=null)
            {
                var mgr=ManagerProp(sys.GetType())?.GetValue(sys);
                if(mgr!=null)
                {
                    var modGrid=mgr.GetType().GetProperty("ModifierGrid",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)?.GetValue(mgr);
                    if(modGrid!=null)
                    {
                        var mLen=(int)(modGrid.GetType().GetProperty("Length")?.GetValue(modGrid)??0);
                        if(mLen!=map.cellIndices.NumGridCells)return true;
                    }
                }
            }
            return false;
        }
        catch{return false;}
    }
    internal static void RefreshDetachedComponents(Map map)
    {
        try
        {
            var cacheType=AccessTools.TypeByName("SmashTools.ComponentCache");
            if(cacheType==null)return;
            var clear=AccessTools.Method(cacheType,"ClearMap",new[]{typeof(Map)});
            var preCache=AccessTools.Method(cacheType,"PreCache",new[]{typeof(Map)});
            clear?.Invoke(null,new object[]{map});
            preCache?.Invoke(null,new object[]{map});
        }
        catch(Exception e)
        {
            Log.Warning("[RimLayers vehicles] Failed to refresh detached map components: "+e);
        }
    }
    internal static void RecacheVehiclePatherComponents(Map map,FrameworkVehicleAdapter adapter)
    {
        try
        {
            var recache=adapter.PatherType.GetMethod("RecacheComponents",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            if(recache==null)return;
            foreach(var p in map.mapPawns.AllPawnsSpawned.Where(adapter.IsVehicle))
            {
                var pather=adapter.Pather(p);
                if(pather!=null)recache.Invoke(pather,null);
            }
        }
        catch(Exception e){Log.Warning("[RimLayers vehicles] Failed to recache vehicle components: "+e);}
    }
    internal static void ReclaimVehiclePositions(Map map,FrameworkVehicleAdapter adapter)
    {
        try
        {
            var reclaim=adapter.VehicleType.GetMethod("ReclaimPosition",BindingFlags.Public|BindingFlags.Instance);
            if(reclaim==null)return;
            foreach(var pawn in map.mapPawns.AllPawnsSpawned)
                if(adapter.IsVehicle(pawn))reclaim.Invoke(pawn,null);
        }
        catch(Exception e)
        {
            Log.Warning("[RimLayers vehicles] Failed to reclaim vehicle positions: "+e);
        }
    }
    internal static void SyncMapSize(Map map,FrameworkVehicleAdapter adapter)
    {
        var system=map.components.FirstOrDefault(c=>c.GetType().FullName=="Vehicles.VehiclePathingSystem");
        if(system==null)return;
        using var pause=PauseWorker(system);
        var type=system.GetType();
        var construct=FrameworkVehicleAdapter.Method(type,"ConstructComponents",typeof(void));
        var selection=type.GetNestedType("GridSelection")!;
        var deferment=type.GetNestedType("GridDeferment")!;
        var regenerate=FrameworkVehicleAdapter.Method(type,"RegenerateGrids",typeof(void),selection,deferment);
        var allGrids=Enum.Parse(selection,"All");
        var forced=Enum.Parse(deferment,"Forced");
        var deferredField=FrameworkVehicleAdapter.Field(type,"deferredGridGeneration");

        var mgrProp=ManagerProp(type);
        var oldMgr=mgrProp?.GetValue(system);
        if(oldMgr!=null)
        {
            UnhookManagerEvents(map,oldMgr,null);
            DisposeManager(oldMgr);
        }

        RefreshDetachedComponents(map);
        deferredField.SetValue(system,Activator.CreateInstance(deferredField.FieldType,system));
        construct.Invoke(system,null);
        regenerate.Invoke(system,new[]{allGrids,forced});
        RecacheVehiclePatherComponents(map,adapter);
        ReclaimVehiclePositions(map,adapter);
    }
    private static void UnhookManagerEvents(Map map,object? mgr,List<(FieldInfo field,Delegate del)>? saved)
    {
        if(mgr==null)return;
        try
        {
            var eventsField=typeof(Map).GetField("events",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            var mapEvents=eventsField?.GetValue(map);
            if(mapEvents==null)return;
            var targets=new HashSet<object>{mgr};
            var costGridsField=mgr.GetType().GetField("costGrids",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            if(costGridsField?.GetValue(mgr) is Array costGrids)
                foreach(var g in costGrids)if(g!=null)targets.Add(g);
            var dirtyGridsField=mgr.GetType().GetField("dirtyGrids",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            if(dirtyGridsField?.GetValue(mgr) is Array dirtyGrids)
                foreach(var d in dirtyGrids)if(d!=null)targets.Add(d);
            var pathFindersField=mgr.GetType().GetField("pathFinders",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            if(pathFindersField?.GetValue(mgr) is Array pathFinders)
                foreach(var p in pathFinders)if(p!=null)targets.Add(p);
            var modGridProp=mgr.GetType().GetProperty("ModifierGrid",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            if(modGridProp?.GetValue(mgr) is object modGrid)targets.Add(modGrid);
            foreach(var field in mapEvents.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                if(typeof(Delegate).IsAssignableFrom(field.FieldType)&&field.GetValue(mapEvents) is Delegate del)
                {
                    foreach(var inv in del.GetInvocationList())
                    {
                        if(inv.Target!=null&&targets.Contains(inv.Target))
                        {
                            del=Delegate.Remove(del,inv)!;
                            saved?.Add((field,inv));
                        }
                    }
                    field.SetValue(mapEvents,del);
                }
            }
        }
        catch(Exception e){Log.Warning("[RimLayers vehicles] Failed to unhook manager events: "+e);}
    }
    private static void DisposeManager(object? mgr)
    {
        if(mgr==null)return;
        try
        {
            if(mgr is IDisposable d)d.Dispose();
            else mgr.GetType().GetMethod("Dispose",BindingFlags.Instance|BindingFlags.Public)?.Invoke(mgr,null);
        }
        catch(Exception e){Log.Warning("[RimLayers vehicles] Failed to dispose manager: "+e);}
    }
    internal static IDisposable PauseWorker(object system)
    {
        var thread=FrameworkVehicleAdapter.Field(system.GetType(),"dedicatedThread").GetValue(system);
        if(thread==null)return new ResumeWorker(null,null);
        var type=thread.GetType();var suspended=FrameworkVehicleAdapter.Property(type,"IsSuspended");
        if((bool)suspended.GetValue(thread)!)return new ResumeWorker(null,null);
        var suspend=FrameworkVehicleAdapter.Method(type,"Suspend",typeof(void));
        var resume=FrameworkVehicleAdapter.Method(type,"Unsuspend",typeof(void));
        suspend.Invoke(thread,null);
        if(!(bool)suspended.GetValue(thread)!)throw new InvalidOperationException("Vehicle region worker did not suspend; operation was not applied.");
        return new ResumeWorker(thread,resume);
    }
    private sealed class ResumeWorker(object? thread,MethodInfo? resume):IDisposable
    {
        private bool done;
        public void Dispose(){if(done)return;done=true;resume?.Invoke(thread,null);}
    }
}
