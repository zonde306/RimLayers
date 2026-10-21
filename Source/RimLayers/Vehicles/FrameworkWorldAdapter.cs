using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Presentation;
using RimLayers.Recovery;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
namespace RimLayers.Vehicles;

/// <summary>Base Vehicle Framework world transport. No LocalUpgrade types or copied native state.</summary>
internal sealed class FrameworkWorldAdapter
{
    internal static FrameworkWorldAdapter? Installed {get;private set;}
    private static readonly Harmony patches=new("rimlayers.vehicles.world");
    private readonly FrameworkVehicleAdapter basic;
    private readonly Type arrivalType,worldType,skyType,targeterType,launcherType;
    private readonly ConstructorInfo arrivalConstructor;
    private readonly Func<object,Pawn> arrivalPawn,worldPawn,skyPawn;
    private readonly Func<object,MapParent> arrivalMap;
    private readonly (Func<object,IntVec3> Get,Action<object,IntVec3> Set) arrivalCell;
    private readonly (Func<object,Rot4> Get,Action<object,Rot4> Set) arrivalRotation,skyRotation;
    private readonly Func<object,Rot4> skyLandingRotation,targeterRotation;
    private readonly Func<object,object> launcher,protocol,properties,restriction;
    private readonly Func<object,Rot4?> forcedRotation;
    private readonly Func<object,object,Rot4,object> getProperties;
    private readonly Func<object,Map,IntVec3,Rot4,bool> landingRestricted;
    private readonly Func<object,object,Map,IntVec3,Rot4,bool> canStart;
    private readonly (Func<object,bool> Get,Action<object,bool> Set) inFlight;
    private readonly Action<object,Map,IntVec3,Rot4> prepare;
    private readonly Action<object,GlobalTargetInfo> arrive;
    private readonly Func<object> targeter;
    private readonly Action<object,object,Map,Action<LocalTargetInfo,Rot4>,Func<LocalTargetInfo,bool>,Action,Texture2D,bool,bool> beginTarget;
    private readonly object landingKind;
    private FrameworkWorldAdapter(FrameworkVehicleAdapter basic)
    {
        this.basic=basic;var a=basic.VehicleType.Assembly;
        Type T(string n)=>a.GetType(n,true)!;
        arrivalType=T("Vehicles.World.ArrivalAction_LandToCell");worldType=T("Vehicles.World.AerialVehicleInFlight");
        skyType=T("Vehicles.VehicleSkyfaller_Arriving");targeterType=T("Vehicles.LandingTargeter");launcherType=T("Vehicles.CompVehicleLauncher");
        if(!typeof(WorldObject).IsAssignableFrom(worldType)||!typeof(Thing).IsAssignableFrom(skyType))throw new InvalidOperationException("World holder identity mismatch.");
        arrivalConstructor=arrivalType.GetConstructor(new[]{basic.VehicleType,typeof(MapParent),typeof(IntVec3),typeof(Rot4)})??throw new MissingMethodException("LandToCell constructor");
        arrivalPawn=VehicleReflection.Read<Pawn>(arrivalType,"vehicle");worldPawn=VehicleReflection.Read<Pawn>(worldType,"vehicle");skyPawn=VehicleReflection.Read<Pawn>(skyType,"vehicle");
        arrivalMap=VehicleReflection.Read<MapParent>(arrivalType,"mapParent");arrivalCell=VehicleReflection.Field<IntVec3>(arrivalType,"landingCell");
        arrivalRotation=VehicleReflection.Field<Rot4>(arrivalType,"landingRot");skyRotation=VehicleReflection.Field<Rot4>(skyType,"rotatePostLanding");
        skyLandingRotation=VehicleReflection.Call<Func<object,Rot4>>(FrameworkVehicleAdapter.Property(skyType,"LandingRotation").GetMethod!);
        targeterRotation=VehicleReflection.Read<Rot4>(targeterType,"landingRotation");
        launcher=VehicleReflection.Call<Func<object,object>>(FrameworkVehicleAdapter.Property(basic.VehicleType,"CompVehicleLauncher").GetMethod!);
        protocol=VehicleReflection.Read<object>(launcherType,"launchProtocol");inFlight=VehicleReflection.Field<bool>(launcherType,"inFlight");
        var protocolType=T("Vehicles.LaunchProtocol");var kind=protocolType.GetNestedType("LaunchType",BindingFlags.Public|BindingFlags.NonPublic)!;landingKind=Enum.Parse(kind,"Landing");
        getProperties=VehicleReflection.Call<Func<object,object,Rot4,object>>(FrameworkVehicleAdapter.Method(protocolType,"GetProperties",T("Vehicles.LaunchProtocolProperties"),kind,typeof(Rot4)));
        properties=VehicleReflection.Call<Func<object,object>>(FrameworkVehicleAdapter.Property(protocolType,"LandingProperties").GetMethod!);
        var propsType=T("Vehicles.LaunchProtocolProperties");restriction=VehicleReflection.Read<object>(propsType,"restriction");forcedRotation=VehicleReflection.Read<Rot4?>(propsType,"forcedRotation");
        var restrictType=FrameworkVehicleAdapter.Field(propsType,"restriction").FieldType;
        canStart=VehicleReflection.Call<Func<object,object,Map,IntVec3,Rot4,bool>>(FrameworkVehicleAdapter.Method(restrictType,"CanStartProtocol",typeof(bool),basic.VehicleType,typeof(Map),typeof(IntVec3),typeof(Rot4)));
        landingRestricted=VehicleReflection.Call<Func<object,Map,IntVec3,Rot4,bool>>(FrameworkVehicleAdapter.Method(protocolType,"LandingRestricted",typeof(bool),typeof(Map),typeof(IntVec3),typeof(Rot4)));
        prepare=VehicleReflection.Call<Action<object,Map,IntVec3,Rot4>>(FrameworkVehicleAdapter.Method(protocolType,"Prepare",typeof(void),typeof(Map),typeof(IntVec3),typeof(Rot4)));
        arrive=VehicleReflection.Call<Action<object,GlobalTargetInfo>>(FrameworkVehicleAdapter.Method(arrivalType,"Arrived",typeof(void),typeof(GlobalTargetInfo)));
        targeter=VehicleReflection.Call<Func<object>>(FrameworkVehicleAdapter.Property(targeterType,"Instance").GetMethod!);
        beginTarget=VehicleReflection.Call<Action<object,object,Map,Action<LocalTargetInfo,Rot4>,Func<LocalTargetInfo,bool>,Action,Texture2D,bool,bool>>(FrameworkVehicleAdapter.Method(targeterType,"BeginTargetingAndFocusMap",typeof(void),basic.VehicleType,typeof(Map),typeof(Action<LocalTargetInfo,Rot4>),typeof(Func<LocalTargetInfo,bool>),typeof(Action),typeof(Texture2D),typeof(bool),typeof(bool)));
    }
    internal static bool TryInstall(FrameworkVehicleAdapter basic,out string reason)
    {
        try
        {
            var a=new FrameworkWorldAdapter(basic);
            void Pre(Type type,string name,string hook,params Type[] args)=>patches.Patch(FrameworkVehicleAdapter.Method(type,name,typeof(void),args),prefix:new HarmonyMethod(typeof(FrameworkWorldAdapter),hook){priority=Priority.First});
            patches.Patch(a.arrivalConstructor,postfix:new HarmonyMethod(typeof(FrameworkWorldAdapter),nameof(CaptureDestination)));
            Pre(a.arrivalType,"Arrived",nameof(BeforeArrival),typeof(GlobalTargetInfo));
            Pre(a.worldType,"MoveForward",nameof(AllowWorldMovement));Pre(a.worldType,"InitializeNextFlight",nameof(AllowWorldMovement),typeof(Vector3));
            Pre(a.worldType,"ResumePathPostLoad",nameof(AllowWorldMovement));
            var launch=AccessTools.GetDeclaredMethods(a.launcherType).Single(m=>m.Name=="Launch"&&m.GetParameters().Length==2);
            if(launch.GetParameters()[1].ParameterType.FullName!="Vehicles.World.IArrivalAction")throw new MissingMethodException("World launch signature mismatch.");
            patches.Patch(launch,prefix:new HarmonyMethod(typeof(FrameworkWorldAdapter),nameof(BeforeLaunch)));
            patches.Patch(AccessTools.GetDeclaredMethods(a.worldType).Single(m=>m.Name=="Launch"&&m.GetParameters().Length==2),prefix:new HarmonyMethod(typeof(FrameworkWorldAdapter),nameof(WorldRelaunch)));
            patches.Patch(FrameworkVehicleAdapter.Method(a.worldType,"GetGizmos",typeof(IEnumerable<Gizmo>)),postfix:new HarmonyMethod(typeof(FrameworkWorldAdapter),nameof(WorldGizmos)));
            Pre(a.targeterType,"BeginTargeting",nameof(Targeting),basic.VehicleType,typeof(Map),typeof(Action<LocalTargetInfo,Rot4>),typeof(Func<LocalTargetInfo,bool>),typeof(Action),typeof(Action),typeof(Texture2D),typeof(bool),typeof(bool));
            Pre(a.skyType,"Tick",nameof(SkyTick));Pre(a.skyType,"TryHitRoof",nameof(NoRoofPunch));
            patches.Patch(FrameworkVehicleAdapter.Method(a.skyType,"FinalizeLanding",typeof(void)),prefix:new HarmonyMethod(typeof(FrameworkWorldAdapter),nameof(BeforeTouchdown)),postfix:new HarmonyMethod(typeof(FrameworkWorldAdapter),nameof(AfterTouchdown)));
            patches.Patch(FrameworkVehicleAdapter.Method(a.skyType,"SpawnSetup",typeof(void),typeof(Map),typeof(bool)),postfix:new HarmonyMethod(typeof(FrameworkWorldAdapter),nameof(SkySpawned)));
            Installed=a;reason="Base world arrival and departure adapter installed.";return true;
        }
        catch(Exception e){patches.UnpatchAll(patches.Id);Installed=null;reason="World travel disabled: "+e;return false;}
    }
    internal bool Clear(Pawn pawn,Map map,IntVec3 cell,Rot4 rotation,out string reason,Thing? ignore=null,bool landing=true)
    {
        reason="RL_VehicleLandingUnavailable";
        if(LayerMapComponent.For(map) is not {} owner)return true;
        if(!VehicleSpace.CanStand(owner,pawn,cell,rotation.AsAngle,out reason,ignoreThing:ignore))return false;
        var pose=VehicleSpace.Pose(owner,pawn,cell,rotation.AsAngle);double top=Enumerable.Range(0,owner.Layout!.LayerCount).Max(owner.Elevation)*3+basic.Hull(pawn).Height+3;
        var entry=new RampBody(pose.Position with{Height=pose.Position.Height+.001},pose.Heading,0);
        var to=entry with{Bottom=entry.Bottom with{Height=top}};
        var snapshot=VehicleAirspace.Snapshot(owner,pawn,entry,to);
        var boxes=snapshot.Surfaces.Where(s=>(ignore==null||s.Thing!=ignore)&&(!s.Horizontal||s.Bounds.Top>pose.Position.Height+1e-6)).Select(s=>s.Bounds).ToArray();
        if(VehicleFlightSweep.FirstHit(basic.Hull(pawn),entry,to,boxes)!=null){reason="RL_VehicleAirCorridorBlocked";return false;}
        if(landing)
        {
            var p=protocol(launcher(pawn));var props=getProperties(p,landingKind,rotation);var r=props==null?null:restriction(props);
            if(landingRestricted(p,map,cell,rotation)||(r!=null&&!canStart(r,pawn,map,cell,rotation))){reason="RL_VehicleLandingRestriction";return false;}
        }
        reason="";return true;
    }
    private Rot4 EffectiveRotation(Pawn pawn,Rot4 rotation)
    {var props=properties(protocol(launcher(pawn)));return props==null?rotation:forcedRotation(props)??rotation;}
    private static void CaptureDestination(object __instance)
    {var a=Installed;if(a==null||Verse.Current.Game==null)return;VehicleWorldTravel.Current.DestinationFor(a.arrivalPawn(__instance),a.arrivalMap(__instance),a.arrivalCell.Get(__instance),a.arrivalRotation.Get(__instance));}
    private static bool BeforeLaunch(ThingComp __instance)
    {
        if(Installed is not {} a||__instance.parent is not Pawn pawn||!pawn.Spawned||LayerMapComponent.For(pawn.Map) is not {} owner)return true;
        if(!a.Clear(pawn,pawn.Map,pawn.Position,pawn.Rotation,out var reason,landing:false))
        {Messages.Message(reason.Translate(),pawn,MessageTypeDefOf.RejectInput,false);return false;}
        owner.TryDecode(pawn.Position,out var at);var s=VehicleWorldTravel.Current.Ensure(pawn);
        s.Origin=pawn.Map.Parent;s.OriginLayer=at.LayerId;s.OriginX=at.X;s.OriginZ=at.Z;s.Waiting=false;
        RecoveryCompatibility.DeferFor(pawn,120);return true;
    }
    private static bool BeforeArrival(object __instance)
    {
        var a=Installed!;var pawn=a.arrivalPawn(__instance);var parent=a.arrivalMap(__instance);
        var s=VehicleWorldTravel.Current.State(pawn);
        if(s?.HasDestination!=true){CaptureDestination(__instance);s=VehicleWorldTravel.Current.State(pawn);}
        if(s?.HasDestination!=true)return true;
        string reason="RL_VehicleLandingUnavailable";
        if(parent!=s.Destination||parent?.HasMap!=true||LayerMapComponent.For(parent.Map) is not {} owner||!owner.Layout!.Contains(new(s.Layer,s.X,s.Z)))
        {a.Hold(s,reason);return false;}
        var cell=owner.ToStorage(new(s.Layer,s.X,s.Z));var rot=a.EffectiveRotation(pawn,s.Rotation);
        if(!a.Clear(pawn,parent.Map,cell,rot,out reason)){a.Hold(s,reason);return false;}
        a.arrivalCell.Set(__instance,cell);a.arrivalRotation.Set(__instance,s.Rotation);s.Waiting=false;return true;
    }
    private void Hold(VehicleWorldIntent s,string reason)
    {
        bool notify=!s.Waiting;s.Waiting=true;s.Reason=reason;
        inFlight.Set(launcher(s.Vehicle),false);
        if(notify)Messages.Message("RL_VehicleLandingHeld".Translate(s.Vehicle.LabelCap,reason.Translate()),MessageTypeDefOf.RejectInput,false);
    }
    private static bool AllowWorldMovement(object __instance)=>VehicleWorldTravel.Current.State(Installed!.worldPawn(__instance))?.Waiting!=true;
    private static void WorldRelaunch(object __instance)
    {if(VehicleWorldTravel.Current.State(Installed!.worldPawn(__instance)) is {} s){s.Waiting=false;s.Skyfaller=null;}}
    private static void Targeting(object __instance,Pawn vehicle,Map map,ref Func<LocalTargetInfo,bool>? targetValidator)
    {
        if(LayerMapComponent.For(map)==null)return;
        var original=targetValidator;var a=Installed!;
        targetValidator=cell=>(original?.Invoke(cell)??true)&&a.Clear(vehicle,map,cell.Cell,a.targeterRotation(__instance),out _);
    }
    private static void SkySpawned(Thing __instance)
    {
        var a=Installed!;if(LayerMapComponent.For(__instance.Map)==null)return;
        var pawn=a.skyPawn(__instance);var s=VehicleWorldTravel.Current.Ensure(pawn);
        if(!s.HasDestination)VehicleWorldTravel.Current.DestinationFor(pawn,__instance.Map.Parent,__instance.Position,a.skyLandingRotation(__instance));
        s.Skyfaller=__instance;
    }
    private static bool SkyTick(Thing __instance)
    {
        if(LayerMapComponent.For(__instance.Map)==null)return true;
        var a=Installed!;var pawn=a.skyPawn(__instance);var s=VehicleWorldTravel.Current.State(pawn);
        if(s?.Waiting==true)return false;
        RecoveryCompatibility.DeferFor(pawn,3);return true;
    }
    private static bool NoRoofPunch(Thing __instance)=>LayerMapComponent.For(__instance.Map)==null;
    private static bool BeforeTouchdown(Thing __instance,out Pawn __state)
    {
        var a=Installed!;__state=a.skyPawn(__instance);
        if(LayerMapComponent.For(__instance.Map)==null)return true;
        if(a.Clear(__state,__instance.Map,__instance.Position,a.skyLandingRotation(__instance),out var reason,__instance))return true;
        var s=VehicleWorldTravel.Current.Ensure(__state);s.Skyfaller=__instance;a.Hold(s,reason);return false;
    }
    private static void AfterTouchdown(Pawn __state)
    {
        if(!__state.Spawned||LayerMapComponent.For(__state.Map)==null)return;
        if(VehicleWorldTravel.Current.State(__state) is {} s){s.Waiting=false;s.HasDestination=false;s.Skyfaller=null;}
        __state.Map.GetComponent<VehicleFlight>().Begin(__state);RecoveryCompatibility.DeferFor(__state,120);
    }
    private static void WorldGizmos(object __instance,ref IEnumerable<Gizmo> __result)
    {
        if(VehicleWorldTravel.Current.State(Installed!.worldPawn(__instance)) is not {Waiting:true} s)return;
        __result=__result.Concat(new[]{new Command_Action{defaultLabel="RL_VehicleChooseLanding".Translate(),defaultDesc="RL_VehicleLandingHeld".Translate(s.Vehicle.LabelCap,s.Reason.Translate()),action=()=>Installed.ChooseAgain(s)}});
    }
    internal void ChooseAgain(VehicleWorldIntent s)
    {
        if(s.Destination?.HasMap!=true){Messages.Message("RL_VehicleLandingUnavailable".Translate(),MessageTypeDefOf.RejectInput,false);return;}
        var map=s.Destination.Map;Current.Game.CurrentMap=map;
        if(LayerMapComponent.For(map) is {} owner&&owner.Layout!.Contains(new(s.Layer,s.X,s.Z)))LayerView.Switch(owner,s.Layer);
        beginTarget(targeter(),s.Vehicle,map,(cell,rot)=>Retry(s,cell.Cell,rot),_=>true,null!,null!,true,false);
    }
    internal bool Retry(VehicleWorldIntent s,IntVec3 cell,Rot4 rotation)
    {
        if(s.Destination?.HasMap!=true)return false;var map=s.Destination.Map;
        if(!Clear(s.Vehicle,map,cell,EffectiveRotation(s.Vehicle,rotation),out var reason,s.Skyfaller))
        {Hold(s,reason);return false;}
        VehicleWorldTravel.Current.DestinationFor(s.Vehicle,s.Destination,cell,rotation);
        if(s.Skyfaller is {Spawned:true} sky)
        {
            sky.Position=cell;sky.Rotation=EffectiveRotation(s.Vehicle,rotation);skyRotation.Set(sky,rotation);
            prepare(protocol(launcher(s.Vehicle)),map,cell,sky.Rotation);s.Waiting=false;
        }
        else
        {
            var action=arrivalConstructor.Invoke(new object[]{s.Vehicle,s.Destination,cell,rotation});
            arrive(action,new GlobalTargetInfo(s.Destination));
        }
        return !s.Waiting;
    }
}
