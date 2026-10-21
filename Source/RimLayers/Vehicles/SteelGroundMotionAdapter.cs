using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimLayers.Vehicles;

/// <summary>Optional Steel Horizon state synchronization. No extension types enter the shared contracts.</summary>
internal sealed class SteelGroundMotionAdapter
{
    private readonly Type motionType;
    private readonly Action<ThingComp,Vector3,float> begin;
    private readonly Func<ThingComp,bool> driving;
    private readonly Func<Pawn,Vector3> trueCenter;
    private readonly Func<ThingComp,float> payment;
    private readonly FieldInfo remaining;
    private readonly FrameworkVehicleAdapter basic;
    private static SteelGroundMotionAdapter? installed;
    private static readonly Harmony patches=new("rimlayers.vehicles.steel.ground");

    private SteelGroundMotionAdapter(Assembly extension,FrameworkVehicleAdapter basic)
    {
        this.basic=basic;
        if(extension.GetName().Name!="VehicleFramework.LocalUpgrade")
            throw new InvalidOperationException("Unexpected Steel Horizon enhancement assembly.");
        motionType=extension.GetType("VehicleFramework.LocalUpgrade.CompVehicleGroundMotion",true)!;
        if(!typeof(ThingComp).IsAssignableFrom(motionType))
            throw new InvalidOperationException("Ground motion component is not a current ThingComp.");
        var start=FrameworkVehicleAdapter.Method(motionType,"Begin",typeof(void),typeof(Vector3),typeof(float));
        var pathDriving=FrameworkVehicleAdapter.Property(motionType,"PathDriving");
        var pathPayment=FrameworkVehicleAdapter.Property(motionType,"PathPayment");
        remaining=FrameworkVehicleAdapter.Field(basic.PatherType,"nextCellCostLeft");
        if(remaining.FieldType!=typeof(float))throw new InvalidOperationException("Unexpected native path cost type.");
        var pawn=Expression.Parameter(typeof(Pawn),"pawn");
        var center=FrameworkVehicleAdapter.Method(basic.VehicleType,"TrueCenter",typeof(Vector3));
        trueCenter=Expression.Lambda<Func<Pawn,Vector3>>(Expression.Call(Expression.Convert(pawn,basic.VehicleType),center),pawn).Compile();
        var comp=Expression.Parameter(typeof(ThingComp),"comp");
        var position=Expression.Parameter(typeof(Vector3),"position");
        var heading=Expression.Parameter(typeof(float),"heading");
        var converted=Expression.Convert(comp,motionType);
        begin=Expression.Lambda<Action<ThingComp,Vector3,float>>(Expression.Call(converted,start,position,heading),comp,position,heading).Compile();
        driving=Expression.Lambda<Func<ThingComp,bool>>(Expression.Property(converted,pathDriving),comp).Compile();
        payment=Expression.Lambda<Func<ThingComp,float>>(Expression.Property(converted,pathPayment),comp).Compile();
    }

    internal static SteelGroundMotionAdapter? TryInstall(Assembly extension,FrameworkVehicleAdapter basic,out string diagnostic)
    {
        try
        {
            var result=new SteelGroundMotionAdapter(extension,basic);
            patches.Patch(FrameworkVehicleAdapter.Method(basic.PatherType,"PatherTick",typeof(void)),
                postfix:new HarmonyMethod(typeof(SteelGroundMotionAdapter),nameof(FinishSubnormalStep)){priority=Priority.Last});
            installed=result;
            diagnostic="Steel ground pose synchronization installed.";
            return result;
        }
        catch(Exception e)
        {
            patches.UnpatchAll(patches.Id);installed=null;
            diagnostic="Steel ground enhancement disabled: "+e;
            return null;
        }
    }

    internal void Rebase(Pawn pawn,double heading)
    {
        var comp=pawn.AllComps.FirstOrDefault(motionType.IsInstanceOfType);
        // Begin resets path anchors and render interpolation without sweeping the storage-band displacement.
        if(comp!=null)begin(comp,trueCenter(pawn),(float)heading);
    }

    private static void FinishSubnormalStep(object __instance)
    {
        var adapter=installed;if(adapter==null)return;
        float left=(float)adapter.remaining.GetValue(__instance)!;
        // Observed with 4.9.2 + VVE Bang Bus: payment and remaining cost both reach float.Epsilon,
        // but the native tick never consumes that remainder. This is < 1e-38 tick of movement.
        // Require the motion component to have explicitly paid the entire remainder.
        if(left<=0||left>=1.17549435E-38f)return;
        var pawn=adapter.basic.Actor(__instance);
        var comp=pawn.AllComps.FirstOrDefault(adapter.motionType.IsInstanceOfType);
        if(comp!=null&&adapter.driving(comp)&&adapter.payment(comp)>=left)
            adapter.remaining.SetValue(__instance,0f);
    }
}
