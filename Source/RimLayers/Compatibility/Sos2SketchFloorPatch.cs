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
        banded = false;
        if (!Sos2Compatibility.Active) return;
        if (ShipArea == null || __instance == null) return;
        // The postfix needs the same answer whether or not the footprint below turns out to change, so
        // the flag is raised first and the footprint is only swapped when it actually differs.
        banded = true;
        bandedCall = true;
        deckHeight = -1;
        if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out int stride, out int height, out _)) return;
        if (owner.Layout!.Width != height) return;
        deckHeight = height;
        if (ShipArea.GetValue(__instance) is not HashSet<IntVec3> { Count: > 0 } area) return;
        // From here on the ghost is built in band-local space, on both sides that have to agree: the
        // footprint (flattened below) and Map.Size (answered by BandedSize inside the body). Any one of
        // them left in storage space puts part of the ghost a band away and the whole placement turns
        // red. The flag stays raised until the postfix has used it, because the ghost's final position
        // is storage coordinates and a band-local centre would put it on the wrong deck.
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
        // The prefix measured the deck; a sketch started on a map whose width is not the deck height
        // never raises the swap, and that call has to keep reading the real Map.Size.
        return new IntVec3(map.Size.x, 0, deckHeight > 0 ? deckHeight : layout.Height);
    }

    /// <summary>True while <c>CreateShipSketch</c> is building a ghost in band-local space.</summary>
    [System.ThreadStatic] private static bool banded;

    /// <summary>
    /// Whether the call in progress raised <see cref="banded"/>. The flag has to survive until the
    /// postfix has placed the ghost, but it must never leak into a later sketch on the same thread.
    /// </summary>
    [System.ThreadStatic] private static bool bandedCall;

    /// <summary>The deck height <c>Map.Size</c> answers while this call is in flight; -1 when untouched.</summary>
    [System.ThreadStatic] private static int deckHeight;

    /// <summary>
    /// Whether the ghost is currently being laid out in band-local space. Exposed for the in-game
    /// test, which has to know whether a ghost it inspects was measured on one deck.
    /// </summary>
    internal static bool GhostIsBanded => banded;

    private static void Postfix(Map targetMap, byte rotb, object __instance)
    {
        // The area is restored after the ghost is anchored, not before: the anchor converts the deck's
        // local centre into storage coordinates with BandedSize, and a swapped footprint or a cleared
        // flag at that point silently parks the ghost in the middle band of the map instead.
        try
        {
            AnchorAndTrace(targetMap, rotb);
        }
        finally
        {
            Restore(__instance);
            banded = false;
            bandedCall = false;
            deckHeight = -1;
        }
    }

    private static void AnchorAndTrace(Map targetMap, byte rotb)
    {
        if (!Sos2Compatibility.Active) return;
        // Lay the ghost on the deck the player is standing on. This used to force the ground floor, on
        // the assumption that a layered map is always viewed from the bottom - but a ship's command
        // console can be on any deck, and jumping the view away from it is what makes a click look like
        // "it only switched floors and nothing else happened". The ghost is drawn as an offset from the
        // deck it is dropped on, so it has to land on the viewed deck, whatever that deck is.
        AnchorOnDeck(targetMap);
        TraceGhost(rotb, targetMap);
    }

    private static System.Exception? Finalizer(System.Exception? __exception, object __instance)
    {
        Restore(__instance);
        banded = false;
        bandedCall = false;
        deckHeight = -1;
        // The sketch throws from inside SOS2 and every GUI mod on the stack swallows the exception
        // before the player sees it, so the only place the real fault is still visible is here. Without
        // this line a dead ship command reads as "the blueprint never appeared" with no cause anywhere
        // in the log.
        if (__exception != null)
            Log.Error("[RimLayers] SOS2 CreateShipSketch threw before the ghost existed: "
                      + __exception.GetType().Name + ": " + __exception.Message
                      + System.Environment.NewLine + __exception);
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
    /// <summary>The deck <see cref="AnchorOnDeck"/> would use for this map, for the in-game test.</summary>
    internal static int AnchorDeckOf(Map? targetMap)
    {
        LayerMapComponent? owner = LayerMapComponent.For(targetMap);
        return owner?.Layout is { LayerCount: > 1 } ? owner.ViewedLayer : -1;
    }

    private static bool AnchorOnDeck(Map targetMap)
    {
        // The ghost is spawned at Map.Center, which on a layered map is the middle of the storage
        // height - one band per deck away from anything the player can see. Move it onto the deck the
        // player is looking at, because that is the deck the ghost's own offsets are measured from.
        //
        // What it must never do is move something that is not this ghost: the ship command is also
        // reachable from a console whose sketch failed partway, and a selection left over from a
        // previous attempt still looks like a minified thing. So the handle is verified by the inner
        // thing's type before its position is touched.
        // The ghost is still at Map.Center when this runs, and Map.Center on a layered map is the
        // middle of the storage height, which is several decks above anything the player can see. The
        // selector cannot be trusted to hand it over: CreateShipSketch selects it and then immediately
        // opens an Install designator on it, and that designator dereferences the same selection. If
        // anything on the way in refuses, the call throws before this postfix runs and the ghost is
        // never moved, so it is located by type instead.
        if (!TryGhost(targetMap, out Thing minified)) return false;
        LayerMapComponent? owner = LayerMapComponent.For(targetMap);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return false;
        int deck = owner.ViewedLayer;
        if (deck < 0 || deck >= layout.LayerCount) return false;
        // While the sketch is in flight Map.Size is the deck's rectangle, which is exactly where the
        // ghost was measured against (with y = 0 at the deck's own floor). Converting the deck-local
        // centre to storage coordinates uses that same rectangle, so the ghost lands on the deck the
        // player is looking at instead of an elevation's worth of cells below it.
        IntVec3 centre = owner.ToStorage(new LayerCell(deck, layout.Width / 2, layout.Height / 2));
        if (!centre.InBounds(targetMap) || minified.Position == centre) return false;
        minified.Position = centre;
        return true;
    }

    /// <summary>
    /// The ship ghost standing on <paramref name="targetMap"/>, found by type rather than through the
    /// selector so a failed selection cannot hide it.
    /// </summary>
    private static bool TryGhost(Map targetMap, out Thing ghost)
    {
        ghost = null!;
        foreach (Thing thing in targetMap.listerThings.AllThings)
        {
            if (thing is not MinifiedThing) continue;
            if (!thing.GetType().Name.Contains("MinifiedThingShipMove")) continue;
            ghost = thing;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="map"/> carries the ship ghost, asked by type so the answer does not
    /// depend on the selector. The in-game test uses it to prove the ghost was built at all before it
    /// asks where the ghost ended up.
    /// </summary>
    internal static bool HasGhost(Map? map) => map != null && TryGhost(map, out _);

    /// <summary>
    /// One line per ghost that says where it ended up and which deck the player is looking at, because
    /// those two can disagree: the view is switched to the ground floor, but a ghost anchored by a
    /// different rule is then drawn on a band the player cannot see. The log is the only way to tell
    /// "the ghost was never built" from "the ghost was built out of sight".
    /// </summary>
    private static void TraceGhost(byte rotb, Map targetMap)
    {
        if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out _, out _, out int layers)) return;
        TryGhost(targetMap, out Thing found);
        Thing? ghost = Find.Selector.SingleSelectedThing ?? (found == null ? null : found);
        string at = ghost == null ? "none" : ghost.Position.ToString();
        string ghostDeck = ghost != null && owner.TryDecode(ghost.Position, out LayerCell ghostCell)
            ? "L" + ghostCell.LayerId
            : "?";
        Log.Message($"[RimLayers] SOS2 ghost rotb={rotb} map={targetMap} layers={layers} " +
                    $"viewed=L{owner.ViewedLayer} anchored={(ghostDeck == "L" + owner.ViewedLayer ? "on-view" : "off-view")} " +
                    $"ghost={at} deck={ghostDeck} selected={Find.Selector.SingleSelectedThing != null} centre={targetMap.Center} centreDeck=" +
                    (owner.TryDecode(targetMap.Center, out LayerCell centreCell) ? "L" + centreCell.LayerId : "?"));
    }

    /// <summary>
    /// Moves the viewed floor to the map's ground level when it is not already there. Returns true
    /// when the view actually moved, so the behaviour can be asserted without driving the UI.
    /// </summary>
    internal static bool EnsureGroundFloor(Map? map)
    {
        LayerMapComponent? owner = LayerMapComponent.For(map);
        if (owner?.Layout is not { LayerCount: > 1 } layout) return false;
        // A deck that is not allocated at elevation 0 is the normal case for an underground map, whose
        // bands start below zero. Asking for elevation 0 would decline on those maps and leave the view
        // where it is, so fall back to the lowest deck the ship can actually stand on.
        int ground = layout.TryGetLayerAtElevation(0, out int atZero) ? atZero : 0;
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
