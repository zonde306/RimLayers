using System.Reflection;
using System.Linq.Expressions;
using System.Threading;
using HarmonyLib;
using RimLayers.Core;
using Verse;
using UnityEngine;
using Verse.AI;

namespace RimLayers.Vehicles;

/// <summary>Bindings are constructed completely before any hook is installed.</summary>
internal sealed class FrameworkVehicleAdapter:IVehicleAdapter
{
    public string Provider { get; }
    internal readonly Type VehicleType, PatherType;
    internal Action<Pawn,double>? AfterCommit;
    private readonly FieldInfo pather,actor,nextCell,currentPath;
    private readonly PropertyInfo pathCurrent;
    private readonly PropertyInfo regionUpdater;
    private readonly MethodInfo rebuildRegions;
    private readonly PropertyInfo fullRotation,canMove,drafted;
    private readonly MethodInfo start,stop,fail,reclaim,teleported,findPath,dispose;
    private readonly PropertyInfo pathFound,pathNodes,indexer,pathFinder;
    private readonly Type pathingType;
    private readonly MethodInfo rotFromAngle;
    private readonly PropertyInfo rotationAngle;
    internal readonly MethodInfo Validation;
    private readonly Func<Pawn,bool> canMoveCall,draftedCall;
    private readonly Func<Pawn,Vector3,VehicleVisual> visualCall;
    private readonly Func<Pawn,double> headingCall;
    private readonly Action<Pawn,double> headingSetCall;
    private readonly Action<Pawn> reclaimCall,teleportedCall;
    private readonly Func<Pawn,IntVec3,double,IEnumerable<IntVec3>> occupiedCall;
    [ThreadStatic] internal static bool FindingLeg;
    internal FrameworkVehicleAdapter(Assembly assembly,string provider)
    {
        Provider=provider;
        Type Required(string name)=>assembly.GetType("Vehicles."+name,true)!;
        VehicleType=Required("VehiclePawn");PatherType=Required("VehiclePathFollower");
        if(!typeof(Pawn).IsAssignableFrom(VehicleType))throw new InvalidOperationException("VehiclePawn is not a 1.7 Pawn.");
        pather=Field(VehicleType,"vehiclePather");actor=Field(PatherType,"vehicle");nextCell=Field(PatherType,"nextCell");
        if(pather.FieldType!=PatherType||actor.FieldType!=VehicleType)throw new InvalidOperationException("Vehicle/pather contract mismatch.");
        fullRotation=Property(VehicleType,"FullRotation");
        canMove=Property(VehicleType,"CanMoveFinal");drafted=Property(VehicleType,"Drafted");
        start=Method(PatherType,"StartPath",typeof(void),typeof(LocalTargetInfo),typeof(PathEndMode),typeof(bool));
        stop=Method(PatherType,"StopDead",typeof(void));
        fail=Method(PatherType,"PatherFailed",typeof(void));
        reclaim=Method(VehicleType,"ReclaimPosition",typeof(void));
        teleported=Method(VehicleType,"Notify_Teleported",typeof(void),typeof(bool),typeof(bool));
        var rot=fullRotation.PropertyType;
        rotFromAngle=Method(rot,"FromAngle",rot,typeof(float));rotationAngle=Property(rot,"AsAngle");
        pathingType=Required("VehiclePathingSystem");indexer=Property(pathingType,"Item");
        pathFinder=Property(indexer.PropertyType,"VehiclePathFinder");
        regionUpdater=Property(indexer.PropertyType,"VehicleRegionAndRoomUpdater");
        rebuildRegions=Method(regionUpdater.PropertyType,"TryRebuildVehicleRegions",typeof(void));
        var path=Required("VehiclePath");
        findPath=Method(pathFinder.PropertyType,"FindPath",path,typeof(IntVec3),typeof(LocalTargetInfo),VehicleType,typeof(CancellationToken),typeof(PathEndMode));
        Validation=Method(pathFinder.PropertyType,"ValidatePathRequest",typeof(bool),typeof(IntVec3),typeof(LocalTargetInfo),typeof(TraverseParms),typeof(PathEndMode));
        pathFound=Property(path,"Found");pathNodes=Property(path,"Nodes");
        currentPath=Field(PatherType,"curPath");pathCurrent=Property(path,"Current");
        dispose=Method(path,"Dispose",typeof(void));
        // Compile the hot pose/occupancy accessors once, without a hard dependency on Vehicles or SmashTools.
        var pawn=Expression.Parameter(typeof(Pawn),"pawn");
        var vehicle=Expression.Convert(pawn,VehicleType);
        var heading=Expression.Parameter(typeof(double),"heading");
        var cell=Expression.Parameter(typeof(IntVec3),"cell");
        var rotation=Expression.Call(rotFromAngle,Expression.Convert(heading,typeof(float)));
        canMoveCall=Expression.Lambda<Func<Pawn,bool>>(Expression.Property(vehicle,canMove),pawn).Compile();
        draftedCall=Expression.Lambda<Func<Pawn,bool>>(Expression.Property(vehicle,drafted),pawn).Compile();
        headingCall=Expression.Lambda<Func<Pawn,double>>(Expression.Convert(Expression.Property(Expression.Property(vehicle,fullRotation),rotationAngle),typeof(double)),pawn).Compile();
        headingSetCall=Expression.Lambda<Action<Pawn,double>>(Expression.Call(vehicle,fullRotation.SetMethod!,rotation),pawn,heading).Compile();
        reclaimCall=Expression.Lambda<Action<Pawn>>(Expression.Call(vehicle,reclaim),pawn).Compile();
        teleportedCall=Expression.Lambda<Action<Pawn>>(Expression.Call(vehicle,teleported,Expression.Constant(false),Expression.Constant(true)),pawn).Compile();
        var occupied=Method(VehicleType,"InhabitedCellsProjected",typeof(IEnumerable<IntVec3>),typeof(IntVec3),rot,typeof(int));
        occupiedCall=Expression.Lambda<Func<Pawn,IntVec3,double,IEnumerable<IntVec3>>>(Expression.Call(vehicle,occupied,cell,rotation,Expression.Constant(0)),pawn,cell,heading).Compile();
        var graphic=Property(VehicleType,"VehicleGraphic");
        var render=AccessTools.GetDeclaredMethods(graphic.PropertyType.BaseType!).Single(m=>m.Name=="ParallelGetPreRenderResults");
        var transformType=render.GetParameters()[0].ParameterType.GetElementType()!;
        var transform=Expression.Variable(transformType,"transform");var rendered=Expression.Variable(render.ReturnType,"rendered");
        var drawAt=Expression.Parameter(typeof(Vector3),"drawAt");
        var constructor=transformType.GetConstructor(new[]{typeof(Vector3),rot,typeof(float)})??throw new MissingMethodException("Vehicle visual transform constructor");
        var packet=Expression.New(typeof(VehicleVisual).GetConstructor(new[]{typeof(Mesh),typeof(Material),typeof(Vector3),typeof(Quaternion)})!,
            Expression.Field(rendered,Field(render.ReturnType,"mesh")),Expression.Field(rendered,Field(render.ReturnType,"material")),
            Expression.Field(rendered,Field(render.ReturnType,"position")),Expression.Field(rendered,Field(render.ReturnType,"quaternion")));
        visualCall=Expression.Lambda<Func<Pawn,Vector3,VehicleVisual>>(Expression.Block(new[]{transform,rendered},
            Expression.Assign(transform,Expression.New(constructor,drawAt,Expression.Property(vehicle,fullRotation),Expression.Constant(0f))),
            Expression.Assign(rendered,Expression.Call(Expression.Property(vehicle,graphic),render,transform,Expression.Constant(true),Expression.Convert(pawn,typeof(Thing)),Expression.Constant(0f))),packet),pawn,drawAt).Compile();
    }
    internal static FieldInfo Field(Type type,string name)=>AccessTools.Field(type,name)??throw new MissingFieldException(type.FullName,name);
    internal static PropertyInfo Property(Type type,string name)=>AccessTools.Property(type,name)??throw new MissingMemberException(type.FullName,name);
    internal static MethodInfo Method(Type type,string name,Type result,params Type[] args)
    {
        var method=AccessTools.Method(type,name,args)??throw new MissingMethodException(type.FullName,name);
        if(method.ReturnType!=result)throw new MissingMethodException(type.FullName,name+" return type");
        return method;
    }
    public VehicleVisual Visual(Pawn vehicle,Vector3 position)=>visualCall(vehicle,position);
    public bool IsVehicle(Thing thing)=>VehicleType.IsInstanceOfType(thing);
    public bool CanMove(Pawn vehicle)=>IsVehicle(vehicle)&&canMoveCall(vehicle)&&draftedCall(vehicle);
    public double Heading(Pawn vehicle)=>headingCall(vehicle);
    public IEnumerable<IntVec3> OccupiedCells(Pawn vehicle,IntVec3 position,double heading)=>occupiedCall(vehicle,position,heading);
    public void SetHeading(Pawn vehicle,double heading)=>headingSetCall(vehicle,heading);
    public VehicleHull Hull(Pawn vehicle)=>new(vehicle.def.size.x,vehicle.def.size.z,vehicle.def.GetModExtension<VehicleClearance>()?.height??3);
    internal object Pather(Pawn vehicle)=>pather.GetValue(vehicle)!;
    internal void ResetFlightInterpolation(Pawn vehicle)=>teleportedCall(vehicle);
    internal void SyncContinuousNextCell(Pawn vehicle)=>nextCell.SetValue(Pather(vehicle),vehicle.Position);
    internal IntVec3 NextCell(Pawn vehicle)=>(IntVec3)nextCell.GetValue(Pather(vehicle))!;
    internal Pawn Actor(object follower)=>(Pawn)actor.GetValue(follower)!;
    internal IReadOnlyList<IntVec3> LookAhead(Pawn vehicle,int count)
    {
        var native=currentPath.GetValue(Pather(vehicle));
        if(native==null)return Array.Empty<IntVec3>();
        var nodes=(IReadOnlyList<IntVec3>)pathNodes.GetValue(native)!;
        int current=(int)pathCurrent.GetValue(native)!;
        var snapshot=new List<IntVec3>(count);
        for(int i=Math.Min(current,nodes.Count-1);i>=0&&snapshot.Count<count;--i)snapshot.Add(nodes[i]);
        return snapshot;
    }
    public IReadOnlyList<IntVec3>? FindLeg(Pawn vehicle,IntVec3 from,LocalTargetInfo target,PathEndMode endMode)
    {
        var owner=LayerMapComponent.For(vehicle.Map);
        if(owner==null||!owner.TryDecode(from,out var origin)||!owner.TryDecode(target.Cell,out var end)||origin.LayerId!=end.LayerId)return null;
        // Do not move the real vehicle to a hypothetical endpoint to make native validation pass.
        var system=vehicle.Map.components.FirstOrDefault(pathingType.IsInstanceOfType);
        if(system==null)return null;
        using var pause=VehiclePathingExpansion.PauseWorker(system);
        var data=indexer.GetValue(system,new object[]{vehicle.def});
        var updater=regionUpdater.GetValue(data)!;
        rebuildRegions.Invoke(updater,null);
        var finder=pathFinder.GetValue(data);
        object? path=null;bool previous=FindingLeg;
        try
        {
            FindingLeg=true;
            path=findPath.Invoke(finder,new object[]{from,target,vehicle,CancellationToken.None,endMode});
            if(path==null||!(bool)pathFound.GetValue(path)!)return null;
            var nodes=((IEnumerable<IntVec3>)pathNodes.GetValue(path)!).Reverse().ToArray();
            if(nodes.Any(c=>!owner.TryDecode(c,out var at)||at.LayerId!=origin.LayerId||!owner.Structure.HasSurface(c)))return null;
            return nodes;
        }
        finally {FindingLeg=previous;if(path!=null)dispose.Invoke(path,null);}
    }
    public void StartLeg(Pawn vehicle,IntVec3 destination)=>start.Invoke(Pather(vehicle),new object[]{new LocalTargetInfo(destination),PathEndMode.OnCell,false});
    public void Stop(Pawn vehicle)=>stop.Invoke(Pather(vehicle),null);
    public void Fail(Pawn vehicle)=>fail.Invoke(Pather(vehicle),null);
    internal void CommitContinuous(Pawn vehicle,IntVec3 position,double heading)
    {
        var old=OccupiedCells(vehicle,vehicle.Position,Heading(vehicle)).ToArray();
        SetHeading(vehicle,heading);vehicle.Position=position;reclaimCall(vehicle);
        foreach(var c in old.Concat(OccupiedCells(vehicle,position,heading)).Distinct())
            if(c.InBounds(vehicle.Map))vehicle.Map.pathing.RecalculatePerceivedPathCostAt(c);
    }
    public void Commit(Pawn vehicle,IntVec3 position,double heading)
    {
        var old=OccupiedCells(vehicle,vehicle.Position,Heading(vehicle)).ToArray();
        // Native StopDead hooks may inspect speed and position: stop in the source coordinate frame.
        Stop(vehicle);
        SetHeading(vehicle,heading);
        vehicle.Position=position;
        reclaimCall(vehicle);
        teleportedCall(vehicle);
        AfterCommit?.Invoke(vehicle,heading);
        foreach(var c in old.Concat(OccupiedCells(vehicle,position,heading)).Distinct())
            if(c.InBounds(vehicle.Map))vehicle.Map.pathing.RecalculatePerceivedPathCostAt(c);
    }
}
