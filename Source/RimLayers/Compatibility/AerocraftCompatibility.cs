using System;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Vehicles;
using UnityEngine;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Aerocraft Framework (packageId <c>MYDE.AerocraftFramework</c>, workshop 3611555958) flies a
/// <see cref="Building"/> with a continuous <c>Vector2 RealCurrentPosition</c>, not a Pawn. It has
/// no <c>Vehicles</c> assembly, no pather and no altitude field of its own: <c>DrawPos.y</c> is
/// always <c>def.Altitude</c> and take-off/landing only flips <c>Move_WarmUpTick</c>.
///
/// <para><b>Model.</b> An aircraft in flight is not standing on any floor — it is in the sky. What
/// RimLayers adds is a single rule: <em>an airborne aircraft rides the floor you are looking at</em>.
/// Its continuous track is kept inside the storage band of the viewed layer, so every native
/// subsystem that reads <c>Thing.Position</c> — targeting, picking, verb range, reservations,
/// boarding, refuelling, damage — resolves against that floor without a single extra hook. A parked
/// aircraft is different: it rests on a real floor and stays in that floor's band until it lifts off
/// again.</para>
///
/// <para>Underground is the exception that needs no code: <c>Follow</c> only moves a flight when the
/// viewed layer is at or above the surface, so an aircraft overhead simply keeps its band while you
/// are downstairs, and the native section/pick filters already make it invisible and unselectable
/// there.</para>
///
/// <para>Binding is lazy and failure is non-fatal: a layout change downgrades the adapter to
/// "not bound", every hook then returns <c>true</c> and the aircraft behaves exactly as it would
/// without RimLayers installed. This is deliberate — Aerocraft is an optional third-party pack and
/// must never take RimLayers' own patch classes down with it.</para>
/// </summary>
internal static class AerocraftCompatibility
{
    internal const string PackageId = "myde.aerocraftframework";
    private const string AssemblyName = "AerocraftFramework";
    private const string BaseTypeName = "MYDE_AerocraftFramework.Building_Aerocraft_Base";
    private const string BodyTypeName = "MYDE_AerocraftFramework.Building_Aerocraft_AsBaseThing";
    private const string LeavingTypeName = "MYDE_AerocraftFramework.Skyfaller_Aerocraft_Leaving";
    private const string WorldObjectTypeName = "MYDE_AerocraftFramework.WorldObject_CrossMapThing_Flying";

    /// <summary>One bound field or property, compiled once.</summary>
    internal sealed class Accessor<T>
    {
        internal Func<object, T> Read = null!;
        internal Action<object, T> Write = null!;
    }

    internal static Type? BaseType { get; private set; }
    internal static Type? BodyType { get; private set; }
    internal static Type? LeavingType { get; private set; }
    internal static Type? WorldObjectType { get; private set; }
    internal static bool Bound { get; private set; }
    internal static string Diagnostic { get; private set; } = "Not installed";

    private static bool resolved;
    private static long trackedFlights;

    // Continuous track. Vector2.y is a STORAGE z (layer*Stride + local z) throughout this adapter.
    internal static readonly Accessor<Vector2> Position = new();
    internal static readonly Accessor<Vector3> TargetPosition = new();
    internal static readonly Accessor<Vector3> TakeOffPoint = new();
    internal static readonly Accessor<Vector3> GlideTakeOffPoint = new();
    internal static readonly Accessor<Vector3> ShadowPoint = new();
    internal static readonly Accessor<bool> UpOrDown = new();
    internal static readonly Accessor<int> WarmUpTick = new();
    internal static readonly Accessor<int> WarmUpTickMax = new();
    /// <summary>Set by the gliding take-off/descent commands; tells a run order from a vertical one.</summary>
    internal static readonly Accessor<bool> TurningByGlide = new();
    internal static Func<object, bool> IsStatic = null!;
    internal static Func<object, bool> IsFlying = null!;
    private static Func<object, ThingDef> defCall = null!;

    /// <summary>
    /// Runs from RimLayers' Mod constructor and again from the startup bootstrap. The constructor
    /// call usually cannot see Aerocraft's types yet; the bootstrap call can.
    /// </summary>
    internal static void Install()
    {
        if (Bound) return;
        // Instance-state guard only: Aerocraft's assembly is not loaded while RimLayers' Mod
        // constructor runs, so a failed first attempt must be retried by the startup bootstrap.
        if (!Active()) { Diagnostic = "Aerocraft Framework is not an active mod"; return; }
        if (!Resolve()) return;
        try
        {
            Bind();
            Log.Message("[RimLayers] " + Diagnostic);
        }
        catch (Exception e)
        {
            Diagnostic = "Aerocraft Framework layout changed; layered support disabled until the adapter is updated. " + e;
            Log.Warning("[RimLayers] " + Diagnostic);
        }
    }

    /// <summary>
    /// <c>ModLister.GetActiveModWithIdentifier</c> lower-cases the id it is given, so the constant
    /// must already be lower case (the workshop pack's real packageId is <c>MYDE.AerocraftFramework</c>).
    /// PackageId above is lower case for exactly that reason; the running-mod scan is the fallback
    /// used by <see cref="Vehicles.VehicleCompatibility"/> for the same reason.
    /// </summary>
    private static bool Active()
    {
        try
        {
            if (ModLister.GetActiveModWithIdentifier(PackageId, false) != null) return true;
            return LoadedModManager.RunningModsListForReading
                .Any(m => m.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool Resolve()
    {
        if (resolved) return BaseType != null && BodyType != null;
        BaseType = AccessTools.TypeByName(BaseTypeName);
        BodyType = AccessTools.TypeByName(BodyTypeName);
        LeavingType = AccessTools.TypeByName(LeavingTypeName);
        WorldObjectType = AccessTools.TypeByName(WorldObjectTypeName);
        resolved = BaseType != null && BodyType != null;
        if (!resolved) Diagnostic = "Aerocraft Framework assembly is not resolvable yet";
        return resolved;
    }

    private static void Bind()
    {
        if (BaseType == null || BodyType == null) throw new InvalidOperationException("Aerocraft types are not resolved.");
        if (BaseType.Assembly.GetName().Name != AssemblyName)
            throw new InvalidOperationException("Unexpected Aerocraft assembly identity.");
        if (!typeof(Building).IsAssignableFrom(BaseType))
            throw new InvalidOperationException("Aerocraft aircraft is no longer a Building.");

        BindField(Position, BaseType, "RealCurrentPosition");
        BindField(ShadowPoint, BaseType, "Shadow_Pos");
        IsFlying = BoolProperty(BaseType, "Is_Flying");
        IsStatic = BoolProperty(BaseType, "Is_Static");

        BindField(TargetPosition, BodyType, "TargetVPos");
        BindField(TakeOffPoint, BodyType, "TakeOffVPos_A");
        BindField(GlideTakeOffPoint, BodyType, "TakeOffVPos_G");
        BindField(UpOrDown, BodyType, "If_UpOrDown");
        BindField(WarmUpTick, BodyType, "Move_WarmUpTick");
        BindField(WarmUpTickMax, BodyType, "Move_WarmUpTickMax");
        BindField(TurningByGlide, BodyType, "If_TuringByGlidingTakeOffOrDownNow");

        // `Thing.def` is a public FIELD in 1.7, not a property; the property overload would throw.
        defCall = VehicleReflection.Read<ThingDef>(typeof(Thing), nameof(Thing.def));

        // A floor switch drops every non-Pawn selection so a stale build tool cannot act on the floor
        // you just left. An airborne aircraft is not attached to a floor, so it has to survive that.
        RimLayers.Presentation.LayerView.SelectionKeepers.Add(KeepSelected);

        Bound = true;
        Diagnostic = "Aerocraft Framework layered adapter bound.";
    }

    private static bool KeepSelected(object selected) =>
        selected is Thing thing && IsAerocraft(thing) && IsFlying(thing);

    private static void BindField<T>(Accessor<T> accessor, Type type, string name)
        where T : struct
    {
        var (get, set) = VehicleReflection.Field<T>(type, name);
        accessor.Read = get;
        accessor.Write = set;
    }

    private static Func<object, bool> BoolProperty(Type type, string name)
    {
        var property = FrameworkVehicleAdapter.Property(type, name);
        if (property.PropertyType != typeof(bool))
            throw new InvalidOperationException($"Aerocraft {type.Name}.{name} is no longer a bool.");
        return VehicleReflection.Call<Func<object, bool>>(property.GetMethod!);
    }

    internal static bool IsAerocraft(Thing? thing) => Bound && thing != null && BaseType!.IsInstanceOfType(thing);

    internal static bool IsAerocraftClass(Type? thingClass) =>
        Bound && thingClass != null && BaseType!.IsAssignableFrom(thingClass);

    internal static IntVec2 SizeOf(Thing thing) => defCall(thing).size;

    /// <summary>The flight record for an aircraft, created on first sight.</summary>
    internal static AerocraftLayers.Flight FlightOf(Thing aircraft, LayerMapComponent owner)
    {
        var layers = AerocraftLayers.For(owner.map);
        if (layers.Of(aircraft) is { } existing) return existing;
        var flight = layers.Track(aircraft);
        flight.HomeLayer = BandOf(aircraft, owner);
        Interlocked.Increment(ref trackedFlights);
        return flight;
    }

    /// <summary>The band the aircraft's own track currently reads as.</summary>
    internal static int BandOf(Thing aircraft, LayerMapComponent owner)
    {
        var layout = owner.Layout;
        if (layout == null || !Bound || !IsAerocraft(aircraft)) return 0;
        double z = Position.Read(aircraft).y;
        int band = (int)Math.Floor(z / layout.Stride);
        return Math.Clamp(band, 0, layout.LayerCount - 1);
    }

    /// <summary>
    /// Keeps every airborne flight in the band of the floor being viewed. This is the whole
    /// cross-floor mechanism: because the aircraft's storage cell, target and shadow all end up in
    /// the viewed band, native targeting/picking/verb/reservation code needs no adaptation.
    ///
    /// <para>Underground views are skipped on purpose. An aircraft is only ever overhead, so leaving
    /// it in its current above-surface band is both correct and invisible: the section and pick
    /// filters only admit the viewed band.</para>
    /// </summary>
    internal static void Follow(LayerMapComponent owner)
    {
        if (!Bound || owner.Layout == null) return;
        int viewed = owner.ViewedLayer;
        if (owner.Elevation(viewed) < 0) return; // Underground: the sky is not visible from here.
        var layers = AerocraftLayers.For(owner.map);
        foreach (var flight in layers.All())
        {
            var aircraft = flight.Aircraft;
            if (aircraft == null || aircraft.Destroyed || !aircraft.Spawned) continue;
            if (!IsAerocraft(aircraft) || !IsFlying(aircraft)) continue;
            if (AerocraftLanding.Deferred(aircraft)) continue;
            RebaseTo(aircraft, owner, viewed);
        }
    }

    /// <summary>
    /// Translates the parts of the flight that describe "where the aircraft is right now" into
    /// <paramref name="layer"/>: the continuous track, its drop shadow and the destination it is
    /// currently flying to. The stored cell is rewritten as well so picking and reservations agree
    /// with the track immediately rather than up to ten ticks later.
    ///
    /// <para><c>TakeOffVPos_A</c>/<c>TakeOffVPos_G</c> deliberately stay in the band the aircraft
    /// took off from: they are the "return to base" target, and the base is a real floor.</para>
    /// </summary>
    internal static bool RebaseTo(Thing aircraft, LayerMapComponent owner, int layer)
    {
        var layout = owner.Layout;
        if (layout == null || !IsAerocraft(aircraft)) return false;
        if ((uint)layer >= (uint)layout.LayerCount) return false;
        int band = BandOf(aircraft, owner);
        if (band == layer) return true;
        double shift = (double)(layer - band) * layout.Stride;

        var track = Position.Read(aircraft);
        Position.Write(aircraft, new Vector2(track.x, (float)(track.y + shift)));
        var shadow = ShadowPoint.Read(aircraft);
        // An unset shadow (all zero) means "nothing rendered yet"; never move it off the origin.
        if (shadow.x != 0f || shadow.y != 0f || shadow.z != 0f)
            ShadowPoint.Write(aircraft, new Vector3(shadow.x, shadow.y, (float)(shadow.z + shift)));
        ShiftZ(TargetPosition, aircraft, shift);

        // Commit the storage cell now. `Change_Position` would catch up on its own ten-tick cadence,
        // but until then the aircraft would still be picked up in the band we just left.
        // The LOCAL z is the same before and after the shift: both `track.y` and the band moved by
        // exactly `shift`, so adding it here as well would push the cell a whole band off.
        int x = Math.Clamp((int)track.x, 0, layout.Width - 1);
        int local = (int)Math.Floor((double)track.y - band * layout.Stride);
        local = Math.Clamp(local, 0, layout.Height - 1);
        aircraft.Position = owner.ToStorage(new RimLayers.Layout.LayerCell(layer, x, local));
        return true;
    }

    private static void ShiftZ(Accessor<Vector3> point, Thing aircraft, double shift)
    {
        var value = point.Read(aircraft);
        point.Write(aircraft, new Vector3(value.x, value.y, (float)(value.z + shift)));
    }

    /// <summary>
    /// Brings the camera (and therefore the aircraft) to <paramref name="layer"/>. Used by
    /// "return to base": the aircraft's own saved base point lives in the band it lifted off from,
    /// so the floor has to follow before the native method aims at it.
    /// </summary>
    internal static void ShowFloor(LayerMapComponent owner, int layer)
    {
        if (owner.Layout == null || (uint)layer >= (uint)owner.Layout.LayerCount) return;
        if (owner == RimLayers.Presentation.LayerView.Current)
        {
            if (RimLayers.Presentation.LayerView.Switch(owner, layer)) return;
        }
        owner.SetViewedLayer(layer);
    }

    /// <summary>Band named by an arbitrary storage-space z, or -1 when it names none.</summary>
    internal static int BandOfPoint(Vector3 point, LayerMapComponent owner)
    {
        var layout = owner.Layout;
        if (layout == null || point.z == 0f) return -1;
        int band = (int)Math.Floor((double)point.z / layout.Stride);
        return band < 0 || band >= layout.LayerCount ? -1 : band;
    }

    /// <summary>Log-only counter used by the diagnostic report.</summary>
    internal static long TrackedFlights => Interlocked.Read(ref trackedFlights);

    internal static string Describe(LayerMapComponent owner, Thing aircraft)
    {
        var flight = AerocraftLayers.For(owner.map).Of(aircraft);
        return $"band={BandOf(aircraft, owner)} viewed={owner.ViewedLayer} " +
            $"home={(flight?.HomeLayer.ToString() ?? "none")} flying={IsFlying(aircraft)}";
    }
}
