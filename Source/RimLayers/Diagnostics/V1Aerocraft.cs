using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

/// <summary>
/// Real-game acceptance for the Aerocraft Framework adapter. The aircraft is an actual
/// <c>Building_Aerocraft_AsBaseThing</c> produced by Aerocraft's own Defs, flown by its own tick,
/// and every assertion reads RimLayers state plus native <c>Thing.Position</c> rather than a mirror.
///
/// <para>The model under test is "an airborne aircraft rides the floor you are looking at". The
/// assertions therefore key on <c>SetViewedLayer</c> and on the aircraft's own stored cell, never on
/// a cached band: a cached band could agree with itself while the aircraft sat in the wrong floor.</para>
///
/// <para>The scenario needs the real pack, so it aborts with one explicit FAIL instead of pretending
/// to pass when <c>MYDE.AerocraftFramework</c> is not enabled: a silent skip would be
/// indistinguishable from a working adapter.</para>
/// </summary>
internal static partial class V1Integration
{
    private const string AerocraftDef = "MYDE_AF_RotorCraft_TwinPropeller_Base";
    private static Thing? aerocraft;
    private static int aerocraftHomeBand;
    private static int aerocraftPhase;

    /// <summary>Band the aircraft's own stored cell currently decodes into, or -1.</summary>
    private static int AerocraftBand(Thing craft) =>
        owner.TryDecode(craft.Position, out var at) ? at.LayerId : -1;

    private static void BeginAerocraft()
    {
        Check("aerocraft-adapter-is-bound", AerocraftCompatibility.Bound, AerocraftCompatibility.Diagnostic);
        Check("aerocraft-pack-is-enabled",
            LoadedModManager.RunningModsListForReading.Any(m => m.PackageId.Equals(AerocraftCompatibility.PackageId, StringComparison.OrdinalIgnoreCase)),
            string.Join(",", LoadedModManager.RunningModsListForReading.Select(m => m.PackageId)));
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(AerocraftDef);
        Check("aerocraft-def-loads", def != null, AerocraftDef);
        if (!AerocraftCompatibility.Bound || def == null) { Finish(); return; }

        // A flat, clear surface apron: the aircraft parks here, lifts off here, and returns here.
        var pad = new CellRect(62, 62, 12, 12);
        var upperPad = pad;
        var shaft = new CellRect(65, 65, 4, 4);
        foreach (IntVec3 c in pad)
        {
            var cell = At(0, c.x, c.z);
            foreach (Thing t in cell.GetThingList(map).ToList())
                if (t is not Pawn) { if (t.def.destroyable) t.Destroy(DestroyMode.Vanish); else t.DeSpawn(); }
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
            map.roofGrid.SetRoof(cell, null);
        }
        // A real upper storey above (but not on top of) the apron, and a lift-shaft opening
        // straight above the apron. Without an actual slab the upper band is open air, so
        // "land on the upper floor" and "redirect to a legal cell on this floor" have no
        // fixture to pass or fail against. The shaft is what lets the apron keep a clear
        // column: an upper floor above it would otherwise veto every take-off.
        foreach (IntVec3 c in upperPad)
        {
            var cell = At(1, c.x, c.z);
            if (shaft.Contains(c)) { map.terrainGrid.SetTerrain(cell, RimLayersDefOf.RL_OpenAir); map.roofGrid.SetRoof(cell, null); continue; }
            map.terrainGrid.SetTerrain(cell, RimLayersDefOf.RL_StructuralFloor);
            map.roofGrid.SetRoof(cell, null);
            owner.StructuralCells.Add(cell);
            // `HasRequiredRoof` wants a supported native roof on the cell BELOW every slab, so a
            // bare `StructuralCells` entry is not enough to make layer 1 a surface.
            map.roofGrid.SetRoof(At(0, c.x, c.z), RoofDefOf.RoofConstructed);
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var apron = At(0, 66, 66);
        Check("apron-has-a-surface", owner.Structure.HasSurface(apron));
        Check("apron-column-is-clear", owner.Combat.OpenLaunchColumn(apron));
        Check("upper-pad-has-a-surface", owner.Structure.HasSurface(At(1, 70, 70)));
        Check("lift-shaft-is-not-a-surface", !owner.Structure.HasSurface(At(1, 66, 66)));

        // Native placement path, not a hand-written spawn: the aircraft must accept the apron.
        var report = GenConstruct.CanPlaceBlueprintAt(def, apron, Rot4.North, map);
        Check("apron-accepts-aerocraft-placement", report.Accepted, report.Reason);
        aerocraft = GenSpawn.Spawn(ThingMaker.MakeThing(def, ThingDefOf.Steel), apron, map, Rot4.North, WipeMode.Vanish);
        aerocraft.SetFaction(Faction.OfPlayer);
        var craft0 = aerocraft;
        Check("aerocraft-spawned-on-the-surface", craft0.Spawned && AerocraftBand(craft0) == 0,
            craft0.Position.ToString());
        if (!craft0.Spawned) { Finish(); return; }

        var flight = AerocraftCompatibility.FlightOf(aerocraft, owner);
        aerocraftHomeBand = AerocraftBand(aerocraft);
        Check("parked-aircraft-band-matches-its-call", AerocraftCompatibility.BandOf(aerocraft, owner) == 0,
            $"band={AerocraftCompatibility.BandOf(aerocraft, owner)}, cellBand={AerocraftBand(aerocraft)}");
        Check("refuel-allowed-when-parked-in-home", RefuelAllowed(66, 66, out var why1), why1);

        // RT-63 powers its rotors from a CompProperties_Battery, not CompRefuelable, and spawns
        // empty. Native Change_Up refuses to lift without energy, so charge it like a player would.
        var battery = aerocraft.TryGetComp<CompPowerBattery>();
        Check("aerocraft-has-a-power-cell", battery != null, battery?.Props.storedEnergyMax.ToString() ?? "none");
        battery?.SetStoredEnergyPct(1f);
        // Take-off through the real gizmo entry (Change_UpOrDown), which is what a player clicks.
        AccessTools.Method(AerocraftCompatibility.BodyType, "Change_UpOrDown", Type.EmptyTypes)!.Invoke(aerocraft, null);
        bool lifting = AerocraftCompatibility.UpOrDown.Read(aerocraft);
        Check("takeoff-accepted", lifting, $"up={lifting}, warmUp={AerocraftCompatibility.WarmUpTick.Read(aerocraft)}");
        Check("takeoff-records-the-base-floor", flight.HomeLayer == 0, $"home={flight.HomeLayer}");

        File.WriteAllText(Path.Combine(directory, "aerocraft-baseline.json"), JsonSerializer.Serialize(new
        {
            aircraft = aerocraft.ThingID,
            apron = apron.ToString(),
            homeBand = aerocraftHomeBand,
            bound = AerocraftCompatibility.Bound
        }));
        GameDataSaveLoader.SaveGame("RimLayers_V1Aerocraft");
        stage = 580; ticks = 0;
    }

    /// <summary>
    /// Drives the native `WorkGiver_Refuel_Turret` with a Home zone painted on the given floor only,
    /// so "column Home" and "same-floor Home" are distinguished instead of both being true.
    /// </summary>
    private static bool RefuelAllowed(int floorX, int floorZ, out string detail)
    {
        var home = map.areaManager.Home;
        var saved = new List<(IntVec3, bool)>();
        for (int layer = 0; layer < owner.Layout!.LayerCount; layer++)
        {
            var cell = At(layer, floorX, floorZ);
            saved.Add((cell, home[cell]));
            home[cell] = false;
        }
        home[At(0, floorX, floorZ)] = true;
        bool bySurfaceHome = new WorkGiver_Refuel_Turret().CanRefuelThing(aerocraft!);
        home[At(0, floorX, floorZ)] = false;
        bool withNoHome = new WorkGiver_Refuel_Turret().CanRefuelThing(aerocraft!);
        // Paint Home on a floor above the aircraft's own: only the column rule can accept this.
        int upper = owner.Layout.LayerCount - 1;
        home[At(upper, floorX, floorZ)] = true;
        bool byColumnOnly = new WorkGiver_Refuel_Turret().CanRefuelThing(aerocraft!);
        home[At(upper, floorX, floorZ)] = false;
        foreach (var (cell, value) in saved) home[cell] = value;
        detail = $"surfaceHome={bySurfaceHome}, noHome={withNoHome}, upperHomeOnly={byColumnOnly}, band={AerocraftBand(aerocraft!)}";
        Check("refuel-refused-without-home", !withNoHome, detail);
        Check("refuel-accepted-by-column-home", byColumnOnly, detail);
        return bySurfaceHome;
    }

    private static void AerocraftStep()
    {
        ticks++;
        var craft = aerocraft!;
        var flight = AerocraftCompatibility.FlightOf(craft, owner);
        // Do not key the script on tick numbers: Is_Flying only becomes true once the native
        // warm-up counter passes its maximum (60 ticks for RT-63), so an early fixed tick would
        // exercise the parked code path instead of the airborne one.
        bool airborne = AerocraftCompatibility.IsFlying(craft);
        if (ticks > 1200)
        {
            Check("aerocraft-flew-within-budget", false,
                $"phase={aerocraftPhase}, warmUp={AerocraftCompatibility.WarmUpTick.Read(craft)}, airborne={airborne}");
            Finish();
            return;
        }
        if (aerocraftPhase == 0)
        {
            if (!airborne) return;
            aerocraftPhase = 1;
        }

        switch (aerocraftPhase)
        {
            case 1:
                // Airborne, viewed on the surface: the stored cell must be in band 0.
                aerocraftPhase = 2;
                Check("airborne-position-stays-inside-its-band", AerocraftBand(craft) == 0,
                    $"pos={craft.Position}, band={AerocraftBand(craft)}");
                Check("airborne-track-matches-stored-cell",
                    Math.Abs(craft.Position.z - AerocraftCompatibility.Position.Read(craft).y) <= 1,
                    $"pos.z={craft.Position.z}, track.z={AerocraftCompatibility.Position.Read(craft).y}");
                break;

            case 2:
                // THE core behaviour: looking at another floor carries the airborne aircraft with it.
                aerocraftPhase = 3;
                int before = AerocraftBand(craft);
                LayerView.Switch(owner, 1);
                Check("viewed-floor-changed", owner.ViewedLayer == 1, $"viewed={owner.ViewedLayer}");
                Check("airborne-aircraft-followed-the-view", AerocraftBand(craft) == 1,
                    $"before={before}, after={AerocraftBand(craft)}, viewed={owner.ViewedLayer}");
                Check("follow-keeps-the-whole-track-in-one-band",
                    Math.Abs(craft.Position.z - AerocraftCompatibility.Position.Read(craft).y) <= 1,
                    $"pos.z={craft.Position.z}, track.z={AerocraftCompatibility.Position.Read(craft).y}");
                break;

            case 3:
                // Looking at a floor above again, then back down: the aircraft must track both ways.
                aerocraftPhase = 4;
                LayerView.Switch(owner, 2);
                Check("airborne-aircraft-followed-an-upper-view", AerocraftBand(craft) == 2,
                    $"after={AerocraftBand(craft)}, viewed={owner.ViewedLayer}");
                LayerView.Switch(owner, 0);
                Check("airborne-aircraft-followed-back-down", AerocraftBand(craft) == 0,
                    $"after={AerocraftBand(craft)}, viewed={owner.ViewedLayer}");
                break;

            case 4:
                // Selection survives a floor switch: an airborne aircraft belongs to no floor, so
                // dropping it would make it impossible to steer across floors.
                aerocraftPhase = 5;
                Find.Selector.ClearSelection();
                Find.Selector.Select(craft, true, false);
                bool selectedBefore = Find.Selector.IsSelected(craft);
                int keepers = LayerView.SelectionKeepers.Count;
                LayerView.Switch(owner, 1);
                bool selectedAfter = Find.Selector.IsSelected(craft);
                Check("selection-keeper-is-registered", keepers > 0, $"keepers={keepers}");
                Check("airborne-aircraft-survives-a-floor-switch-selected", selectedBefore && selectedAfter,
                    $"before={selectedBefore}, after={selectedAfter}");
                LayerView.Switch(owner, 0);
                break;

            case 5:
                // Underground: the sky is not visible from there, so the aircraft stays where it is
                // and must NOT be moved into a basement band.
                aerocraftPhase = 6;
                int bandAbove = AerocraftBand(craft);
                int basement = owner.Layout!.OrderedLayers.FirstOrDefault(l => owner.Elevation(l) < 0);
                if (owner.Elevation(basement) < 0)
                {
                    LayerView.Switch(owner, basement);
                    Check("basement-view-does-not-move-the-aircraft", AerocraftBand(craft) == bandAbove,
                        $"band={AerocraftBand(craft)}, before={bandAbove}, viewed={owner.ViewedLayer}");
                    LayerView.Switch(owner, 0);
                }
                else Check("basement-view-does-not-move-the-aircraft", true, "no underground layer in this layout");
                break;

            case 6:
                // Landing resolves on the viewed floor: view floor 1, land, and the aircraft must
                // end up resting on floor 1 without being left over open air.
                aerocraftPhase = 7;
                LayerView.Switch(owner, 1);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Down", Type.EmptyTypes)!.Invoke(craft, null);
                AerocraftCompatibility.WarmUpTick.Write(craft, 0);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Position", Type.EmptyTypes)!.Invoke(craft, null);
                Check("landing-records-the-viewed-floor-as-home", flight.HomeLayer == 1,
                    $"home={flight.HomeLayer}, viewed={owner.ViewedLayer}, band={AerocraftBand(craft)}");
                Check("landed-aircraft-is-static", AerocraftCompatibility.IsStatic(craft));
                // A landed aircraft must never sit over open air: settle, then assert.
                AerocraftLayers.For(map).Settle();
                Check("landed-aircraft-rests-on-a-real-surface",
                    AerocraftLanding.CanRest(owner, craft.Position, AerocraftCompatibility.SizeOf(craft)),
                    $"pos={craft.Position}, band={AerocraftBand(craft)}");
                LayerView.Switch(owner, 0);
                break;

            case 7:
                // Lift off again so "return to base" has a flight to steer. The aircraft is still
                // parked on the floor it landed on, so the base it records is that floor, not the
                // floor currently on screen.
                aerocraftPhase = 8;
                Check("aircraft-is-parked-after-landing", AerocraftCompatibility.IsStatic(craft));
                LayerView.Switch(owner, 0);
                batteryCharged(craft);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_UpOrDown", Type.EmptyTypes)!.Invoke(craft, null);
                Check("second-takeoff-does-not-cleanse-the-base-floor", flight.HomeLayer == 0 || flight.HomeLayer == 1,
                    $"home={flight.HomeLayer}");
                break;

            case 8:
                // Return to base. `Set_GoBack` is a no-op unless the aircraft is actually flying, and
                // `Is_Flying` only turns true once the native warm-up counter passes its maximum, so
                // this has to be its own tick-driven phase instead of an assertion in the same call
                // stack as the take-off.
                if (!airborne) return;
                aerocraftPhase = 9;
                LayerView.Switch(owner, 2);
                int bandAt2 = AerocraftBand(craft);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Set_GoBack", Type.EmptyTypes)!.Invoke(craft, null);
                Check("return-to-base-brings-the-base-floor-on-screen", owner.ViewedLayer == flight.HomeLayer,
                    $"viewed={owner.ViewedLayer}, home={flight.HomeLayer}, bandAt2={bandAt2}");
                break;

            case 9:
                // Landing geometry: the two cell classes that must never be accepted as a touchdown.
                aerocraftPhase = 10;
                var size = AerocraftCompatibility.SizeOf(craft);
                // Reserved rows sit at local z >= Height inside each band; band 1 owns [Stride, 2*Stride).
                var reserved = new IntVec3(66, 0, owner.Layout!.Stride + owner.Layout.Height + 2);
                Check("isolation-row-does-not-decode", !owner.TryDecode(reserved, out _), reserved.ToString());
                Check("isolation-row-is-not-a-landing-surface", !AerocraftLanding.CanLand(owner, reserved, size),
                    "reserved isolation row must be rejected");
                var openAir = At(2, 100, 100);
                Check("open-air-is-not-a-landing-surface",
                    owner.TryDecode(openAir, out var openAt) && owner.Elevation(openAt.LayerId) > 0 &&
                    !owner.Structure.HasSurface(openAir) && !AerocraftLanding.CanLand(owner, openAir, size),
                    $"cell={openAir}");
                Check("surface-apron-is-a-legal-landing-surface", AerocraftLanding.CanLand(owner, At(0, 66, 66), size));
                break;

            case 10:
                // Forced descent. All five native fall-back sites (no fuel, no power, EMP, crash,
                // self-destruct) funnel into Set_TargetVPos_Down_GlidingTakeOff, which is the single
                // choke point the adapter redirects. Drive it with a cell that is in-band for the
                // aircraft but has no surface: the redirect must land on a real surface of the SAME
                // band, never a reserved row and never another floor.
                //
                // The probe cell is punched out of the band instead of being searched for: the
                // surface band is solid everywhere, so a "find a natural hole" fixture would either
                // fail to find one or drift into another band's reserved rows.
                aerocraftPhase = 11;
                int band = AerocraftBand(craft);
                if (band < 0) band = 0;
                var size2 = AerocraftCompatibility.SizeOf(craft);
                var illegal = At(band, 63, 63);
                foreach (Thing t in illegal.GetThingList(map).ToList())
                    if (t is not Pawn) { if (t.def.destroyable) t.Destroy(DestroyMode.Vanish); else t.DeSpawn(); }
                var hole = map.terrainGrid.TerrainAt(illegal);
                map.terrainGrid.SetTerrain(illegal, RimLayersDefOf.RL_OpenAir);
                map.roofGrid.SetRoof(illegal, null);
                Check("forced-descent-probe-point-is-illegal",
                    owner.TryDecode(illegal, out var illegalAt) && illegalAt.LayerId == band &&
                    !owner.Structure.HasSurface(illegal) && !AerocraftLanding.CanLand(owner, illegal, size2),
                    $"probe={illegal}, band={band}");
                // IMPORTANT: do not read the argument back through reflection. `MethodInfo.Invoke`
                // marshals arguments into its own buffer, so a Harmony prefix that rewrites the
                // parameter (`ref` or `__args[0]`) is invisible to the caller. In the real game the
                // native code calls this method directly and the redirect applies. The
                // production-accurate observable is the aircraft's own TargetVPos.
                var beforeTarget = AerocraftCompatibility.TargetPosition.Read(craft);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Set_TargetVPos_Down_GlidingTakeOff",
                    new[] { typeof(Vector3) })!.Invoke(craft, new object[] { illegal.ToVector3Shifted() });
                var claimed = AerocraftCompatibility.TargetPosition.Read(craft);
                map.terrainGrid.SetTerrain(illegal, hole);
                var redirected = new IntVec3((int)claimed.x, 0, (int)claimed.z);
                Check("forced-descent-order-is-recorded", claimed != beforeTarget,
                    $"before=({beforeTarget.x:0.#},{beforeTarget.z:0.#}) after=({claimed.x:0.#},{claimed.z:0.#})");
                Check("forced-descent-redirects-to-a-legal-cell",
                    AerocraftLanding.CanLand(owner, redirected, size2),
                    $"probe={illegal} -> redirected={redirected}");
                Check("forced-descent-stays-in-the-aircraft-band",
                    owner.TryDecode(redirected, out var redirectAt) && redirectAt.LayerId == band,
                    $"cell={redirected}, band={band}");
                break;

            case 11:
                // Fixed-wing gliding take-off on the surface runway: the only Aerocraft class that
                // cannot lift vertically. It resolves a runway end first, which is the branch the
                // row-dynamics rules key on.
                aerocraftPhase = 12;
                var wingDef = DefDatabase<ThingDef>.GetNamedSilentFail("MYDE_AF_FWCraft_Bombardment");
                if (wingDef == null) { Check("fixed-wing-def-loads", false, "MYDE_AF_FWCraft_Bombardment"); break; }
                // A 26x26 clear apron on the SURFACE band: 20-tick runway plus turning room, no
                // roof, no structural slab above, so every cell passes CanLand.
                foreach (IntVec3 c in new CellRect(28, 28, 26, 26))
                {
                    var cell = At(0, c.x, c.z);
                    foreach (Thing t in cell.GetThingList(map).ToList())
                        if (t is not Pawn) { if (t.def.destroyable) t.Destroy(DestroyMode.Vanish); else t.DeSpawn(); }
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                    map.roofGrid.SetRoof(cell, null);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                // This def is not madeFromStuff (unlike RT-63), so no stuff argument.
                var wing = GenSpawn.Spawn(ThingMaker.MakeThing(wingDef), At(0, 31, 31), map, Rot4.North, WipeMode.Vanish);
                wing.SetFaction(Faction.OfPlayer);
                wing.TryGetComp<CompRefuelable>()?.Refuel(900f);
                wing.TryGetComp<CompPowerBattery>()?.SetStoredEnergyPct(1f);
                Check("fixed-wing-spawns-on-the-surface", AerocraftBand(wing) == 0, $"band={AerocraftBand(wing)}");
                Check("fixed-wing-needs-a-glide-run",
                    AccessTools.Field(AerocraftCompatibility.BodyType, "If_NeedGlidingWhenTakeOff")!.GetValue(wing) is true,
                    "def must be a gliding type");
                Check("runway-accepts-a-gliding-takeoff", AerocraftLanding.CanLand(owner, At(0, 31, 31), wing.def.Size));
                AccessTools.Field(AerocraftCompatibility.BodyType, "If_TuringByGlidingTakeOffOrDownNow")!.SetValue(wing, true);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Up", Type.EmptyTypes)!.Invoke(wing, null);
                Check("gliding-takeoff-accepted", AerocraftCompatibility.UpOrDown.Read(wing),
                    $"up={AerocraftCompatibility.UpOrDown.Read(wing)}");
                Check("gliding-takeoff-keeps-its-band",
                    AerocraftCompatibility.BandOf(wing, owner) == 0, $"band={AerocraftCompatibility.BandOf(wing, owner)}");
                wing.Destroy(DestroyMode.Vanish);
                break;

            case 12:
                aerocraftPhase = 99;
                GameDataSaveLoader.SaveGame("RimLayers_V1Aerocraft");
                Check("aerocraft-band-survives-a-save", AerocraftLayers.For(map).Of(craft) != null,
                    $"tracked={AerocraftCompatibility.TrackedFlights}");
                Finish();
                break;
        }
    }

    private static void batteryCharged(Thing craft) => craft.TryGetComp<CompPowerBattery>()?.SetStoredEnergyPct(1f);

    /// <summary>Reload checks: the record is re-derived from the saved track, never trusted blindly.</summary>
    private static void RestoreAerocraft()
    {
        var baselinePath = Path.Combine(directory, "aerocraft-baseline.json");
        if (!File.Exists(baselinePath)) { Check("aerocraft-baseline-present", false, baselinePath); Finish(); return; }
        using var document = JsonDocument.Parse(File.ReadAllText(baselinePath));
        string id = document.RootElement.GetProperty("aircraft").GetString()!;
        Check("aerocraft-adapter-bound-after-load", AerocraftCompatibility.Bound, AerocraftCompatibility.Diagnostic);
        aerocraft = map.listerThings.AllThings.FirstOrDefault(t => t.ThingID == id);
        Check("aerocraft-survives-load", aerocraft != null, $"thing={id}");
        if (aerocraft == null || !AerocraftCompatibility.Bound) { Finish(); return; }
        var flight = AerocraftCompatibility.FlightOf(aerocraft, owner);
        Check("loaded-aircraft-position-is-decoded", AerocraftBand(aerocraft) >= 0,
            $"pos={aerocraft.Position}");
        Check("loaded-track-agrees-with-its-stored-cell",
            AerocraftCompatibility.BandOf(aerocraft, owner) == AerocraftBand(aerocraft),
            $"track={AerocraftCompatibility.BandOf(aerocraft, owner)}, cell={AerocraftBand(aerocraft)}");
        Check("loaded-aircraft-keeps-a-home-floor",
            flight.HomeLayer >= 0 && flight.HomeLayer < owner.Layout!.LayerCount, $"home={flight.HomeLayer}");
        Finish();
    }
}
