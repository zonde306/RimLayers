using System.Reflection;
using RimLayers.Core;
using Verse;
using Verse.AI;
namespace RimLayers.Vehicles;

/// <summary>Retains the original native graph until the map-size transaction commits.
/// Native workers finish before any map buffer is changed.</summary>
internal sealed class VehiclePathingExpansion:IDisposable
{
    private readonly FrameworkVehicleAdapter adapter;
    private readonly object system;
    private readonly FieldInfo dataField,deferredField;
    private readonly object oldData,oldDeferred;
    private readonly MethodInfo construct,regenerate;
    private readonly object allGrids,forced;
    private readonly IDisposable pause;
    private readonly List<(Pawn Pawn,LocalTargetInfo Target,PathEndMode Mode)> paths=new();
    private bool installed,disposed;
    private VehiclePathingExpansion(Map map,FrameworkVehicleAdapter adapter)
    {
        this.adapter=adapter;
        system=map.components.Single(c=>c.GetType().FullName=="Vehicles.VehiclePathingSystem");
        var type=system.GetType();
        dataField=FrameworkVehicleAdapter.Field(type,"vehicleData");
        deferredField=FrameworkVehicleAdapter.Field(type,"deferredGridGeneration");
        construct=FrameworkVehicleAdapter.Method(type,"ConstructComponents",typeof(void));
        var selection=type.GetNestedType("GridSelection")!;var deferment=type.GetNestedType("GridDeferment")!;
        regenerate=FrameworkVehicleAdapter.Method(type,"RegenerateGrids",typeof(void),selection,deferment);
        allGrids=Enum.Parse(selection,"All");forced=Enum.Parse(deferment,"Forced");
        oldData=dataField.GetValue(system)!;oldDeferred=deferredField.GetValue(system)!;
        var taskField=FrameworkVehicleAdapter.Field(adapter.PatherType,"curPathTask");
        var actors=map.mapPawns.AllPawnsSpawned.Where(adapter.IsVehicle).ToArray();
        var tasks=actors.Select(p=>taskField.GetValue(adapter.Pather(p))).OfType<Task>().ToArray();
        // Do not cancel a player's in-flight request merely because expansion was attempted.
        // A timeout rejects expansion with every original buffer and movement state intact.
        if(!Task.WaitAll(tasks,TimeSpan.FromSeconds(5)))throw new TimeoutException("Vehicle path workers are busy; map expansion was not applied.");
        pause=PauseWorker(system);
        try
        {
            var moving=FrameworkVehicleAdapter.Property(adapter.PatherType,"Moving");
            var target=FrameworkVehicleAdapter.Property(adapter.PatherType,"Destination");
            var mode=FrameworkVehicleAdapter.Field(adapter.PatherType,"peMode");
            var request=FrameworkVehicleAdapter.Property(adapter.PatherType,"RequestStatus");
            foreach(var pawn in actors)
            {
                var pather=adapter.Pather(pawn);
                if((bool)moving.GetValue(pather)!)paths.Add((pawn,(LocalTargetInfo)target.GetValue(pather)!,(PathEndMode)mode.GetValue(pather)!));
                VehicleTraffic.Command(()=>adapter.Stop(pawn));
                taskField.SetValue(pather,null);request.SetValue(pather,Enum.Parse(request.PropertyType,"None"));
            }
        }
        catch{pause.Dispose();throw;}
    }
    internal static VehiclePathingExpansion? Begin(Map map)=>VehicleCompatibility.Adapter is FrameworkVehicleAdapter adapter?
        new(map,adapter):null;
    internal void Install()
    {
        installed=true;
        deferredField.SetValue(system,Activator.CreateInstance(deferredField.FieldType,system));
        construct.Invoke(system,null);
    }
    internal void Rebuild()=>regenerate.Invoke(system,new[]{allGrids,forced});
    internal void Restore()
    {
        if(!installed)return;
        dataField.SetValue(system,oldData);deferredField.SetValue(system,oldDeferred);installed=false;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        pause.Dispose();
        foreach(var p in paths)
            if(p.Pawn.Spawned&&!p.Pawn.Destroyed)VehicleTraffic.Command(()=>VehicleCompatibility.Resume(p.Pawn,p.Target,p.Mode));
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
