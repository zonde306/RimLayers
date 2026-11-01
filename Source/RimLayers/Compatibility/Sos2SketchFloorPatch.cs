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
        savedDoors = null;
        banded = false;
        if (!Sos2Compatibility.Active) return;
        if (ShipArea == null || __instance == null) return;
        if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out int stride, out int height, out _)) return;
        if (owner.Layout!.Width != height) return;
        if (ShipArea.GetValue(__instance) is not HashSet<IntVec3> { Count: > 0 } area) return;
        // From here on the ghost is built in band-local space, which has three parts that all have to
        // agree: the footprint (flattened here), Map.Size (answered by BandedSize inside the body) and
        // the outer airlock doors, whose offsets SOS2 takes from Building.Position. Any one of them left
        // in storage space puts part of the ghost a band away and the whole placement turns red.
        banded = true;
        FlattenDoors(__instance, stride);
        var flattened = new HashSet<IntVec3>();
        bool changed = false;
        foreach (IntVec3 cell in area)
        {
            int local = Sos2Geometry.LocalZ(cell.z, stride);
            if (local != cell.z) changed = true;
            flattened.Add(new IntVec3(cell.x, cell.y, local));
        }
        // A hull that already lives on one deck is left exactly as it was: SOS2 handles it, and a
        // rewrite would only add a way to break it. The Size rewrite stays on regardless: it is
        // neutral here because every offset it feeds is a difference against the same constant.
        if (!changed) return;
        savedArea = area;
        ShipArea.SetValue(__instance, flattened);
    }

    /// <summary>A door whose Position was briefly flattened, with the value to put back.</summary>
    private readonly record struct Door(Thing Part, int StorageZ);

    [System.ThreadStatic] private static List<Door>? savedDoors;

    /// <summary>
    /// Puts every outer airlock's z inside band 0 for the duration of the sketch.
    /// </summary>
    /// <remarks>
    /// <c>CreateShipSketch</c> lays the ghost out from two sources: <c>Area</c>, which this prefix can
    /// swap for a flattened copy, and the outer <c>Building_ShipAirlock</c> doors, whose mirrored
    /// offsets are taken from <c>Building.Position</c> directly. A hull with a door on an upper deck
    /// therefore grows extender cells one band outside the deck the ghost is being dropped on; those
    /// fail <c>InBounds</c>, <c>AllowsPlacing</c> rejects every position, and the flip button looks
    /// dead with no message. The door is only read to compute a decoration offset here, so moving its z
    /// for the duration of the call is safe and is restored before anything else looks at the ship.
    /// </remarks>
    private static void FlattenDoors(object cache, int stride)
    {
        if (ShipParts?.GetValue(cache) is not IEnumerable<Thing> parts) return;
        foreach (Thing part in parts.ToList())
        {
            if (!OuterDoor(part)) continue;
            int local = Sos2Geometry.LocalZ(part.Position.z, stride);
            if (local == part.Position.z) continue;
            (savedDoors ??= new List<Door>()).Add(new Door(part, part.Position.z));
            part.Position = new IntVec3(part.Position.x, 0, local);
        }
    }

    /// <summary>Puts every briefly flattened door back where it belongs.</summary>
    private static void RestoreDoors()
    {
        if (savedDoors is not { Count: > 0 } doors) return;
        foreach (Door door in doors)
        {
            if (door.Part == null || door.Part.DestroyedOrNull()) continue;
            door.Part.Position = new IntVec3(door.Part.Position.x, 0, door.StorageZ);
        }
        savedDoors = null;
    }

    /// <summary>
    /// What <c>Map.Size</c> answers while a ghost is being sketched: the deck's own rectangle instead
    /// of the whole storage, so the flip's mirror lands on the deck the view is on.
    /// </summary>
    /// <remarks>
    /// <c>CreateShipSketch</c> lays the ghost out as <c>mirror(cell) - lowestCorner</c>. For a quarter
    /// turn both terms read <c>Size.x</c>, which already equals the deck height, so rotation works. The
    /// flip reads <c>Size.z</c> for the z half - the storage height, every band stacked plus padding -
    /// so a cell at local z 130 is mirrored to storage z 380, one band above the ground the ghost is
    /// being placed on. The sketch then occupies an empty band, every cell reads as blocked, and the
    /// flip button looks dead. The move itself is band-correct (<c>TransformForShipMove</c> is
    /// rewritten), which is what made the two halves disagree by exactly one band.
    /// </remarks>
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? size = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Size));
        MethodInfo? bandedSize = AccessTools.Method(typeof(Sos2SketchFloorPatch), nameof(BandedSize));
        if (size == null || bandedSize == null)
            throw new System.InvalidOperationException("[RimLayers] SOS2 ghost size anchors are missing.");
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (!instruction.Calls(size))
            {
                yield return instruction;
                continue;
            }
            var call = new CodeInstruction(OpCodes.Call, bandedSize);
            call.labels.AddRange(instruction.labels);
            call.blocks.AddRange(instruction.blocks);
            replaced++;
            yield return call;
        }
        // Six reads: three in the footprint loop, three in the outer-door loop. A partial rewrite would
        // leave one half in storage space and the other in band space, which is worse than vanilla. A
        // seventh read means SOS2 grew another mirror and it needs the same answer, so fail loudly.
        if (replaced != 6)
            throw new System.InvalidOperationException(
                $"RimLayers SOS2 ghost anchor changed: {replaced} Map.Size reads in CreateShipSketch (expected 6).");
    }

    private static IntVec3 BandedSize(Map map)
    {
        if (!banded) return map.Size;
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return map.Size;
        return new IntVec3(map.Size.x, 0, layout.Height);
    }

    /// <summary>True while <c>CreateShipSketch</c> is building a ghost in band-local space.</summary>
    [System.ThreadStatic] private static bool banded;

    /// <summary>
    /// Whether the ghost is currently being laid out in band-local space. Exposed for the in-game
    /// test, which has to know whether a ghost it inspects was measured on one deck.
    /// </summary>
    internal static bool GhostIsBanded => banded;

    private static void Postfix(Map targetMap, byte rotb, object __instance)
    {
        RestoreDoors();
        Restore(__instance);
        banded = false;
        if (!Sos2Compatibility.Active) return;
        EnsureGroundFloor(targetMap);
        AnchorOnDeck(targetMap);
        LogOnce(rotb, targetMap);
    }

    private static System.Exception? Finalizer(System.Exception? __exception, object __instance)
    {
        RestoreDoors();
        Restore(__instance);
        banded = false;
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
        // the selector is the only handle on it - but it is not the ONLY anchor. The ship command is
        // also reachable from a bridge whose sketch failed partway, and a selection left over from a
        // previous ghost still looks like a MinifiedThing. Moving anything that is not this ghost to
        // the deck centre would relocate an unrelated object on the map, so the handle is verified by
        // the inner thing's type before its position is touched.
        if (Find.Selector.SingleSelectedThing is not Thing minified) return;
        if (AccessTools.Field(minified.GetType(), "InnerThing")?.GetValue(minified) is not Thing inner) return;
        if (!inner.GetType().Name.Contains("ShipMoveBlueprint")) return;
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

    private static readonly FieldInfo? ShipParts =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "Parts");

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
