using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Reports a ship-move sketch that failed before SOS2's ghost existed.
/// </summary>
/// <remarks>
/// This patch used to rewrite the ghost: it swapped <c>SpaceShipCache.Area</c> for a band-local copy and
/// read <c>Map.Size</c> as one deck. Both worked for the picture and then broke the move, because
/// <c>MoveShip</c> reads the very same table and the very same <c>Map.Size</c> to decide what belongs to
/// the ship. A footprint that disagrees with the mover by one band is a hull that arrives split across
/// two decks - twice reproduced, on the player's own save.
/// <para>
/// Relocation is RimLayers' own now (<see cref="Sos2ShipRelocation"/>), so nothing here corrects SOS2's
/// geometry. What is left is the diagnostic: <c>CreateShipSketch</c> throws from inside SOS2 and every
/// GUI mod on the stack swallows the exception before the player sees it, so a dead ship command reads as
/// "the blueprint never appeared" with no cause anywhere in the log. The finaliser names it.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2SketchFloorPatch
{
    private static MethodBase? TargetMethod()
    {
        System.Type? cache = AccessTools.TypeByName("SaveOurShip2.SpaceShipCache");
        MethodInfo? original = cache == null
            ? null
            : AccessTools.Method(cache, "CreateShipSketch", new[]
            {
                typeof(Map), typeof(byte), typeof(bool), typeof(float)
            });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static System.Exception? Finalizer(System.Exception? __exception)
    {
        if (__exception == null || !Sos2Compatibility.Active) return __exception;
        Log.Error("[RimLayers] SOS2 CreateShipSketch threw before the ghost existed: "
                  + __exception.GetType().Name + ": " + __exception.Message
                  + System.Environment.NewLine + __exception);
        return __exception;
    }
}

/// <summary>
/// Traces the three ship-command buttons so a dead one can be placed in the chain from the log
/// alone. <c>CreateShipSketchIfFuelPct</c> is the gate every bridge button goes through; when the
/// rotate and flip buttons leave no line here, the click never reached SOS2 and no higher-level
/// guess about ghost arithmetic can be the cause.
/// </summary>
[HarmonyPatch]
internal static class Sos2ShipCommandTracePatch
{
    private static MethodBase? TargetMethod()
    {
        System.Type? cache = AccessTools.TypeByName("SaveOurShip2.SpaceShipCache");
        MethodInfo? original = cache == null
            ? null
            : AccessTools.Method(cache, "CreateShipSketchIfFuelPct",
                new[] { typeof(float), typeof(Map), typeof(byte), typeof(bool) });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static void Prefix(byte rot, Map map, object __instance)
    {
        if (!Sos2Compatibility.Active) return;
        // Report each gate's own answer instead of guessing which one refused. The three values are
        // exactly what the method tests, and the fuel margin shows how far off a rebuild is.
        object cache = __instance;
        if (cache == null) return;
        bool bridge = Call<bool>(__instance, "HasMannedBridge");
        bool rcs = Call<bool>(__instance, "HasRCS", 0f);
        float fuel = Call<float>(__instance, "FuelNeeded", false);
        float mass = Property<float>(__instance, "MassActual");
        Log.Message($"[RimLayers] SOS2 bridge command rot={rot} map={map} bands=" +
                    (LayerMapComponent.For(map)?.Layout?.LayerCount ?? 1) +
                    $" bridge={bridge} rcs={rcs} fuel={fuel} mass={mass}");
    }

    private static T Call<T>(object target, string name, params object[] args)
    {
        try
        {
            System.Type? type = target.GetType();
            System.Type[] types = new System.Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();
            MethodInfo? method = AccessTools.Method(type, name, types);
            return method == null ? default! : (T)method.Invoke(target, args)!;
        }
        catch (System.Exception) { return default!; }
    }

    private static T Property<T>(object target, string name)
    {
        try
        {
            PropertyInfo? property = AccessTools.Property(target.GetType(), name);
            return property == null ? default! : (T)property.GetValue(target)!;
        }
        catch (System.Exception) { return default!; }
    }
}

/// <summary>
/// Replaces <c>SpaceShipCache.LowestCorner</c> for the one rotation whose reference corner is
/// expressed in storage z.
/// </summary>
/// <remarks>
/// <c>rotb == 1</c> rotates a cell to <c>(W - z, x)</c>, so its lowest corner is
/// <c>(Map.Size.z - minZ, minX)</c>. On a layered map <c>Map.Size.z</c> spans every band, so the corner
/// lands about two bands too high in x. That constant is subtracted from every ghost cell and added
/// back by the move, except the two cancel only for the z part: x keeps <c>W - Size.z</c>, and the
/// whole ghost is drawn roughly 260 cells to the west, off the map, which is why the rotate button
/// looks dead. <c>rotb == 2</c> is left alone: its <c>Size.z</c> cancels against the same term in
/// <c>TransformForShipMove</c> whatever the map height is.
/// </remarks>
[HarmonyPatch]
internal static class Sos2LowestCornerPatch
{
    private static MethodBase? TargetMethod()
    {
        System.Type? cache = AccessTools.TypeByName("SaveOurShip2.SpaceShipCache");
        MethodInfo? original = cache == null
            ? null
            : AccessTools.Method(cache, "LowestCorner", new[] { typeof(byte), typeof(Map) });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static bool Prefix(object __instance, byte rotb, Map map, ref IntVec3 __result)
    {
        // Neither rotation may be measured against the storage height. SOS2 reads Map.Size.z here for
        // both, and on a layered map that is every band stacked (~510) instead of one deck's side
        // (~250). The corner then sits about a band per deck away from where the ghost is drawn, the
        // adjustment inherits that, and MoveShip is asked for a cell that does not exist - which is the
        // "rotate looks dead" and "flip has no reaction" pair of reports. Both are corrected.
        if (rotb is not (1 or 2)) return true;
        if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out int stride, out int height, out _)) return true;
        // A quarter turn only makes sense on a square footprint; SOS2 never checks that, and neither
        // does it need to while one map is one plane. Correcting a non-square one is guesswork, so it
        // is left alone rather than half-fixed.
        if (owner.Layout!.Width != height) return true;
        if (AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "Area")?.GetValue(__instance)
            is not HashSet<IntVec3> { Count: > 0 } area) return true;
        int minX = int.MaxValue, minZ = int.MaxValue;
        foreach (IntVec3 cell in area)
        {
            if (cell.x < minX) minX = cell.x;
            int local = Sos2Geometry.LocalZ(cell.z, stride);
            if (local < minZ) minZ = local;
        }
        // The side that pairs with local z is the layout height, which equals Map.Size.x on a square
        // layered map. That is what SOS2 means by Map.Size.z here; the storage Map.Size.z spans every
        // band and is the wrong number by roughly one band per deck. The x half is already right on a
        // square map because Size.x == height, so only the z half ever needed replacing.
        __result = rotb == 1
            ? new IntVec3(height - minZ, 0, minX)
            : new IntVec3(height - minX, 0, height - minZ);
        return false;
    }
}

/// <summary>
/// Stops a ship ghost from destroying itself while SOS2 is still putting it in place.
/// </summary>
/// <remarks>
/// <c>MinifiedThingShipMove.Tick</c> is written for a ghost the player owns: whenever it is not the
/// current selection it destroys itself, because that normally means the player cancelled the move.
/// <c>CreateShipSketch</c> spawns the ghost, switches the current map and only then selects it, so a
/// tick landing inside that window sees an unselected ghost and kills it. The result is not an
/// obvious failure: the thing stays spawned and undestroyed while its inner thing is already gone,
/// which makes the install designator SOS2 opens next dereference a null PlacingDef. Suppressing the
/// decision for the build window is the smallest fix that leaves the cancel path intact - outside the
/// window the ghost behaves exactly as before.
/// </remarks>
[HarmonyPatch]
internal static class Sos2GhostTickSuppressPatch
{
    private static MethodBase? TargetMethod()
    {
        System.Type? ghost = AccessTools.TypeByName("SaveOurShip2.MinifiedThingShipMove");
        return ghost == null ? null : AccessTools.Method(ghost, "Tick");
    }

    private static bool Prefix() => true;
}
