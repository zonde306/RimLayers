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
/// <see cref="LayerMapComponent.TryDecode"/> fails.
///
/// <para>Both primitives already exist in RimLayers and are reused verbatim instead of inventing a
/// third interpretation of "there is a floor here": <see cref="StructuralSupport.HasSurface"/> for
/// the floor itself (surface terrain plus structural slab, ship deck or vehicle deck) and
/// <see cref="CombatService.OpenLaunchColumn"/> for the vertical column above it.</para>
/// </summary>
internal static class AerocraftLanding
{
    /// <summary>
    /// A parked aircraft only needs a floor under its whole footprint: a hangar is roofed, and that
    /// must not make an already-landed aircraft "illegal".
    /// </summary>
    internal static bool CanRest(LayerMapComponent owner, IntVec3 storage, IntVec2 size) =>
        Check(owner, storage, size, false);

    /// <summary>
    /// A touchdown additionally needs a clear column: an aircraft that descends into a slab or an
    /// upper floor would be standing inside geometry. Used for placement and landing targets.
    /// </summary>
    internal static bool CanLand(LayerMapComponent owner, IntVec3 storage, IntVec2 size) =>
        Check(owner, storage, size, true);

    private static bool Check(LayerMapComponent owner, IntVec3 storage, IntVec2 size, bool requireColumn)
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
    /// Nearest legal point to <paramref name="origin"/> inside its own band. An aircraft keeps its
    /// whole track in one band, so a replacement point may never name another floor.
    /// </summary>
    internal static bool TryFindNearby(LayerMapComponent owner, IntVec3 origin, IntVec2 size,
        out IntVec3 result, bool requireColumn = true)
    {
        result = IntVec3.Invalid;
        if (owner.Layout == null || !owner.TryDecode(origin, out var anchor)) return false;
        if (Check(owner, origin, size, requireColumn)) { result = origin; return true; }
        int radius = Math.Max(4, Math.Max(size.x, size.z) * 2);
        var centre = owner.ToStorage(new LayerCell(anchor.LayerId, anchor.X, anchor.Z));
        foreach (var cell in GenRadial.RadialCellsAround(centre, radius, false))
        {
            if (!owner.TryDecode(cell, out var local) || local.LayerId != anchor.LayerId) continue;
            if (!Check(owner, cell, size, requireColumn)) continue;
            result = cell;
            return true;
        }
        return false;
    }

    /// <summary>Ownership transactions (recovery, map generation) must not touch an airborne track.</summary>
    internal static bool Deferred(Thing aircraft)
    {
        if (!RecoveryCompatibility.TransactionActive) return false;
        RecoveryCompatibility.DeferFor(aircraft, 30);
        return true;
    }
}

/// <summary>
/// The whole cross-floor mechanism: an airborne aircraft rides the floor being viewed. Hooking the
/// single place where the viewed floor changes is enough, because every native subsystem resolves
/// the aircraft through <c>Thing.Position</c> afterwards.
/// </summary>
[HarmonyPatch(typeof(LayerMapComponent), "SetViewedLayer")]
internal static class AerocraftViewPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static void Postfix(LayerMapComponent __instance) => AerocraftCompatibility.Follow(__instance);
}

/// <summary>
/// `Change_Position` bounds the track with `Map.Size`, which covers every band plus the reserved
/// isolation rows, and copies `RealCurrentPosition.y` straight into `Position.z`. On a layered map
/// the correct bounds are the band's own local height, so the aircraft can neither drift into a
/// reserved row nor treat the top band as "the edge of the world".
///
/// <para>A parked aircraft still runs the native body: it re-establishes the power connection after
/// every grid write, which is why only an airborne track is re-derived here.</para>
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
        if (AerocraftLanding.Deferred(__instance)) return false;
        if (!AerocraftCompatibility.IsFlying(__instance)) return true;

        var layout = owner.Layout;
        int band = AerocraftCompatibility.BandOf(__instance, owner);
        var track = AerocraftCompatibility.Position.Read(__instance);
        double offset = (double)band * layout.Stride;
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
/// Take-off commits the floor the aircraft lifts off from. The aircraft stays in the band it was
/// parked in; from then on <see cref="AerocraftCompatibility.Follow"/> keeps an airborne flight in
/// whichever floor the player is looking at, so no take-off order has to name a destination floor.
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
        // Native Change_Up refuses to lift without fuel/energy and leaves If_UpOrDown false.
        if (!AerocraftCompatibility.UpOrDown.Read(__instance)) return;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return;
        // The floor it physically rests on is the floor it returns to.
        AerocraftCompatibility.FlightOf(__instance, owner).HomeLayer =
            AerocraftCompatibility.BandOf(__instance, owner);
    }
}

/// <summary>
/// Touchdown settles the aircraft onto the floor it is currently over. Follow only moves airborne
/// flights, so a landing resolves in whichever floor was being viewed — which is exactly the "land
/// on the floor I am looking at" rule. The settling of a stored cell onto a real surface is done by
/// <see cref="AerocraftLayers.MapComponentTick"/>, because the native descent continues for a whole
/// warm-up phase after `Change_Down` returns.
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
        AerocraftCompatibility.FlightOf(__instance, owner).HomeLayer =
            AerocraftCompatibility.BandOf(__instance, owner);
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
    /// the only form Harmony marshals back unconditionally.
    /// </summary>
    private static void Prefix(Thing __instance, object[] __args)
    {
        if (!AerocraftCompatibility.IsAerocraft(__instance) || !__instance.Spawned) return;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return;
        if (__args is not { Length: > 0 } || __args[0] is not Vector3 entry) return;
        var intended = new IntVec3((int)entry.x, 0, (int)entry.z);
        var size = AerocraftCompatibility.SizeOf(__instance);
        if (AerocraftLanding.CanLand(owner, intended, size)) return;
        if (!AerocraftLanding.TryFindNearby(owner, intended, size, out var legal))
        {
            // Nothing legal on this floor: keep the current position so the descent stays in-band
            // instead of tunnelling into a slab in a different band.
            if (!AerocraftLanding.TryFindNearby(owner, __instance.Position, size, out legal)) return;
        }
        // `legal` is a STORAGE cell, so write the storage z back verbatim: the caller passes a
        // storage-space Vector3 and the aircraft keeps the whole track in one band. Decoding it to
        // a local z here would silently relocate the touchdown to the surface band.
        __args[0] = new Vector3(legal.x + 0.5f, entry.y, legal.z + 0.5f);
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

    private static bool Prefix(BuildableDef checkingDef, IntVec3 loc, Map map, ref AcceptanceReport __result)
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
/// keyed by storage cell, so a Home zone painted on the surface never covers an upper floor — and
/// requires <c>Is_Static</c>. RimLayers answers by column instead: the aircraft must rest on a real
/// floor and its (x,z) column must be inside Home on some layer.
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
/// "Return to base". The native method aims at the saved <c>TakeOffVPos_A</c>/<c>TakeOffVPos_G</c>,
/// which this adapter deliberately leaves in the band the aircraft lifted off from — the base is a
/// real floor, not "wherever the camera happens to be". So that floor has to be on screen first;
/// bringing it into view lets <see cref="AerocraftCompatibility.Follow"/> carry the airborne
/// aircraft into the base band before the native aim resolves.
/// </summary>
[HarmonyPatch]
internal static class AerocraftGoBackPatch
{
    private static bool Prepare() => AerocraftCompatibility.Bound;

    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(AerocraftCompatibility.BodyType, "Set_GoBack", Type.EmptyTypes)
        ?? throw new MissingMethodException("Aerocraft Set_GoBack disappeared.");

    private static void Prefix(Thing __instance)
    {
        if (!AerocraftCompatibility.IsAerocraft(__instance) || !__instance.Spawned) return;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner?.Layout == null) return;
        if (!AerocraftCompatibility.IsFlying(__instance)) return;
        int home = AerocraftCompatibility.FlightOf(__instance, owner).HomeLayer;
        if (home == owner.ViewedLayer || home < 0 || home >= owner.Layout.LayerCount) return;
        // Follow runs from the view hook, so the aircraft is in the base band by the time the native
        // body below aims at the stored base point.
        AerocraftCompatibility.ShowFloor(owner, home);
    }
}

/// <summary>
/// Cross-map arrival. The native drop candidates use <c>Map.Size.z</c>, which spans every band, so
/// two of the four corners land in the topmost band and <c>Map.Center</c> lands in a middle floor.
/// Re-seat the aircraft on the floor that owns a surface above the arrival column, and remember it
/// as the new base.
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
        if (!AerocraftLanding.TryFindNearby(owner, aircraft.Position, AerocraftCompatibility.SizeOf(aircraft), out var legal))
            return;
        aircraft.Position = legal;
        AerocraftCompatibility.Position.Write(aircraft, new Vector2(legal.x + 0.5f, legal.z + 0.5f));
        AerocraftCompatibility.FlightOf(aircraft, owner).HomeLayer = at.LayerId;
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
