using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Recovery;
using RimLayers.Vehicles;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Landing and take-off geometry for Aerocraft. Its own fall-back scan only tests the native
/// <c>roofGrid</c> and <c>Map.Size.z</c>; on a layered map <c>Map.Size.z == Layout.StorageHeight</c>,
/// so the middle bands and the reserved isolation rows between them are invisible to it, and a
/// gliding descent can pass through an upper slab or stop on a reserved row where
/// <c>TryDecode</c> fails.
///
/// <para>Both primitives already exist in RimLayers and are reused verbatim instead of inventing a
/// third interpretation of "there is a floor here": <see cref="StructuralSupport.HasSurface"/> for
/// the floor itself (surface terrain plus structural slab, ship deck or vehicle deck) and
/// <see cref="CombatService.OpenLaunchColumn"/> for the vertical column above it.</para>
/// </summary>
internal static class AerocraftLanding
{
    /// <summary>
    /// A whole aircraft footprint must sit on one floor, with a clear column above every cell.
    /// Used for placement, take-off runs and touchdown points alike.
    /// </summary>
    internal static bool CanLand(LayerMapComponent owner, IntVec3 storage, IntVec2 size, bool requireColumn = true)
    {
        if (owner.Layout == null || !owner.TryDecode(storage, out var anchor)) return false;
        // Underground aprons are enclosed by construction: a clear column above them is neither
        // possible nor needed, so the column rule only applies at or above the surface.
        if (requireColumn && owner.Elevation(anchor.LayerId) < 0) requireColumn = false;
        foreach (var cell in GenAdj.OccupiedRect(storage, Rot4.North, size))
        {
            if (!owner.TryDecode(cell, out var local) || local.LayerId != anchor.LayerId) return false;
            if (!owner.Structure.HasSurface(cell)) return false;
            if (requireColumn && !owner.Combat.OpenLaunchColumn(cell)) return false;
        }
        return true;
    }

    /// <summary>
    /// The floor an incoming object would meet at this column: the highest positive elevation that
    /// actually owns a surface, mirroring <see cref="Structure.SkyfallService.Trace"/>.
    /// </summary>
    internal static bool TopmostSurface(LayerMapComponent owner, int x, int z, out int layer)
    {
        layer = 0;
        var layout = owner.Layout;
        if (layout == null) return false;
        foreach (int candidate in layout.OrderedLayers.Reverse())
        {
            if (owner.Elevation(candidate) < 0) continue;
            if (!owner.Structure.HasSurface(owner.ToStorage(new LayerCell(candidate, x, z)))) continue;
            layer = candidate;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Nearest legal touchdown to <paramref name="origin"/> inside its own band. Never crosses into
    /// another floor, because the whole track has to stay in one storage band.
    /// </summary>
    internal static bool TryFindNearby(LayerMapComponent owner, IntVec3 origin, IntVec2 size, out IntVec3 result)
    {
        result = IntVec3.Invalid;
        if (!owner.TryDecode(origin, out var anchor)) return false;
        if (CanLand(owner, origin, size)) { result = origin; return true; }
        int radius = Math.Max(4, Math.Max(size.x, size.z) * 2);
        for (int ring = 1; ring <= radius; ring++)
        {
            foreach (var cell in GenRadial.RadialCellsAround(owner.ToStorage(new LayerCell(anchor.LayerId, anchor.X, anchor.Z)), ring, false))
            {
                if (!owner.TryDecode(cell, out var local) || local.LayerId != anchor.LayerId) continue;
                if (!CanLand(owner, cell, size)) continue;
                result = cell;
                return true;
            }
        }
        return false;
    }

    /// <summary>Expansion and other ownership transactions must not touch an airborne track.</summary>
    internal static bool Deferred(Thing aircraft)
    {
        if (!RecoveryCompatibility.TransactionActive) return false;
        RecoveryCompatibility.DeferFor(aircraft, 30);
        return true;
    }
}

/// <summary>
/// `Change_Position` bounds the track with `Map.Size`, which covers every band plus the reserved
/// isolation rows. Inside a band the correct check is the band's own local height, so the aircraft
/// can neither drift into reserved rows nor treat the top band as "the edge of the world".
/// The rest of the method (no-build-edge turn-back, power reconnection) stays untouched.
/// </summary>
[HarmonyPatch]
internal static class AerocraftPositionBoundsPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Position", Type.EmptyTypes)
        ?? throw new MissingMethodException("Aerocraft Building_Aerocraft_AsBaseThing.Change_Position disappeared.");

    private static bool Prefix(Thing __instance)
    {
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return true;
        // A parked aircraft still has to run the native body: it re-establishes the power
        // connection after every grid write. Only an airborne track is deferred.
        if (!AerocraftCompatibility.IsStatic(__instance) && AerocraftLanding.Deferred(__instance)) return false;
        var flight = AerocraftCompatibility.FlightOf(__instance, owner);
        if (flight.ProxyLayer == 0) return true; // Ground band keeps its native bounds.
        // The native method copies RealCurrentPosition.y straight into Position.z, so clamping the
        // track is what keeps the aircraft inside its band.
        var layout = owner.Layout;
        var track = AerocraftCompatibility.Position.Read(__instance);
        double offset = (double)flight.ProxyLayer * layout.Stride;
        double local = track.y - offset;
        double clampedZ = Math.Clamp(local, 1.0, layout.Height - 1.0);
        double size = AerocraftCompatibility.SizeOf(__instance).x;
        double clampedX = Math.Clamp(track.x, 1.0 + size, layout.Width - size - 1.0);
        if (Math.Abs(clampedZ - local) > 0.001 || Math.Abs(clampedX - track.x) > 0.001)
            AerocraftCompatibility.Position.Write(__instance, new Vector2((float)clampedX, (float)(clampedZ + offset)));
        return true;
    }
}

/// <summary>
/// Take-off is the only event that moves an Aerocraft between floors: the aircraft keeps its track
/// in one storage band and the whole track is rebased to the band the order names. `Change_Up` is
/// the common tail of both the vertical and the gliding take-off.
/// </summary>
[HarmonyPatch]
internal static class AerocraftTakeOffPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Up", Type.EmptyTypes)
        ?? throw new MissingMethodException("Aerocraft Change_Up disappeared.");

    private static void Postfix(Thing __instance)
    {
        if (!AerocraftCompatibility.IsAerocraft(__instance) || !__instance.Spawned) return;
        if (!AerocraftCompatibility.UpOrDown.Read(__instance)) return;
        if (AerocraftLanding.Deferred(__instance)) return;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return;
        var flight = AerocraftCompatibility.FlightOf(__instance, owner);
        int target = TargetBand(__instance, owner, flight);
        if (target == flight.ProxyLayer) return;
        if (!AerocraftCompatibility.Rebase(__instance, owner, flight, target)) return;
        AircraftTrace.Changed(__instance, owner, flight, "takeoff");
    }

    /// <summary>
    /// A vertical take-off from the gizmo must stay on its own floor: <c>TargetVPos</c> may still hold
    /// a point from an earlier order, and honouring it here would teleport the aircraft across floors
    /// on a take-off that never named a destination. Only the gliding command, which resolves a
    /// runway end before calling <c>Change_Up</c>, carries a band it means.
    /// </summary>
    private static int TargetBand(Thing aircraft, LayerMapComponent owner, AerocraftLayers.Flight flight)
    {
        if (!AerocraftCompatibility.TurningByGlide.Read(aircraft)) return flight.ProxyLayer;
        var layout = owner.Layout!;
        var target = AerocraftCompatibility.TargetPosition.Read(aircraft);
        if (target.z == 0f) return flight.ProxyLayer;
        int band = (int)Math.Floor((double)target.z / layout.Stride);
        return band < 0 || band >= layout.LayerCount ? flight.ProxyLayer : band;
    }
}

/// <summary>
/// Touchdown: the aircraft is resting again, so the band it rests in becomes the floor its
/// "return to base" command returns to.
/// </summary>
[HarmonyPatch]
internal static class AerocraftTouchdownPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Down", Type.EmptyTypes)
        ?? throw new MissingMethodException("Aerocraft Change_Down disappeared.");

    private static void Postfix(Thing __instance)
    {
        if (!AerocraftCompatibility.IsAerocraft(__instance) || !__instance.Spawned) return;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return;
        var flight = AerocraftCompatibility.FlightOf(__instance, owner);
        flight.HomeLayer = flight.ProxyLayer;
        AircraftTrace.Changed(__instance, owner, flight, "landed");
    }
}

/// <summary>
/// Forced descents (no fuel, no power, EMP, destruction) compute a touchdown point inline and hand
/// it to <c>Set_TargetVPos_Down_GlidingTakeOff</c> from five different call sites. That method is
/// the single choke point, so the layered floor rule is applied there: a point that is not on a
/// real surface of the aircraft's own band is replaced by the nearest legal cell in that band.
/// </summary>
[HarmonyPatch]
internal static class AerocraftForcedDescentPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(AerocraftCompatibility.BodyType, "Set_TargetVPos_Down_GlidingTakeOff", new[] { typeof(Vector3) })
        ?? throw new MissingMethodException("Aerocraft Set_TargetVPos_Down_GlidingTakeOff disappeared.");

    /// <summary>
    /// `__args` is used instead of a named `ref` parameter: the native signature is
    /// `Set_TargetVPos_Down_GlidingTakeOff(Vector3 VPos)` and writing through the argument array is
    /// the only form Harmony marshals back unconditionally, including when the test drives the
    /// method by reflection (a named by-value parameter stayed at its entry value there).
    /// </summary>
    private static void Prefix(Thing __instance, object[] __args)
    {
        if (!AerocraftCompatibility.IsAerocraft(__instance) || !__instance.Spawned) return;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return;
        var layout = owner.Layout;
        var flight = AerocraftCompatibility.FlightOf(__instance, owner);
        double offset = (double)flight.ProxyLayer * layout.Stride;
        // The native scan works in storage coordinates already; only its legality check is flat.
        if (__args is not { Length: > 0 } || __args[0] is not Vector3 entry) return;
        var intended = new IntVec3((int)entry.x, 0, (int)entry.z);
        if (!owner.TryDecode(intended, out var at)) return;
        if (AerocraftLanding.CanLand(owner, intended, AerocraftCompatibility.SizeOf(__instance))) return;
        if (!AerocraftLanding.TryFindNearby(owner, intended, AerocraftCompatibility.SizeOf(__instance), out var legal))
        {
            // Nothing legal on this floor: keep the current position so the descent stays in-band
            // instead of tunnelling into a slab in a different band.
            legal = owner.ToStorage(new LayerCell(flight.ProxyLayer, at.X, at.Z));
            if (!AerocraftLanding.CanLand(owner, legal, AerocraftCompatibility.SizeOf(__instance))) return;
        }
        // `legal` is a STORAGE cell, so write the storage z back verbatim: the caller passes a
        // storage-space Vector3 and the aircraft keeps the whole track in one band. Decoding it to
        // a local z here would silently relocate the touchdown to band 0 (caught by the acceptance
        // scenario: the redirect landed on (100,236) instead of (100,355)).
        __args[0] = new Vector3(legal.x + 0.5f, entry.y, legal.z + 0.5f);
        AircraftTrace.Changed(__instance, owner, flight, "forced-descent-redirected");
    }
}

/// <summary>
/// Placement. The parent template carries <see cref="PlaceWorker_NotUnderRoof"/>, which asks the
/// native roof grid only. On a layered map the real precondition is "this floor owns a surface and
/// the column above it is clear", the same rule the touchdown scan uses.
/// </summary>
[HarmonyPatch(typeof(PlaceWorker_NotUnderRoof), nameof(PlaceWorker_NotUnderRoof.AllowsPlacing))]
internal static class AerocraftPlacementPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static bool Prefix(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, ref AcceptanceReport __result)
    {
        if (checkingDef is not ThingDef thingDef || !AerocraftCompatibility.IsAerocraftClass(thingDef.thingClass)) return true;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return true;
        __result = AerocraftLanding.CanLand(owner, loc, checkingDef.Size)
            ? AcceptanceReport.WasAccepted
            : new AcceptanceReport("MustPlaceUnroofed".Translate());
        return false;
    }
}

/// <summary>
/// Refuelling. Aerocraft's own prefix requires <c>Map.areaManager.Home[b.Position]</c> — a BoolGrid
/// keyed by storage cell, so a Home zone painted on the surface never covers an upper or underground
/// apron — and requires <c>Is_Static</c>. RimLayers answers by column instead: the aircraft must rest
/// on a real floor and some layer of its (x,z) column must be inside Home.
/// </summary>
[HarmonyPatch(typeof(WorkGiver_Refuel_Turret), nameof(WorkGiver_Refuel_Turret.CanRefuelThing))]
internal static class AerocraftRefuelPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    /// <summary>Must run before Aerocraft's own prefix, which would otherwise answer first.</summary>
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Thing t, ref bool __result)
    {
        if (!AerocraftCompatibility.IsAerocraft(t)) return true;
        var owner = LayerMapComponent.For(t.Map);
        if (owner?.Layout == null) return true;
        if (!owner.TryDecode(t.Position, out var at)) { __result = false; return false; }
        bool grounded = owner.Structure.HasSurface(t.Position);
        bool home = false;
        for (int layer = 0; layer < owner.Layout.LayerCount && !home; layer++)
            home = t.Map.areaManager.Home[owner.ToStorage(new LayerCell(layer, at.X, at.Z))];
        __result = grounded && home && AerocraftCompatibility.IsStatic(t);
        return false;
    }
}

/// <summary>
/// Cross-map arrival. The native drop candidates use <c>Map.Size.z</c>, which spans every band, so
/// two of the four corners land in the topmost band and <c>Map.Center</c> lands in a middle floor.
/// Re-seat the aircraft on a floor that actually owns a surface above the arrival column.
///
/// <para>The aircraft still has to keep its own band, so the track is rebased and the storage cell is
/// rewritten together: leaving the native band-0 corner and the logical top band to disagree would
/// make <c>Change_Position</c> fight the record on the next tick.</para>
/// </summary>
[HarmonyPatch]
internal static class AerocraftArrivalPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound && AerocraftCompatibility.WorldObjectType != null;

    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(AerocraftCompatibility.WorldObjectType, "Arrived", Type.EmptyTypes)
        ?? throw new MissingMethodException("Aerocraft WorldObject_CrossMapThing_Flying.Arrived disappeared.");

    private static void Postfix(object __instance)
    {
        var type = AerocraftCompatibility.WorldObjectType!;
        if (type.GetField("LinkToAerocraft")?.GetValue(__instance) is not Thing aircraft) return;
        if (type.GetField("TargetMap")?.GetValue(__instance) is not Map map) return;
        if (!aircraft.Spawned || !AerocraftCompatibility.IsAerocraft(aircraft)) return;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null || !owner.Managed) return;
        if (!owner.TryDecode(aircraft.Position, out var at)) return;
        if (!AerocraftLanding.TopmostSurface(owner, at.X, at.Z, out int layer)) return;
        var flight = AerocraftCompatibility.FlightOf(aircraft, owner);
        // Move the live track first, then the storage cell: both must name the same band.
        AerocraftCompatibility.Rebase(aircraft, owner, flight, layer);
        flight.HomeLayer = layer;
        aircraft.Position = owner.ToStorage(new LayerCell(layer, at.X, at.Z));
        AerocraftCompatibility.Position.Write(aircraft,
            new Vector2(aircraft.Position.x + 0.5f, aircraft.Position.z + 0.5f));
        AircraftTrace.Changed(aircraft, owner, flight, "arrived");
    }
}

/// <summary>Diagnostic breadcrumbs, capped so a loop can never flood the log.</summary>
internal static class AircraftTrace
{
    private static int emitted;
    private const int Limit = 40;

    internal static void Diagnostics(string text)
    {
        if (emitted >= Limit) return;
        emitted++;
        Log.Message("[RimLayers aerocraft] " + text);
    }

    internal static void Changed(Thing aircraft, LayerMapComponent owner, AerocraftLayers.Flight flight, string reason)
    {
        if (emitted >= Limit) return;
        emitted++;
        Log.Message($"[RimLayers aerocraft] {reason}: {aircraft.LabelShortCap} band {flight.ProxyLayer} " +
            $"(elev {owner.Elevation(flight.ProxyLayer)}, home {flight.HomeLayer}, tracked {AerocraftCompatibility.TrackedFlights}).");
    }
}

/// <summary>
/// Runs once after every active mod's assembly is loaded. A <see cref="Mod"/> constructor runs before
/// that point, so <c>AccessTools.TypeByName</c> cannot see Aerocraft's types from there; this is the
/// earliest place the adapter can bind and its <c>Prepare()</c> gates can answer true.
/// </summary>
[StaticConstructorOnStartup]
internal static class AerocraftBootstrap
{
    static AerocraftBootstrap()
    {
        try { AerocraftCompatibility.Install(); }
        catch (Exception e) { Log.Error($"[RimLayers] Aerocraft compatibility setup failed: {e}"); }
    }
}

/// <summary>
/// Re-targeting while already airborne. <c>Change_Up</c> is only reached on take-off, so a right-click
/// onto another floor during flight would otherwise move <c>TargetVPos</c> into a different band
/// while the track stayed behind — the aircraft would then write <c>Position</c> into a band its own
/// <c>ProxyLayer</c> does not name. Every destination arrives through one of these four setters, so
/// they are the single place a band change has to be committed.
/// </summary>
[HarmonyPatch]
internal static class AerocraftTargetPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        var type = AerocraftCompatibility.BodyType;
        var methods = new[]
        {
            AccessTools.Method(type, "Set_TargetVPos", new[] { typeof(Vector3) }),
            AccessTools.Method(type, "Set_TargetVPos_AndDestroy", new[] { typeof(Vector3) }),
            AccessTools.Method(type, "Set_TargetVPos_Droping", new[] { typeof(Vector3) }),
            AccessTools.Method(type, "Set_Target_FollowTargetThing", new[] { typeof(Thing) })
        };
        foreach (var method in methods)
            if (method != null) yield return method;
    }

    /// <summary>Snapshot of the target before the native body, so a refused order is not rebased.</summary>
    private static void Prefix(Thing __instance, out Vector3 __state)
    {
        __state = AerocraftCompatibility.IsAerocraft(__instance)
            ? AerocraftCompatibility.TargetPosition.Read(__instance)
            : default;
    }

    /// <summary>
    /// Runs last so every native guard has already decided whether the order is accepted; only an
    /// accepted order moves the track.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Thing __instance, Vector3 __state)
    {
        if (!AerocraftCompatibility.IsAerocraft(__instance) || !__instance.Spawned) return;
        if (!AerocraftCompatibility.IsFlying(__instance)) return; // Take-off handles the parked case.
        if (AerocraftLanding.Deferred(__instance)) return;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return;
        // Gliding and falling orders refuse a re-target by returning early. Compare the TARGET BAND
        // rather than the raw vector: an equality test on the vector silently swallowed legitimate
        // orders (the acceptance scenario caught it). A rejected order leaves the band unchanged,
        // so the band comparison is exact and does not depend on how the native code mutates it.
        if (AerocraftCompatibility.TargetBandOf(__instance, owner) ==
            AerocraftCompatibility.BandOfPoint(__state, owner)) return;
        var flight = AerocraftCompatibility.FlightOf(__instance, owner);
        int target = TargetBand(__instance, owner, flight);
        if (target == flight.ProxyLayer) return;
        if (!AerocraftCompatibility.Rebase(__instance, owner, flight, target)) return;
        AircraftTrace.Changed(__instance, owner, flight, "re-targeted");
    }

    /// <summary>The band named by the saved target point, or the current band for a local order.</summary>
    private static int TargetBand(Thing aircraft, LayerMapComponent owner, AerocraftLayers.Flight flight)
    {
        var layout = owner.Layout!;
        var target = AerocraftCompatibility.TargetPosition.Read(aircraft);
        if (target.z == 0f) return flight.ProxyLayer;
        int band = (int)Math.Floor((double)target.z / layout.Stride);
        return band < 0 || band >= layout.LayerCount ? flight.ProxyLayer : band;
    }
}
