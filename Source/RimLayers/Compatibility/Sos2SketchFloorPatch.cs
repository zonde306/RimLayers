using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Keeps SOS2's ship-move ghost inside one deck's band: drops the view to the ground floor and
/// flattens the ship's footprint to band-local z while the ghost is being built.
/// </summary>
/// <remarks>
/// <para>
/// The move/landing blueprint is an <c>Install</c> designator holding one fake minified ship, and it
/// can only be placed on the floor the hull stands on: <c>PlaceWorker_MoveShip</c> validates the
/// bottom band against the destination. Everything about that works only if the mouse is on that
/// floor, and RimLayers deliberately treats a cell outside the viewed floor as no cell at all
/// (<c>MouseCellPatch</c> returns <c>IntVec3.Invalid</c>). A player who launched from upstairs and is
/// still looking upstairs therefore gets a designator that cannot accept any cell, with nothing on
/// screen saying why. Switching the view is the honest fix: the ghost has exactly one floor it can go
/// on, so the tool should put the player on it rather than let them fail invisibly.
/// </para>
/// <para>
/// The second half is arithmetic. <c>SpaceShipCache.Area</c> holds one cell per hull tile, and a
/// multi-deck hull therefore mixes storage bands: the same ship can own z=120, z=375 and z=630 in one
/// set. SOS2 subtracts those raw z values from each other (<c>pos - lowestCorner</c>) and subtracts
/// them from <c>Map.Size</c>, which is correct only while every cell shares a band. With a deck above,
/// the offsets come out around -255 per extra deck, so the ghost is drawn off the map and
/// <c>AllowsPlacing</c> rejects it with no message: rotate and flip appear to do nothing at all.
/// Flattening to <c>LocalZ</c> for the duration of the call restores the geometry SOS2 assumes. The
/// band is put back by <c>TransformForShipMove</c>, which this adapter already rewrites.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2SketchFloorPatch
{
    private static readonly FieldInfo? ShipArea =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "Area");

    private static readonly HashSet<byte> Logged = new();

    /// <summary>The set that was briefly swapped for a flattened copy, restored by postfix/finaliser.</summary>
    [System.ThreadStatic] private static object? savedArea;

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

    /// <summary>
    /// Hands SOS2 a footprint whose z values are band-local, so every <c>pos - lowestCorner</c> it
    /// computes is a real distance instead of a sum of one deck plus a band offset.
    /// </summary>
    private static void Prefix(object __instance, Map targetMap)
    {
        savedArea = null;
        if (!Sos2Compatibility.Active) return;
        if (ShipArea == null || __instance == null) return;
        if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out int stride, out int height, out _)) return;
        // Half this correction would be worse than none of it. The airlock extenders are laid out from
        // Building.Position, which cannot be flattened, so a hull whose outer doors sit on an upper
        // deck keeps SOS2's own arithmetic rather than getting one consistent half and one broken half.
        if (owner.Layout!.Width != height) return;
        if (ShipArea.GetValue(__instance) is not HashSet<IntVec3> { Count: > 0 } area) return;
        if (HasAirlockOffGround(__instance, stride)) return;
        var flattened = new HashSet<IntVec3>();
        bool changed = false;
        foreach (IntVec3 cell in area)
        {
            int local = Sos2Geometry.LocalZ(cell.z, stride);
            if (local != cell.z) changed = true;
            flattened.Add(new IntVec3(cell.x, cell.y, local));
        }
        // A hull that already lives on one deck is left exactly as it was: SOS2 handles it, and a
        // rewrite would only add a way to break it.
        if (!changed) return;
        savedArea = area;
        ShipArea.SetValue(__instance, flattened);
    }

    private static readonly FieldInfo? ShipParts =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "Parts");

    /// <summary>True when any outer airlock sits off the ground deck, which the flattening cannot cover.</summary>
    private static bool HasAirlockOffGround(object cache, int stride)
    {
        // SOS2 grows the ghost from the airlock doors as well as from Area, and those offsets come from
        // Building.Position. A door on an upper deck would therefore stay a band away from the rest of
        // the flattened hull.
        if (ShipParts?.GetValue(cache) is not IEnumerable<Thing> parts) return false;
        foreach (Thing part in parts)
        {
            if (!OuterDoor(part)) continue;
            if (Sos2Geometry.LocalZ(part.Position.z, stride) != part.Position.z) return true;
        }
        return false;
    }

    private static bool DoorCached;
    private static MethodInfo? DoorMethod;

    /// <summary><c>Building_ShipAirlock.Outerdoor()</c>, which is what decides the extender set.</summary>
    private static bool OuterDoor(Thing part)
    {
        if (!DoorCached)
        {
            DoorCached = true;
            System.Type? airlock = AccessTools.TypeByName("SaveOurShip2.Building_ShipAirlock");
            DoorMethod = airlock == null ? null : AccessTools.Method(airlock, "Outerdoor");
        }
        if (DoorMethod == null || part == null) return false;
        if (!DoorMethod.DeclaringType!.IsInstanceOfType(part)) return false;
        try { return DoorMethod.Invoke(part, null) is true; }
        catch (System.Exception) { return false; }
    }

    private static void Postfix(Map targetMap, byte rotb, object __instance)
    {
        Restore(__instance);
        if (!Sos2Compatibility.Active) return;
        EnsureGroundFloor(targetMap);
        AnchorOnDeck(targetMap);
        LogOnce(rotb, targetMap);
    }

    private static System.Exception? Finalizer(System.Exception? __exception, object __instance)
    {
        Restore(__instance);
        return __exception;
    }

    private static void Restore(object __instance)
    {
        if (savedArea == null || ShipArea == null || __instance == null) return;
        ShipArea.SetValue(__instance, savedArea);
        savedArea = null;
    }

    /// <summary>
    /// Puts the freshly spawned ghost on the deck instead of at <c>Map.Center</c>, which on a layered
    /// map is <c>z = storageHeight / 2</c> and therefore usually lands in the second band. The player
    /// then sees nothing at all on the floor the view was just moved to.
    /// </summary>
    private static void AnchorOnDeck(Map targetMap)
    {
        // CreateShipSketch selects the ghost as the very last thing it does, so by the time this runs
        // the selector is the only handle on it.
        if (Find.Selector.SingleSelectedThing is not Thing minified) return;
        LayerMapComponent? owner = LayerMapComponent.For(targetMap);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return;
        if (!layout.TryGetLayerAtElevation(0, out int ground)) return;
        IntVec3 centre = owner.ToStorage(new LayerCell(ground, layout.Width / 2, layout.Height / 2));
        if (!centre.InBounds(targetMap) || minified.Position == centre) return;
        minified.Position = centre;
    }

    /// <summary>
    /// One line per rotation mode so a still-red ghost can be diagnosed from the player log without
    /// asking for a repro. The numbers are the ones SOS2's own arithmetic hinges on.
    /// </summary>
    private static void LogOnce(byte rotb, Map targetMap)
    {
        if (!Logged.Add(rotb)) return;
        if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out _, out int height, out int layers)) return;
        Log.Message($"[RimLayers] SOS2 ship ghost rotb={rotb} map={targetMap} logical={owner.Layout!.Width}x{height} bands={layers}");
    }

    /// <summary>
    /// Moves the viewed floor to the map's ground level when it is not already there. Returns true
    /// when the view actually moved, so the behaviour can be asserted without driving the UI.
    /// </summary>
    internal static bool EnsureGroundFloor(Map? map)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return false;
        if (!layout.TryGetLayerAtElevation(0, out int ground)) return false;
        if (owner.ViewedLayer == ground) return false;
        // Switch refuses a component that is not the currently viewed map, which is the case while the
        // game is still pointed elsewhere. The caller has already moved CurrentMap by this point, so a
        // refusal here means the view is genuinely not ours to move.
        if (!LayerView.Switch(owner, ground)) return false;
        // The ghost is one cell tall on screen but the hull it stands for is not; keep the camera on the
        // floor too, otherwise the switch moves the world out from under the cursor.
        CameraJumper.TryJump(owner.ToStorage(new LayerCell(ground, owner.Layout.Width / 2, owner.Layout.Height / 2)), map);
        return true;
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
        // 1 turns the hull a quarter and 2 flips it, but both do the same shape of arithmetic: SOS2
        // measures the mirrored corner against Map.Size.z, which on a layered map is the storage height
        // (every band stacked, ~510) and not the deck's own side (~250). The corner then lands roughly
        // one band per deck outside the map, the adjustment derived from it inherits that, and MoveShip
        // asks for a cell with a negative z - the "Got ThingsListAt out of bounds: (127, 0, -397)" that
        // starts the half-moved-shop cascade. Both rotations need the same correction.
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
