using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using UnityEngine;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Temporary evidence tap: what a ship-to-ship shot actually looks like to a shield on a layered map.
/// </summary>
/// <remarks>
/// A report said a shield on deck 1 stops fire on deck 1 but not fire on deck 2, and that a shield on
/// deck 2 does stop deck-2 fire. Three explanations fit that, and reading the source cannot separate
/// them: the shot is spawned in the wrong band, the shield's radius test sees a band in its y, or the
/// shield is not in <c>ShipMapComp.Shields</c> when the shot arrives. This class answers with numbers
/// from the running game instead of another argument.
/// </remarks>
internal static class Sos2ShieldTrace
{
    internal static bool Installed;

    /// <summary>Shots a shield has actually caught, and a one-line description of the last one.</summary>
    internal static int Hits;

    /// <summary>Intercept tests run, so "no hit" can be told apart from "nothing was fired".</summary>
    internal static int Shots;
    internal static string LastShot = "none";

    internal static void Install(Harmony harmony)
    {
        if (Installed) return;
        Type? postfixType = AccessTools.TypeByName("SaveOurShip2.ShieldsWithoutDLC");
        MethodInfo? postfix = postfixType == null
            ? null
            : AccessTools.Method(postfixType, "Postfix",
                new[] { typeof(Projectile), typeof(Vector3), typeof(Vector3), typeof(bool).MakeByRefType() });
        if (postfix == null) return;
        harmony.Patch(postfix, postfix: new HarmonyMethod(AccessTools.Method(typeof(Sos2ShieldTrace), nameof(After))));
        Installed = true;
    }

    private static void After(Projectile __instance, Vector3 lastExactPos, Vector3 newExactPos)
    {
        try
        {
            // __instance can arrive null: Harmony still calls every postfix on the shared patch
            // when another mod patched the same method. Guard before touching anything.
            if (__instance == null) return;
            Map? map = __instance.Map;
            if (map == null) return;
            LayerMapComponent? owner = LayerMapComponent.For(map);
            if (owner?.Layout is not { LayerCount: > 1 } layout) return;
            object? comp = Sos2Compatibility.ShipCompOf(map);
            if (comp == null) return;
            int shields = 0;
            var lines = new List<string>();
            if (comp != null && AccessTools.Field(comp.GetType(), "Shields")?.GetValue(comp) is System.Collections.IEnumerable list)
            {
                foreach (object? entry in list)
                {
                    if (entry == null) continue;
                    shields++;
                    Thing? parent = AccessTools.Field(typeof(ThingComp), "parent")?.GetValue(entry) as Thing
                                    ?? AccessTools.Field(entry.GetType(), "parent")?.GetValue(entry) as Thing;
                    if (parent == null) continue;
                    bool down = AccessTools.Field(entry.GetType(), "shutDown")?.GetValue(entry) is true;
                    float radius = AccessTools.Field(entry.GetType(), "radius")?.GetValue(entry) is float r ? r : -1f;
                    Vector3 pos = parent.Position.ToVector3Shifted();
                    float raw = Vector3.Distance(lastExactPos, pos);
                    pos.y = lastExactPos.y;
                    float flat = Vector3.Distance(lastExactPos, pos);
                    lines.Add($"shield@{parent.Position} layer={(owner.TryDecode(parent.Position, out LayerCell at) ? at.LayerId : -1)} " +
                              $"down={down} radius={radius} raw={raw:F1} flat={flat:F1} hit={(flat <= radius)}");
                }
            }
            Shots++;
            LastShot = $"proj={__instance.def.defName} cell={__instance.Position} " +
                $"layer={(owner.TryDecode(__instance.Position, out LayerCell shot) ? shot.LayerId : -1)} " +
                $"last={lastExactPos} new={newExactPos} shields={shields} | {string.Join(" ; ", lines)}";
            if (LastShot.Contains(" hit=True")) Hits++;
        }
        catch (Exception e)
        {
            Log.Warning("[RimLayers shield] trace failed: " + e);
        }
    }
}
