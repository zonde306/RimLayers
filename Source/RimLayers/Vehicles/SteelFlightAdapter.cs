using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Presentation;
using RimLayers.Recovery;
using UnityEngine;
using Verse;
namespace RimLayers.Vehicles;

/// <summary>Steel Horizon retains its flight dynamics; RimLayers owns the logical frame.
/// Altitude is relative to a persistent takeoff/landing datum, never the current proxy band.</summary>
internal sealed partial class SteelFlightAdapter:IVehicleFlightAdapter
{
    internal static SteelFlightAdapter? Installed {get;private set;}
    private static readonly Harmony patches=new("rimlayers.vehicles.steel.flight");
    private readonly FrameworkVehicleAdapter basic;
    internal readonly Type CompType;
    private readonly Func<object,bool> airborne,eligible,manual,crashing;
    private readonly Func<object,float> heading,pitch,bank;
    private readonly (Func<object,float> Get,Action<object,float> Set) altitude,target,transition;
    private readonly (Func<object,Vector2> Get,Action<object,Vector2> Set) position,origin,loiter,holding;
    private readonly Func<object,string> stateName;
    private readonly Action<object> ensure;
    private readonly Func<object,object> statHandler,movement;
    private readonly Func<object,object,float> abstractStat,efficiency;
    private readonly Func<object,bool> emptyTank,enoughOperators;
    private readonly MethodInfo canDirect;
    private readonly Func<object,object> fuelComp;
    private readonly object moveStat;
    private readonly MethodInfo lostPower;
    private readonly Action<object,float> finalizePose;
    private readonly Action<object,bool> reset;
    private readonly Func<object,Vector3> groundPosition;
    [ThreadStatic] private static bool committing;
    private SteelFlightAdapter(Assembly assembly,FrameworkVehicleAdapter basic)
    {
        this.basic=basic;
        var statType=basic.VehicleType.Assembly.GetType("Vehicles.VehicleStatDef",true)!;
        var defType=basic.VehicleType.Assembly.GetType("Vehicles.VehicleDef",true)!;
        var statOf=basic.VehicleType.Assembly.GetType("Vehicles.VehicleStatDefOf",true)!;
        moveStat=FrameworkVehicleAdapter.Field(statOf,"MoveSpeed").GetValue(null)!;
        abstractStat=VehicleReflection.Call<Func<object,object,float>>(FrameworkVehicleAdapter.Method(basic.VehicleType.Assembly.GetType("Vehicles.Ext_Vehicles",true)!,"GetStatValueAbstract",typeof(float),defType,statType));
        statHandler=VehicleReflection.Read<object>(basic.VehicleType,"statHandler");movement=VehicleReflection.Read<object>(basic.VehicleType,"movementStatus");
        var handlerType=FrameworkVehicleAdapter.Field(basic.VehicleType,"statHandler").FieldType;
        efficiency=VehicleReflection.Call<Func<object,object,float>>(FrameworkVehicleAdapter.Method(handlerType,"StatEfficiency",typeof(float),statType));
        var fuelProperty=FrameworkVehicleAdapter.Property(basic.VehicleType,"CompFueledTravel");
        fuelComp=VehicleReflection.Call<Func<object,object>>(fuelProperty.GetMethod!);
        emptyTank=VehicleReflection.Call<Func<object,bool>>(FrameworkVehicleAdapter.Property(fuelProperty.PropertyType,"EmptyTank").GetMethod!);
        lostPower=FrameworkVehicleAdapter.Method(assembly.GetType("VehicleFramework.LocalUpgrade.VehicleFailureMotion",true)!,"LostPower",typeof(bool),basic.VehicleType);
        if(assembly.GetName().Name!="VehicleFramework.LocalUpgrade")throw new InvalidOperationException("Unexpected flight provider assembly.");
        CompType=assembly.GetType("VehicleFramework.LocalUpgrade.CompVehicleLocalFlight",true)!;
        if(!typeof(ThingComp).IsAssignableFrom(CompType))throw new InvalidOperationException("Flight comp identity mismatch.");
        Func<object,T> Property<T>(string n)=>VehicleReflection.Call<Func<object,T>>(FrameworkVehicleAdapter.Property(CompType,n).GetMethod!);
        airborne=Property<bool>("IsAirborne");eligible=Property<bool>("Eligible");crashing=Property<bool>("IsCrashing");
        heading=Property<float>("VisualHeading");pitch=Property<float>("PitchAngle");bank=Property<float>("BankAngle");
        var stateProperty=FrameworkVehicleAdapter.Property(CompType,"State");
        if(!stateProperty.PropertyType.IsEnum)throw new InvalidOperationException("Unknown flight state contract.");
        var getState=VehicleReflection.Call<Func<object,object>>(stateProperty.GetMethod!);stateName=o=>getState(o).ToString()!;
        altitude=VehicleReflection.Field<float>(CompType,"altitudeMeters");target=VehicleReflection.Field<float>(CompType,"targetAltitudeMeters");
        transition=VehicleReflection.Field<float>(CompType,"transitionStartAltitudeMeters");
        position=VehicleReflection.Field<Vector2>(CompType,"flightGroundPosition");origin=VehicleReflection.Field<Vector2>(CompType,"takeoffGroundOrigin");
        loiter=VehicleReflection.Field<Vector2>(CompType,"loiterCenter");holding=VehicleReflection.Field<Vector2>(CompType,"holdingOffset");
        ensure=VehicleReflection.Call<Action<object>>(Method("EnsureFlightGroundCoordinate",typeof(void)));
        projection=VehicleReflection.Call<Func<object,float,Vector3>>(Method("AltitudeProjectionOffsetFor",typeof(Vector3),typeof(float)));
        groundPosition=VehicleReflection.Call<Func<object,Vector3>>(Method("GroundTrackPosition",typeof(Vector3)));
        finalizePose=VehicleReflection.Call<Action<object,float>>(Method("FinalizeTouchdownPose",typeof(void),typeof(float)));
        reset=VehicleReflection.Call<Action<object,bool>>(Method("ResetState",typeof(void),typeof(bool)));
        var control=assembly.GetType("VehicleFramework.LocalUpgrade.AircraftDirectControlUtility",true)!;
        canDirect=FrameworkVehicleAdapter.Method(control,"CanActivate",typeof(bool),CompType,typeof(string).MakeByRefType());
        enoughOperators=VehicleReflection.Call<Func<object,bool>>(FrameworkVehicleAdapter.Property(basic.VehicleType,"HasEnoughOperators").GetMethod!);
        BindCollision(assembly);BindNavigation();
        manual=VehicleReflection.Call<Func<object,bool>>(FrameworkVehicleAdapter.Method(control,"IsActive",typeof(bool),CompType));
    }
    private MethodInfo Method(string name,Type result,params Type[] args)=>FrameworkVehicleAdapter.Method(CompType,name,result,args);
    internal ThingComp? Comp(Pawn p)=>p.AllComps.FirstOrDefault(CompType.IsInstanceOfType);
    public bool Active(Pawn p)=>Comp(p) is {} c&&eligible(c)&&stateName(c)!="Grounded";
    public double Height(Pawn p)=>p.Map.GetComponent<VehicleFlight>().Ensure(p).Datum+altitude.Get(Comp(p)!);
    public string State(Pawn p)=>stateName(Comp(p)!);
    public bool Manual(Pawn p)=>Comp(p) is {} c&&manual(c);
    internal void SetTargetHeight(Pawn p,double absolute)=>target.Set(Comp(p)!,(float)(absolute-p.Map.GetComponent<VehicleFlight>().Ensure(p).Datum));
    internal static bool TryInstall(Assembly assembly,FrameworkVehicleAdapter basic,out string diagnostic)
    {
        try
        {
            var a=new SteelFlightAdapter(assembly,basic);
            void Prefix(string n,Type result,string hook,params Type[] args)=>patches.Patch(a.Method(n,result,args),prefix:new HarmonyMethod(typeof(SteelFlightAdapter),hook){priority=Priority.First});
            patches.Patch(a.canDirect,postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(DirectCanActivate)));
            patches.Patch(a.lostPower,postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(LostPower)));
            Prefix("BeginTakeoff",typeof(void),nameof(BeforeTakeoff));
            Prefix("BeginLanding",typeof(void),nameof(BeforeLanding));
            Prefix("CompTick",typeof(void),nameof(BeforeTick));
            Prefix("SynchronizeLogicalProxyToFlightTrack",typeof(void),nameof(Synchronize));
            Prefix("SetFlightGroundPosition",typeof(void),nameof(ConstrainGround),typeof(Vector2));
            Prefix("SafeDirectHeading",typeof(float),nameof(SafeHeading),typeof(float),typeof(Vector3),typeof(Map),typeof(int));
            patches.Patch(a.Method("TryValidateLanding",typeof(bool),typeof(string).MakeByRefType()),postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(ValidateLanding)));
            patches.Patch(a.Method("TryValidateTakeoff",typeof(bool),typeof(string).MakeByRefType()),postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(ValidateTakeoff)));
            patches.Patch(a.Method("AltitudeVisualOffset",typeof(Vector3)),prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(VisualOffset)));
            a.InstallCollision();a.InstallNavigation();a.InstallPresentation();
            Installed=a;diagnostic="Native continuous flight coordinate adapter installed.";return true;
        }
        catch(Exception e){patches.UnpatchAll(patches.Id);Installed=null;diagnostic="Continuous flight disabled: "+e;return false;}
    }
    private static bool Context(ThingComp comp,out Pawn pawn,out LayerMapComponent owner,out VehicleFlightState flight)
    {
        pawn=comp?.parent as Pawn??null!;owner=LayerMapComponent.For(pawn?.Map)!;flight=null!;
        if(Installed==null||pawn?.Spawned!=true||owner==null||!Installed.eligible(comp!))return false;
        flight=pawn.Map.GetComponent<VehicleFlight>().Ensure(pawn);return true;
    }
    private static void LostPower(Pawn vehicle,ref bool __result)
    {
        var a=Installed;
        if(!__result||a==null||!vehicle.Spawned||LayerMapComponent.For(vehicle.Map)==null||a.Comp(vehicle) is not {} comp||!a.eligible(comp))return;
        // Older aircraft deliberately have zero taxi speed. That authored restriction is not
        // an engine failure; preserve native empty-tank, offline and component-efficiency checks.
        if(a.abstractStat(vehicle.def,a.moveStat)>0.1f)return;
        var fuel=a.fuelComp(vehicle);
        __result=(fuel!=null&&a.emptyTank(fuel))||a.movement(vehicle).ToString()!="Online"||a.efficiency(a.statHandler(vehicle),a.moveStat)<=0.1f;
    }
    private static void DirectCanActivate(ThingComp flight,ref string reason,ref bool __result)
    {
        if(__result||!Context(flight,out var pawn,out _,out _))return;
        var a=Installed!;
        // Only the zero-taxi-speed authored aircraft case. Keep the native flight state,
        // ignition, operators, fuel and component health requirements intact.
        if(a.abstractStat(pawn.def,a.moveStat)>.1f||!pawn.Drafted||a.stateName(flight)!="Airborne"||!a.enoughOperators(pawn))return;
        bool failed=true;LostPower(pawn,ref failed);
        if(!failed){__result=true;reason=null!;}
    }
    private static void BeforeTakeoff(ThingComp __instance)
    {contacts.Remove(__instance);if(Context(__instance,out var pawn,out _,out _))pawn.Map.GetComponent<VehicleFlight>().Begin(pawn);}
    private static void BeforeLanding(ThingComp __instance)
    {
        if(!Context(__instance,out _,out var owner,out var f))return;
        var a=Installed!;double datum=owner.Elevation(f.ProxyLayer)*3,delta=f.Datum-datum;
        a.altitude.Set(__instance,(float)(a.altitude.Get(__instance)+delta));
        a.target.Set(__instance,(float)(a.target.Get(__instance)+delta));
        a.transition.Set(__instance,(float)(a.transition.Get(__instance)+delta));f.Datum=datum;f.Landing=true;
    }
    private static void BeforeTick(ThingComp __instance)
    {
        if(!Context(__instance,out var pawn,out var owner,out var f)||!Installed!.eligible(__instance))return;
        if(Installed.stateName(__instance)=="Grounded")return;
        RecoveryCompatibility.DeferFor(pawn,3);Installed.ensure(__instance);
        f.Previous=Installed.Body(__instance,owner,f);f.HasPrevious=true;
        Installed.UpdateRoute(__instance,pawn,owner,f);
    }
    internal RampBody Body(object comp,LayerMapComponent owner,VehicleFlightState f)
    {
        var pos=position.Get(comp);double p=pitch(comp)*Math.PI/180;
        return new(new(pos.x,f.Datum+altitude.Get(comp),pos.y-f.ProxyLayer*owner.Layout!.Stride),heading(comp),p,bank(comp)*Math.PI/180);
    }
    private static bool Synchronize(ThingComp __instance)
    {
        if(committing||!Context(__instance,out var pawn,out var owner,out var f))return true;
        var a=Installed!;if(a.stateName(__instance)=="Grounded")return true;
        if(a.stateName(__instance)=="Spooling")return true;
        a.ensure(__instance);var body=a.Body(__instance,owner,f);
        if(f.HasPrevious&&a.ResolveCollision(__instance,pawn,owner,f,body))return false;
        int layer=VehicleFlight.ProxyAt(owner,body.Bottom.Height);
        // During the final centimetres of landing keep the chosen touchdown plane.
        if(f.Landing&&a.stateName(__instance)=="Landing")layer=VehicleFlight.ProxyAt(owner,f.Datum);
        bool ownsTrack=a.manual(__instance)||a.fixedWing(__instance)||a.crashing(__instance)||f.RouteActive||layer!=f.ProxyLayer;
        if(ownsTrack)a.Commit(__instance,pawn,owner,f,body,layer);
        a.CompleteRoute(__instance,pawn,owner,f,body);
        return !ownsTrack;
    }
    private void Commit(object comp,Pawn pawn,LayerMapComponent owner,VehicleFlightState f,RampBody body,int layer)
    {
        committing=true;
        try
        {
            int oldLayer=f.ProxyLayer;float shift=(layer-oldLayer)*owner.Layout!.Stride;
            if(shift!=0)
            {
                position.Set(comp,position.Get(comp)+new Vector2(0,shift));
                origin.Set(comp,origin.Get(comp)+new Vector2(0,shift));loiter.Set(comp,loiter.Get(comp)+new Vector2(0,shift));
                // Discard stale storage-band path and tween, in its old frame; preserve the actual Job.
                VehicleTraffic.Command(()=>basic.Stop(pawn));
            }
            owner.TryDecode(pawn.Position,out var oldLocal);
            var anchor=VehicleSpace.Pose(owner,pawn,pawn.Position,body.Yaw).Position;
            double offsetX=anchor.X-oldLocal.X,offsetZ=anchor.Z-oldLocal.Z;
            var cell=owner.ToStorage(new(layer,Math.Clamp((int)Math.Floor(body.Bottom.X-offsetX+.5),0,owner.Layout.Width-1),Math.Clamp((int)Math.Floor(body.Bottom.Z-offsetZ+.5),0,owner.Layout.Height-1)));
            basic.CommitContinuous(pawn,cell,body.Yaw);basic.SyncContinuousNextCell(pawn);f.ProxyLayer=layer;
            if(shift!=0)VehicleTraffic.Command(()=>basic.ResetFlightInterpolation(pawn));
            // Derive interpolation from the same continuous position, not the storage-band delta.
            var proxy=pawn.DrawPos;var pos=position.Get(comp);holding.Set(comp,new(pos.x-proxy.x,pos.y-proxy.z));
            if(shift!=0&&manual(comp))LayerView.Switch(owner,layer);
            if(shift!=0&&f.RouteActive)StartNativeRoute(pawn,owner,f);
        }
        finally {committing=false;}
    }
    private static void ConstrainGround(ThingComp __instance,ref Vector2 position)
    {
        if(!Context(__instance,out var pawn,out var owner,out var f))return;
        var hull=Installed!.basic.Hull(pawn);float radius=(float)Math.Sqrt(hull.Width*hull.Width+hull.Length*hull.Length)/2+.01f;
        position.x=Math.Clamp(position.x,radius,owner.Layout!.Width-radius);
        position.y=Math.Clamp(position.y,f.ProxyLayer*owner.Layout.Stride+radius,f.ProxyLayer*owner.Layout.Stride+owner.Layout.Height-radius);
    }
    private static bool SafeHeading(ThingComp __instance,float requestedHeading,Vector3 origin,Map map,int margin,ref float __result)
    {
        if(!Context(__instance,out _,out var owner,out var f))return true;
        float z=origin.z-f.ProxyLayer*owner.Layout!.Stride;
        double angle=requestedHeading*Math.PI/180;float distance=Math.Max(12,margin*3);
        var x=origin.x+Math.Sin(angle)*distance;var nextZ=z+Math.Cos(angle)*distance;
        __result=x>=margin&&x<owner.Layout.Width-margin&&nextZ>=margin&&nextZ<owner.Layout.Height-margin?requestedHeading:
            (float)(Math.Atan2(owner.Layout.Width*.5-origin.x,owner.Layout.Height*.5-z)*180/Math.PI+360)%360;
        return false;
    }
    private static void ValidateLanding(ThingComp __instance,ref string reason,ref bool __result)
    {
        if(!__result||!Context(__instance,out var pawn,out var owner,out _))return;
        __result=VehicleSpace.CanStand(owner,pawn,pawn.Position,Installed!.heading(__instance),out reason);
    }
    private static void ValidateTakeoff(ThingComp __instance,ref string reason,ref bool __result)
    {
        if(!__result||!Context(__instance,out var pawn,out var owner,out _))return;
        __result=VehicleSpace.CanStand(owner,pawn,pawn.Position,Installed!.heading(__instance),out reason);
    }
    private static bool VisualOffset(ThingComp __instance,ref Vector3 __result)
    {
        if(!Context(__instance,out _,out var owner,out var f))return true;
        // The framework projects height relative to the floor that owns this rendered proxy.
        float height=(float)Math.Max(0,f.Datum+Installed!.altitude.Get(__instance)-owner.Elevation(f.ProxyLayer)*3);
        __result=Installed.Projection(__instance,height);return false;
    }
    private Vector3 Projection(object comp,float height)
    {
        return projection(comp,height);
    }
    private readonly Func<object,float,Vector3> projection;
}
