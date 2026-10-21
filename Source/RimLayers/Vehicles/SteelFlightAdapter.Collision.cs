using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using UnityEngine;
using Verse;
namespace RimLayers.Vehicles;

internal sealed partial class SteelFlightAdapter
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ThingComp,VehicleFlightState> contacts=new();
    private static readonly List<(ThingComp Comp,Pawn Pawn,LayerMapComponent Owner,VehicleFlightState Flight,AirSurface Surface)> pendingImpacts=new();
    private Func<object,float> verticalSpeed=null!,airSpeed=null!;
    private (Func<object,bool> Get,Action<object,bool> Set) completing,lethal;
    private Action<Pawn,DamageInfo?,DestroyMode,bool> kill=null!;
    private MethodInfo nativeKill=null!;
    [ThreadStatic] private static Pawn? impactVehicle;
    [ThreadStatic] private static Pawn? drawingWreck;
    private MethodInfo wreckDraw=null!,fuelDraw=null!;
    private Func<object,Pawn> wreckPawn=null!;
    private Action<Pawn,bool> impactEffect=null!;
    private Action<object,bool> setIgnition=null!;
    private Func<object,object> ignition=null!;
    private Func<object,bool,DamageInfo?,bool,bool> beginFailure=null!;
    private MethodInfo failureMethod=null!,synchronizeMethod=null!;
    private void BindCollision(Assembly assembly)
    {
        verticalSpeed=VehicleReflection.Call<Func<object,float>>(FrameworkVehicleAdapter.Property(CompType,"VerticalSpeedMetersPerSecond").GetMethod!);
        airSpeed=VehicleReflection.Call<Func<object,float>>(FrameworkVehicleAdapter.Property(CompType,"AirspeedCellsPerSecond").GetMethod!);
        completing=VehicleReflection.Field<bool>(CompType,"completingFailure");lethal=VehicleReflection.Field<bool>(CompType,"failureLethal");
        var wreckType=basic.VehicleType.Assembly.GetType("Vehicles.VehicleBuilding",true)!;
        wreckDraw=FrameworkVehicleAdapter.Method(wreckType,"DrawAt",typeof(void),typeof(Vector3),typeof(bool));
        wreckPawn=VehicleReflection.Read<Pawn>(wreckType,"vehicle");
        fuelDraw=FrameworkVehicleAdapter.Method(basic.VehicleType.Assembly.GetType("Vehicles.CompFueledTravel",true)!,"PostDraw",typeof(void));
        nativeKill=FrameworkVehicleAdapter.Method(basic.VehicleType,"Kill",typeof(void),typeof(DamageInfo?),typeof(DestroyMode),typeof(bool));
        kill=VehicleReflection.Call<Action<Pawn,DamageInfo?,DestroyMode,bool>>(nativeKill);
        var presentation=assembly.GetType("VehicleFramework.LocalUpgrade.VehicleFailurePresentation",true)!;
        impactEffect=VehicleReflection.Call<Action<Pawn,bool>>(FrameworkVehicleAdapter.Method(presentation,"Impact",typeof(void),basic.VehicleType,typeof(bool)));
        ignition=VehicleReflection.Read<object>(basic.VehicleType,"ignition");
        setIgnition=VehicleReflection.Call<Action<object,bool>>(FrameworkVehicleAdapter.Property(FrameworkVehicleAdapter.Field(basic.VehicleType,"ignition").FieldType,"Drafted").SetMethod!);
        failureMethod=Method("BeginFailure",typeof(bool),typeof(bool),typeof(DamageInfo?),typeof(bool));
        beginFailure=VehicleReflection.Call<Func<object,bool,DamageInfo?,bool,bool>>(failureMethod);
        synchronizeMethod=Method("SynchronizeLogicalProxyToFlightTrack",typeof(void));
    }
    private void InstallCollision()
    {
        patches.Patch(wreckDraw,prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(BeforeWreckDraw)),finalizer:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(AfterWreckDraw)));
        patches.Patch(fuelDraw,prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(WreckFuelOverlay)));
        patches.Patch(nativeKill,prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(CrashWreck)));
        patches.Patch(failureMethod,prefix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(BeforeFailure)),postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(AfterFailure)));
        patches.Patch(Method("TickFailure",typeof(void)),transpiler:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(FailureSurfaceGate)));
        patches.Patch(Method("CompTick",typeof(void)),transpiler:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(FailureSurfaceGate)));
        patches.Patch(AccessTools.Method(typeof(TickManager),nameof(TickManager.DoSingleTick)),postfix:new HarmonyMethod(typeof(SteelFlightAdapter),nameof(FlushImpacts)));
    }
    private static void BeforeFailure(ThingComp __instance,out (VehicleFlightState? Flight,double Datum,float Altitude,float Target,float Transition) __state)
    {
        __state=default;
        if(!Context(__instance,out _,out var owner,out var f)||Installed!.crashing(__instance)||Installed.completing.Get(__instance))return;
        var a=Installed;if(!a.airborne(__instance))return;
        __state=(f,f.Datum,a.altitude.Get(__instance),a.target.Get(__instance),a.transition.Get(__instance));
        // Native failure samples still drive the motion. Move its endpoint below the lowest
        // real floor so its time limit cannot complete a crash on an imaginary takeoff plane.
        double datum=Enumerable.Range(0,owner.Layout!.LayerCount).Min(owner.Elevation)*3-3,delta=f.Datum-datum;
        a.altitude.Set(__instance,(float)(__state.Altitude+delta));a.target.Set(__instance,(float)(__state.Target+delta));
        a.transition.Set(__instance,(float)(__state.Transition+delta));f.Datum=datum;f.Landing=false;
    }
    private static void AfterFailure(ThingComp __instance,bool __result,(VehicleFlightState? Flight,double Datum,float Altitude,float Target,float Transition) __state)
    {
        if(__result||__state.Flight==null)return;
        var a=Installed!;__state.Flight.Datum=__state.Datum;a.altitude.Set(__instance,__state.Altitude);
        a.target.Set(__instance,__state.Target);a.transition.Set(__instance,__state.Transition);
    }
    private static IEnumerable<CodeInstruction> FailureSurfaceGate(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
    {
        var code=instructions.ToList();int count=0;
        var sync=FrameworkVehicleAdapter.Method(code.Select(i=>i.operand).OfType<MethodInfo>().First(m=>m.Name=="SynchronizeLogicalProxyToFlightTrack").DeclaringType!,"SynchronizeLogicalProxyToFlightTrack",typeof(void));
        foreach(var instruction in code)
        {
            yield return instruction;
            if(!instruction.Calls(sync))continue;
            count++;var next=generator.DefineLabel();
            yield return new(OpCodes.Ldarg_0);
            yield return new(OpCodes.Call,AccessTools.Method(typeof(SteelFlightAdapter),nameof(ContactCompleted)));
            yield return new(OpCodes.Brfalse,next);yield return new(OpCodes.Ret);
            var resume=new CodeInstruction(OpCodes.Nop);resume.labels.Add(next);yield return resume;
        }
        if(count!=1)throw new InvalidOperationException("Native crash contact gate changed; refusing to bind.");
    }
    private static void BeforeWreckDraw(Thing __instance,out Pawn? __state)
    {__state=drawingWreck;if(LayerMapComponent.For(__instance.Map)!=null)drawingWreck=Installed!.wreckPawn(__instance);}
    private static void AfterWreckDraw(Pawn? __state)=>drawingWreck=__state;
    private static bool WreckFuelOverlay(ThingComp __instance)
    {
        // The native wreck draws its original, despawned pawn. Empty-tank overlays require
        // pawn.Map, which is null in that draw path; the wreck's own rendering still runs.
        return !ReferenceEquals(__instance.parent,drawingWreck)||__instance.parent.Map!=null;
    }
    private static void CrashWreck(Pawn __instance,ref bool spawnWreckage)
    {if(ReferenceEquals(__instance,impactVehicle))spawnWreckage=true;}
    private static bool ContactCompleted(ThingComp comp)=>contacts.TryGetValue(comp,out _);
    private static void FlushImpacts()
    {
        // Native TickAllComps iterates a cached count. ResetState/ignition may remove requested
        // comps; commit after that loop, not from inside the flight comp's callback.
        var work=pendingImpacts.ToArray();pendingImpacts.Clear();
        foreach(var p in work)if(p.Pawn.Spawned&&!p.Pawn.Destroyed&&!p.Flight.ImpactCommitted)
            Installed!.CompleteImpact(p.Comp,p.Pawn,p.Owner,p.Flight,p.Surface);
    }
    private static RampBody ClearanceBody(VehicleHull hull,RampBody pose)
    {
        double p=pose.Pitch,b=pose.Bank;
        // Native altitude is the lowest point above the flight datum. Preserve that meaning
        // while rotating the entire box (including a banked wing), rather than sinking a wing into its pad.
        double upY=Math.Cos(p)*Math.Cos(b),rightY=Math.Cos(p)*Math.Sin(b),forwardY=Math.Sin(p);
        double extent=Math.Abs(rightY)*hull.Width/2+Math.Abs(forwardY)*hull.Length/2+Math.Abs(upY)*hull.Height/2;
        return pose with{Bottom=pose.Bottom with{Height=pose.Bottom.Height+extent-upY*hull.Height/2}};
    }
    private bool ResolveCollision(ThingComp comp,Pawn pawn,LayerMapComponent owner,VehicleFlightState f,RampBody next)
    {
        if(f.ImpactCommitted)return false;
        var hull=basic.Hull(pawn);var snapshot=VehicleAirspace.Snapshot(owner,pawn,f.Previous,next);
        var from=ClearanceBody(hull,f.Previous);var to=ClearanceBody(hull,next);
        // A horizontal plane below the body's exact lowest-point altitude is separated,
        // even if conservative angular padding in the SAT envelope reaches it.
        var boxes=snapshot.Surfaces.Where(s=>!s.Horizontal||s.Bounds.Top>Math.Min(f.Previous.Bottom.Height,next.Bottom.Height)+1e-7).Select(s=>s.Bounds).ToArray();
        var hit=VehicleFlightSweep.FirstHit(hull,from,to,boxes);
        if(hit==null)return false;
        var contact=snapshot.Surfaces[hit.Value.ObstacleKey];
        // Contact with a planned landing deck is intentional; the native touchdown check
        // still verifies the complete hull and the authored landing restrictions.
        if(f.Landing&&contact.Horizontal&&Math.Abs(contact.Bounds.Top-f.Datum)<1e-5&&next.Bottom.Height>=f.Datum-1e-5)return false;
        double fraction=Math.Max(0,hit.Value.Fraction-1e-5);var body=VehicleFlightSweep.Interpolate(f.Previous,next,fraction);
        altitude.Set(comp,(float)(body.Bottom.Height-f.Datum));
        position.Set(comp,new((float)body.Bottom.X,(float)(body.Bottom.Z+f.ProxyLayer*owner.Layout!.Stride)));
        Commit(comp,pawn,owner,f,body,VehicleFlight.ProxyAt(owner,body.Bottom.Height+.001));
        f.ImpactCell=contact.Cell;f.ImpactHeight=Height(pawn);contacts.AddOrUpdate(comp,f);
        pendingImpacts.Add((comp,pawn,owner,f,contact));return true;
    }
    private void CompleteImpact(ThingComp comp,Pawn pawn,LayerMapComponent owner,VehicleFlightState f,AirSurface surface)
    {
        if(f.ImpactCommitted)return;
        f.ImpactCommitted=true;f.ImpactCell=surface.Cell;f.ImpactHeight=Height(pawn);contacts.AddOrUpdate(comp,f); // committed before damage callbacks, which can re-enter failure hooks
        float speed=MathF.Sqrt(verticalSpeed(comp)*verticalSpeed(comp)+airSpeed(comp)*airSpeed(comp));
        bool destroyed=lethal.Get(comp)||speed>=10||!VehicleSpace.CanStand(owner,pawn,pawn.Position,heading(comp),out _);
        float amount=Math.Clamp(12+speed*speed*1.5f,12,600);
        var damage=new DamageInfo(DamageDefOf.Blunt,amount,1,heading(comp),surface.Thing);
        completing.Set(comp,true);
        try
        {
            bool abortRoute=f.RouteActive;f.RouteActive=false;f.Guidance=null;
            VehicleTraffic.Command(()=>{if(abortRoute)basic.Fail(pawn);else basic.Stop(pawn);});
            float angle=heading(comp);finalizePose(comp,angle);reset(comp,true);setIgnition(ignition(pawn),false);
            f.Datum=owner.Elevation(f.ProxyLayer)*3;f.HasPrevious=false;f.Landing=false;
            if(surface.Thing is {Destroyed:false,def.destroyable:true} obstacle)obstacle.TakeDamage(new DamageInfo(DamageDefOf.Blunt,amount,1,angle,pawn));
            // Native Kill retains the framework's crew, cargo, damage and wreckage semantics.
            // A survivable controlled descent receives native component damage exactly once.
            impactVehicle=pawn;
            pawn.TakeDamage(damage);
            // Native component damage may itself call Kill. That call receives wreckage=true
            // in this scoped contact; never call Kill again on the destroyed original object.
            if(pawn.Spawned&&!pawn.Destroyed)
            {if(destroyed)kill(pawn,damage,DestroyMode.KillFinalize,true);else impactEffect(pawn,false);}
            Log.Message($"[RimLayers vehicles] Airframe contact committed once: {pawn.ThingID}, surface={surface.Cell}, speed={speed:F2}, wreck={destroyed}");
        }
        finally{impactVehicle=null;completing.Set(comp,false);}
    }
}
