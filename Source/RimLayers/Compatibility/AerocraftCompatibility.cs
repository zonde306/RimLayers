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
/// <para>On a layered map its <c>Change_Position</c> writes
/// <c>new IntVec3((int)RealCurrentPosition.x, Position.y, (int)RealCurrentPosition.y)</c>, so the
/// aircraft is pinned to storage band 0. This adapter therefore uses the same contract as
/// <see cref="SteelFlightAdapter"/>: the continuous track stays entirely inside one storage band and
/// every logical height change is a whole-track rebase by <c>(newLayer - oldLayer) * Stride</c>.
/// Aerocraft derives all of its geometry from <c>TargetVPos - DrawPos</c> and
/// <c>GetVector3_By_AngleFlat(DrawPos, ...)</c>, so translating every saved track point by the same
/// amount is invisible to its own simulation and to its rendering (base texture, gun turret,
/// decoration and shadow all take x/z from <c>DrawPos</c>/<c>Shadow_Pos</c>).</para>
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

        Bound = true;
        Diagnostic = "Aerocraft Framework layered adapter bound.";
    }

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

    /// <summary>
    /// The band the aircraft's track currently lives in. A freshly loaded save has no record yet,
    /// so the band is derived from the track itself; that cannot disagree with its own geometry.
    /// </summary>
    internal static AerocraftLayers.Flight FlightOf(Thing aircraft, LayerMapComponent owner)
    {
        var layers = AerocraftLayers.For(owner.map);
        if (layers.Of(aircraft) is { } existing) return existing;
        var flight = layers.Track(aircraft);
        int band = BandOf(aircraft, owner);
        flight.ProxyLayer = band;
        flight.HomeLayer = band;
        Interlocked.Increment(ref trackedFlights);
        return flight;
    }

    internal static int BandOf(Thing aircraft, LayerMapComponent owner)
    {
        var layout = owner.Layout;
        if (layout == null || !Bound || !IsAerocraft(aircraft)) return 0;
        double z = Position.Read(aircraft).y;
        int band = (int)Math.Floor(z / layout.Stride);
        return Math.Clamp(band, 0, layout.LayerCount - 1);
    }

    /// <summary>
    /// Moves the live track into <paramref name="layer"/>. Only the points that describe
    /// "where the aircraft physically is" move: the continuous track and its drop shadow.
    ///
    /// <para><c>TargetVPos</c> already carries the destination band — it is the value the band was
    /// derived from, so translating it would push the destination one band past the intended floor.
    /// <c>TakeOffVPos_A</c>/<c>TakeOffVPos_G</c> deliberately stay behind in the band the aircraft
    /// took off from: <c>Set_GoBack</c> reuses them verbatim, and re-targeting that point is what
    /// brings the aircraft home through this same method. Shifting them would make "return to base"
    /// a no-op that lands on the current floor instead.</para>
    /// </summary>
    internal static bool Rebase(Thing aircraft, LayerMapComponent owner, AerocraftLayers.Flight flight, int layer)
    {
        var layout = owner.Layout;
        if (layout == null || !IsAerocraft(aircraft)) return false;
        if ((uint)layer >= (uint)layout.LayerCount) return false;
        if (layer == flight.ProxyLayer) return true;
        double shift = (double)(layer - flight.ProxyLayer) * layout.Stride;

        var track = Position.Read(aircraft);
        Position.Write(aircraft, new Vector2(track.x, (float)(track.y + shift)));
        var shadow = ShadowPoint.Read(aircraft);
        // An unset shadow (all zero) means "nothing rendered yet"; never move it off the origin.
        if (shadow.x != 0f || shadow.y != 0f || shadow.z != 0f)
            ShadowPoint.Write(aircraft, new Vector3(shadow.x, shadow.y, (float)(shadow.z + shift)));

        flight.ProxyLayer = layer;
        return true;
    }

    private static void ShiftZ(Accessor<Vector3> point, Thing aircraft, double shift)
    {
        var value = point.Read(aircraft);
        point.Write(aircraft, new Vector3(value.x, value.y, (float)(value.z + shift)));
    }

    /// <summary>Band named by an arbitrary storage-space z.</summary>
    internal static int BandOfPoint(Vector3 point, LayerMapComponent owner)
    {
        var layout = owner.Layout;
        if (layout == null || point.z == 0f) return -1;
        int band = (int)Math.Floor((double)point.z / layout.Stride);
        return band < 0 || band >= layout.LayerCount ? -1 : band;
    }

    /// <summary>Band the aircraft's saved destination names, or -1 when it names none.</summary>
    internal static int TargetBandOf(Thing aircraft, LayerMapComponent owner)
    {
        if (!Bound || !IsAerocraft(aircraft)) return -1;
        return BandOfPoint(TargetPosition.Read(aircraft), owner);
    }

    /// <summary>Rewrites a storage cell's local z into the given layer's band.</summary>
    internal static IntVec3 InBand(IntVec3 storage, LayerMapComponent owner, int layer)
    {
        if (owner.Layout == null || !owner.TryDecode(storage, out var at)) return storage;
        return owner.ToStorage(new RimLayers.Layout.LayerCell(layer, at.X, at.Z));
    }

    /// <summary>Log-only counter used by the diagnostic report.</summary>
    internal static long TrackedFlights => Interlocked.Read(ref trackedFlights);
}
