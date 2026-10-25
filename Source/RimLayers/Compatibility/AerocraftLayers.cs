using RimLayers.Core;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Per-map record of the Aerocraft flights RimLayers has to steer. The continuous track itself
/// (<c>RealCurrentPosition</c>) stays authoritative for x/z and for the band; this component only
/// remembers which floor each aircraft took off from, so "return to base" has a floor to go back to.
///
/// <para>An airborne aircraft does not belong to a floor: it rides the layer you are looking at.
/// That is why no band is stored here — a saved band could contradict the track after a save made
/// while the camera was on another floor, and the geometry is what every native subsystem reads.</para>
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
        /// <summary>Floor the aircraft last took off from; "return to base" flies back to it.</summary>
        public int HomeLayer;

        public void ExposeData()
        {
            Scribe_References.Look(ref Aircraft, "aerocraft");
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

    internal IReadOnlyList<Flight> All() => flights;

    /// <summary>
    /// Snaps every PARKED aircraft onto a real surface of the floor it is resting on. An airborne
    /// flight is never moved: it belongs to no floor and is re-seated by
    /// <see cref="AerocraftCompatibility.Follow"/> as the view changes.
    ///
    /// <para>This is what makes "land on the floor I am looking at" safe: the track arrives in the
    /// viewed band, and the stored cell is then resolved against that band's own surfaces instead of
    /// being left over open air or inside a reserved isolation row.</para>
    /// </summary>
    public void Settle()
    {
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null || flights.Count == 0) return;
        foreach (var flight in flights)
        {
            var aircraft = flight.Aircraft;
            if (aircraft == null || aircraft.Destroyed || !aircraft.Spawned) continue;
            if (!AerocraftCompatibility.IsAerocraft(aircraft)) continue;
            if (AerocraftCompatibility.IsFlying(aircraft)) continue;
            var size = AerocraftCompatibility.SizeOf(aircraft);
            if (AerocraftLanding.CanRest(owner, aircraft.Position, size)) continue;
            // A parked aircraft only needs a floor, not a clear column: it may rest under a roof.
            if (!AerocraftLanding.TryFindNearby(owner, aircraft.Position, size, out var legal, requireColumn: false))
                continue;
            aircraft.Position = legal;
            AerocraftCompatibility.Position.Write(aircraft, new UnityEngine.Vector2(legal.x + 0.5f, legal.z + 0.5f));
            flight.HomeLayer = owner.TryDecode(legal, out var at) ? at.LayerId : flight.HomeLayer;
        }
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
    /// Re-seats every airborne aircraft on the floor being viewed once the map is complete, so a save
    /// taken while the camera was upstairs cannot leave a flight parked in a band nobody can see.
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
            flight.HomeLayer = Math.Clamp(flight.HomeLayer, 0, owner.Layout.LayerCount - 1);
        }
        AerocraftCompatibility.Follow(owner);
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
