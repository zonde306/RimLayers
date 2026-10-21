using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Combat;

/// <summary>Record the native decision before its caller can save or commit Impact.</summary>
[HarmonyPatch(typeof(CompProjectileInterceptor),nameof(CompProjectileInterceptor.CheckIntercept))]
internal static class ShieldDecisionReceiptPatch
{
    [ThreadStatic] private static (LayerFlight Flight,CompProjectileInterceptor Shield)? current;

    internal static bool Check(LayerFlight flight,CompProjectileInterceptor shield,Vector3 from,Vector3 to)
    {
        var previous=current;
        current=(flight,shield);
        try
        {
            bool intercepted=shield.CheckIntercept(flight.Projectile,from,to);
            flight.ShieldCommitted=intercepted;
            return intercepted;
        }
        finally { current=previous; }
    }

    [HarmonyPriority(Priority.First)]
    static void Postfix(CompProjectileInterceptor __instance,Projectile projectile,bool __result)
    {
        if(current is {} attempt && attempt.Shield==__instance && attempt.Flight.Projectile==projectile)
            attempt.Flight.ShieldCommitted=__result;
    }
}
