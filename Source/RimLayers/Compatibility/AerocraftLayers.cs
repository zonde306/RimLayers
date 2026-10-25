using RimLayers.Core;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Per-map record of which storage band each Aerocraft aircraft is parked in, plus the floor its
/// "return to base" command belongs to. The continuous track itself (Aerocraft's own
/// <c>RealCurrentPosition</c>) stays authoritative for x/z; this component only owns the band.
///
/// <para>Aerocraft has no height field at all, so the band cannot be re-derived after a rebase from
/// anything but the saved track. It is therefore saved explicitly and corrected on load from
/// <c>RealCurrentPosition.y</c>, so a record and its geometry can never drift apart.</para>
///
/// <para>Instantiation is free: <see cref="Map"/>'s constructor walks
/// <c>typeof(MapComponent).AllSubclassesNonAbstract()</c> and adds every non-<c>CustomMapComponent</c>
/// with an <c>Activator.CreateInstance(type, map)</c>, and <c>Map.ExposeData</c> persists the
/// component list. No Def entry is needed.</para>
/// </summary>
public sealed class AerocraftLayers : MapComponent
{
    public sealed class Flight : IExposable
    {
        public Thing Aircraft = null!;
        /// <summary>Storage band the continuous track currently lives in.</summary>
        public int ProxyLayer;
        /// <summary>Band the aircraft last took off from; "return to base" targets this floor.</summary>
        public int HomeLayer;

        public void ExposeData()
        {
            Scribe_References.Look(ref Aircraft, "aerocraft");
            Scribe_Values.Look(ref ProxyLayer, "aerocraftProxyLayer", 0);
            Scribe_Values.Look(ref HomeLayer, "aerocraftHomeLayer", 0);
        }
    }

    private List<Flight> flights = new();
    private readonly Dictionary<int, Flight> index = new();

    public AerocraftLayers(Map map) : base(map) { }

    public Flight? Of(Thing? thing)
    {
        if (thing == null) return null;
        return index.GetValueOrDefault(thing.thingIDNumber);
    }

    internal Flight Track(Thing thing)
    {
        if (Of(thing) is { } existing) return existing;
        var flight = new Flight { Aircraft = thing };
        flights.Add(flight);
        index.Add(thing.thingIDNumber, flight);
        return flight;
    }

    internal void Forget(Thing thing)
    {
        if (Of(thing) is not { } flight) return;
        index.Remove(thing.thingIDNumber);
        flights.Remove(flight);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref flights, "aerocraftFlights", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            flights ??= new();
            index.Clear();
            foreach (var flight in flights)
                if (flight.Aircraft != null) index[flight.Aircraft.thingIDNumber] = flight;
        }
    }

    /// <summary>
    /// Re-derives every band from the saved track once the map is complete. The geometry is what the
    /// rest of the game reads through <c>Thing.Position</c>, so it wins over the saved number.
    /// </summary>
    public override void FinalizeInit()
    {
        base.FinalizeInit();
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null || flights.Count == 0) return;
        for (int i = flights.Count - 1; i >= 0; i--)
        {
            var flight = flights[i];
            if (flight.Aircraft == null || flight.Aircraft.Destroyed)
            {
                index.Remove(flight.Aircraft?.thingIDNumber ?? -1);
                flights.RemoveAt(i);
                continue;
            }
            flight.ProxyLayer = AerocraftCompatibility.BandOf(flight.Aircraft, owner);
            flight.HomeLayer = Math.Clamp(flight.HomeLayer, 0, owner.Layout.LayerCount - 1);
        }
    }

    public override void MapComponentTick()
    {
        base.MapComponentTick();
        if (flights.Count == 0) return;
        // Only a handful of aircraft exist per map, and this runs twice a second.
        if (Find.TickManager.TicksGame % 30 != 0) return;
        for (int i = flights.Count - 1; i >= 0; i--)
        {
            var flight = flights[i];
            if (flight.Aircraft != null && !flight.Aircraft.Destroyed && flight.Aircraft.Spawned) continue;
            if (flight.Aircraft != null) index.Remove(flight.Aircraft.thingIDNumber);
            flights.RemoveAt(i);
        }
    }

    internal static AerocraftLayers For(Map map) => map.GetComponent<AerocraftLayers>();
}
