using System.IO;
using System.Text.Json;
using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Layout;
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
/// <para>The scenario needs the real pack, so it aborts with one explicit FAIL instead of pretending
/// to pass when <c>MYDE.AerocraftFramework</c> is not enabled: a silent skip would be
/// indistinguishable from a working adapter.</para>
/// </summary>
internal static partial class V1Integration
{
    private const string AerocraftDef = "MYDE_AF_RotorCraft_TwinPropeller_Base";
    private static Thing? aerocraft;
    private static IntVec3 aerocraftApron;   // upper-floor landing pad centre
    private static int aerocraftHomeBand;
    private static int aerocraftPhase;

    private static void BeginAerocraft()
    {
        Check("aerocraft-adapter-is-bound", AerocraftCompatibility.Bound, AerocraftCompatibility.Diagnostic);
        Check("aerocraft-pack-is-enabled",
            LoadedModManager.RunningModsListForReading.Any(m => m.PackageId.Equals(AerocraftCompatibility.PackageId, StringComparison.OrdinalIgnoreCase)),
            string.Join(",", LoadedModManager.RunningModsListForReading.Select(m => m.PackageId)));
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(AerocraftDef);
        Check("aerocraft-def-loads", def != null, AerocraftDef);
        if (!AerocraftCompatibility.Bound || def == null) { Finish(); return; }

        // An upper-floor apron: structural slab, supported by a roof directly below, with the
        // column above it kept clear so a vertical take-off is legal.
        aerocraftApron = At(1, 66, 66);
        foreach (IntVec3 c in new CellRect(63, 63, 8, 8))
        {
            map.roofGrid.SetRoof(At(0, c.x, c.z), RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1, c.x, c.z), RimLayersDefOf.RL_StructuralFloor);
        }
        foreach (int layer in new[] { 0 }) GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column, ThingDefOf.Steel), At(layer, 63, 63), map);
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Check("upper-apron-has-surface", owner.Structure.HasSurface(aerocraftApron));

        // Native placement path, not a hand-written spawn: the aircraft must accept the aprons.
        var report = GenConstruct.CanPlaceBlueprintAt(def, aerocraftApron, Rot4.North, map);
        Check("apron-accepts-aerocraft-placement", report.Accepted, report.Reason);
        aerocraft = GenSpawn.Spawn(ThingMaker.MakeThing(def, ThingDefOf.Steel), aerocraftApron, map, Rot4.North, WipeMode.Vanish);
        aerocraft.SetFaction(Faction.OfPlayer);
        Check("aerocraft-spawned-on-upper-apron",
            aerocraft.Spawned && owner.TryDecode(aerocraft.Position, out var at) && at.LayerId == 1,
            aerocraft?.Position.ToString() ?? "null");

        if (aerocraft == null) { Check("aerocraft-spawned", false, "spawn returned null"); Finish(); return; }
        var flight = AerocraftCompatibility.FlightOf(aerocraft, owner);
        aerocraftHomeBand = flight.ProxyLayer;
        Check("aerocraft-track-band-derived-from-geometry", flight.ProxyLayer == 1, $"band={flight.ProxyLayer}, home={flight.HomeLayer}");

        // Refuelling: the Home check must work by column, because the upper apron is never in Home.
        var refuel = new WorkGiver_Refuel_Turret();
        map.areaManager.Home[At(0, 66, 66)] = true;
        Check("upper-apron-not-in-native-home", !map.areaManager.Home[aerocraftApron]);
        Check("refuel-allowed-by-column-home", refuel.CanRefuelThing(aerocraft!));
        map.areaManager.Home[At(0, 66, 66)] = false;
        Check("refuel-refused-without-any-home-layer", !refuel.CanRefuelThing(aerocraft!));
        map.areaManager.Home[At(1, 66, 66)] = true;
        Check("refuel-allowed-when-same-floor-in-home", refuel.CanRefuelThing(aerocraft!));

        // RT-63 powers its rotors from a CompProperties_Battery, not CompRefuelable, and spawns
        // empty. Native Change_Up refuses to lift without energy, so charge it like a player would.
        var battery = aerocraft.TryGetComp<CompPowerBattery>();
        Check("aerocraft-has-a-power-cell", battery != null, battery?.Props.storedEnergyMax.ToString() ?? "none");
        battery?.SetStoredEnergyPct(1f);
        // Take-off through the real gizmo entry (Change_UpOrDown), which is what a player clicks.
        // Vertical, so the aircraft must keep its floor even though TargetVPos may hold a stale
        // point from another band.
        AccessTools.Method(AerocraftCompatibility.BodyType, "Change_UpOrDown", Type.EmptyTypes)!.Invoke(aerocraft, null);
        Check("vertical-takeoff-keeps-its-floor",
            AerocraftCompatibility.FlightOf(aerocraft!, owner).ProxyLayer == aerocraftHomeBand,
            $"band={AerocraftCompatibility.FlightOf(aerocraft!, owner).ProxyLayer}");
        // Cross-check the compiled accessor against plain reflection: a wrong field binding would
        // otherwise make every later assertion silently read a default value.
        bool viaReflection = (bool)AccessTools.Field(AerocraftCompatibility.BodyType, "If_UpOrDown")!.GetValue(aerocraft)!;
        Check("takeoff-sets-upward-intent", AerocraftCompatibility.UpOrDown.Read(aerocraft!) == viaReflection && viaReflection,
            $"accessor={AerocraftCompatibility.UpOrDown.Read(aerocraft!)}, reflection={viaReflection}");

        File.WriteAllText(Path.Combine(directory, "aerocraft-baseline.json"), JsonSerializer.Serialize(new
        {
            aircraft = aerocraft.ThingID,
            apron = aerocraftApron.ToString(),
            band = aerocraftHomeBand,
            bound = AerocraftCompatibility.Bound
        }));
        GameDataSaveLoader.SaveGame("RimLayers_V1Aerocraft");
        stage = 580; ticks = 0;
    }

    private static void AerocraftStep()
    {
        ticks++;
        var flight = AerocraftCompatibility.FlightOf(aerocraft!, owner);
        // Do not key the script on tick numbers: Is_Flying only becomes true once the native
        // warm-up counter passes its maximum (60 ticks for RT-63), so an early fixed tick would
        // exercise the parked code path instead of the airborne one.
        bool airborne = AerocraftCompatibility.IsFlying(aerocraft!);
        if (ticks > 900)
        {
            Check("aerocraft-flew-within-budget", false,
                $"phase={aerocraftPhase}, warmUp={AerocraftCompatibility.WarmUpTick.Read(aerocraft!)}, airborne={airborne}");
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
                // The aircraft's own Change_Position must keep Position inside the upper band.
                aerocraftPhase = 2;
                Check("airborne-position-stays-inside-its-band",
                    owner.TryDecode(aerocraft!.Position, out var at0) && at0.LayerId == flight.ProxyLayer,
                    $"pos={aerocraft.Position}, band={flight.ProxyLayer}");
                Check("airborne-position-matches-track",
                    Math.Abs(aerocraft.Position.z - (int)AerocraftCompatibility.Position.Read(aerocraft).y) <= 1,
                    $"pos.z={aerocraft.Position.z}, track.z={AerocraftCompatibility.Position.Read(aerocraft).y}");
                break;

            case 2:
                // Cross-floor re-target while flying: the whole track must follow the new band.
                aerocraftPhase = 3;
                AccessTools.Method(AerocraftCompatibility.BodyType, "Set_TargetVPos", new[] { typeof(Vector3) })!
                    .Invoke(aerocraft, new object[] { At(0, 70, 70).ToVector3Shifted() });
                var targetRead = AerocraftCompatibility.TargetPosition.Read(aerocraft!);
                Check("airborne-retarget-moves-track-to-target-floor", flight.ProxyLayer == 0,
                    $"band={flight.ProxyLayer}, target=({targetRead.x:0.#},{targetRead.z:0.#}), expected band 0");
                // Position is committed by the aircraft's own Change_Position, which runs on a
                // 10-tick cadence (ChangPosTickMax), so the storage cell cannot be asserted in the
                // same frame as the rebase. Phase 6 checks it after the native commit.
                break;

            case 3:
                // The stored cell follows the track through the aircraft's own Change_Position,
                // which returns immediately while Is_Static and otherwise commits on a 10-tick
                // cadence (ChangPosTickMax). Wait for a real commit instead of asserting on a frame.
                if (!AerocraftCompatibility.IsStatic(aerocraft!) &&
                    Math.Abs(aerocraft!.Position.z - (int)AerocraftCompatibility.Position.Read(aerocraft).y) <= 1)
                    aerocraftPhase = 4;
                else if (ticks % 60 == 0 && ticks < 600)
                    AircraftTrace.Diagnostics($"waiting for storage commit: pos={aerocraft!.Position}, " +
                        $"track={(int)AerocraftCompatibility.Position.Read(aerocraft).y}, static={AerocraftCompatibility.IsStatic(aerocraft)}");
                if (aerocraftPhase == 4)
                {
                    Check("retarget-keeps-single-band-for-position",
                        owner.TryDecode(aerocraft.Position, out var at1) && at1.LayerId == 0,
                        $"pos={aerocraft.Position}, band={flight.ProxyLayer}");
                    Check("retarget-storage-follows-track",
                        Math.Abs(aerocraft.Position.z - (int)AerocraftCompatibility.Position.Read(aerocraft).y) <= 1,
                        $"pos.z={aerocraft.Position.z}, track.z={AerocraftCompatibility.Position.Read(aerocraft).y}");
                }
                break;

            case 4:
                // Landing records the band it rests in as the floor "return to base" targets.
                aerocraftPhase = 5;
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Down", Type.EmptyTypes)!.Invoke(aerocraft, null);
                AerocraftCompatibility.WarmUpTick.Write(aerocraft!, 0);   // native landing counts this down
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Position", Type.EmptyTypes)!.Invoke(aerocraft, null);
                Check("landing-records-current-band-as-home", flight.HomeLayer == flight.ProxyLayer,
                    $"band={flight.ProxyLayer}, home={flight.HomeLayer}");
                Check("landed-aircraft-is-static", AerocraftCompatibility.IsStatic(aerocraft!));
                break;

            case 5:
                // Surface selection: the highest floor that owns a surface above a column, and the
                // two cell classes that must never be accepted as a touchdown point.
                aerocraftPhase = 6;
                bool foundTop = AerocraftLanding.TopmostSurface(owner, 66, 66, out int top);
                Check("topmost-surface-finds-the-upper-apron", foundTop && top == 1, $"layer={top}");
                Check("open-air-cell-is-not-a-landing-surface",
                    !AerocraftLanding.CanLand(owner, At(1, 100, 100), new IntVec2(1, 1)),
                    "empty upper air must be rejected");
                // Reserved rows sit at local z >= Height inside each band; band 1 owns [Stride, 2*Stride).
                var reserved = new IntVec3(66, 0, owner.Layout!.Stride + owner.Layout.Height + 2);
                Check("isolation-row-does-not-decode", !owner.TryDecode(reserved, out _), reserved.ToString());
                Check("isolation-row-is-not-a-landing-surface",
                    !AerocraftLanding.CanLand(owner, reserved, new IntVec2(1, 1)),
                    "reserved isolation row must be rejected");
                break;

            case 6:
                // Forced descent. All five native fall-back sites (no fuel, no power, EMP, crash,
                // self-destruct) funnel into Set_TargetVPos_Down_GlidingTakeOff, which is the single
                // choke point the adapter redirects. Drive it directly with an illegal point: an
                // empty upper-air cell. The adapter must replace it with a real surface in the
                // aircraft's own band, never a reserved row and never another floor.
                aerocraftPhase = 7;
                // Take off again so the descent order is accepted (gliding descent refuses while
                // a glide is already in progress, and a parked aircraft is already down).
                AerocraftCompatibility.WarmUpTick.Write(aerocraft!, 0);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Down", Type.EmptyTypes)!.Invoke(aerocraft, null);
                // Probe point must be inside the AIRCRAFT'S CURRENT band (it is band 0 after the
                // re-target above) but with no surface under it. Using another band's cell would
                // test the wrong thing: the redirect must stay in-band, so the probe must start
                // in-band and still be rejected.
                // Pick a cell of the aircraft's own band that is genuinely not a landing surface.
                // Band 0 has natural terrain almost everywhere, so build the rejection explicitly:
                // a cell whose vertical column is blocked by the upper apron's slab still has a
                // floor, so instead use a cell that carries an impassable building.
                // Band 0 is natural terrain almost everywhere inside the map, so build the rejection
                // where it is unambiguous: an upper-floor cell above the flat ground (RL_OpenAir has
                // no structural surface). It decodes to a real layer, which is what matters — the
                // redirect must move it onto a surface of the SAME band.
                int probeLayer = flight.ProxyLayer == 0 && owner.Layout!.LayerCount > 1 ? 1 : flight.ProxyLayer;
                IntVec3 illegalCell = IntVec3.Invalid;
                for (int x = 30; x < 90 && !illegalCell.IsValid; x++)
                    for (int z = 30; z < 90; z++)
                    {
                        var probe = owner.ToStorage(new LayerCell(probeLayer, x, z));
                        if (!owner.TryDecode(probe, out _)) continue;
                        if (!owner.Structure.HasSurface(probe)) { illegalCell = probe; break; }
                    }
                Check("forced-descent-probe-point-is-illegal", illegalCell.IsValid,
                    $"probe={illegalCell}, probeLayer={probeLayer}, aircraftBand={flight.ProxyLayer}");
                if (!illegalCell.IsValid) { aerocraftPhase = 7; break; }
                // Redirect must stay in the aircraft's own band, which for band 0 means the probe's
                // band differs — that is exactly the cross-band case the redirect must NOT follow.
                int probeBand = owner.TryDecode(illegalCell, out var pl) ? pl.LayerId : -1;
                // IMPORTANT: do not read the argument back through reflection. `MethodInfo.Invoke`
                // marshals arguments into its own buffer, so a Harmony prefix that rewrites the
                // parameter (`ref` or `__args[0]`) is invisible to the caller — the array we pass in
                // still holds the original value. In the real game the native code calls this method
                // directly and the redirect applies. The observable, production-accurate result is
                // therefore the aircraft's own TargetVPos, which the native body assigns.
                var beforeTarget = AerocraftCompatibility.TargetPosition.Read(aerocraft!);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Set_TargetVPos_Down_GlidingTakeOff",
                    new[] { typeof(Vector3) })!.Invoke(aerocraft, new object[] { illegalCell.ToVector3Shifted() });
                var claimed = AerocraftCompatibility.TargetPosition.Read(aerocraft!);
                Check("forced-descent-order-is-recorded",
                    claimed != beforeTarget, $"before=({beforeTarget.x:0.#},{beforeTarget.z:0.#}) after=({claimed.x:0.#},{claimed.z:0.#})");
                var redirectedStorage = new IntVec3((int)claimed.x, 0, (int)claimed.z);
                Check("forced-descent-redirects-to-a-legal-cell",
                    AerocraftLanding.CanLand(owner, redirectedStorage, AerocraftCompatibility.SizeOf(aerocraft!)),
                    $"probe={illegalCell}(band {probeBand}) -> redirected={redirectedStorage}");
                Check("forced-descent-stays-in-the-aircraft-band",
                    owner.TryDecode(redirectedStorage, out var fleetAt) && fleetAt.LayerId == flight.ProxyLayer,
                    $"cell={redirectedStorage}, band={flight.ProxyLayer}, probeBand={probeBand}");
                break;

            case 7:
                // Fixed-wing gliding take-off. This is the only Aerocraft class that cannot lift
                // vertically: If_NeedGlidingWhenTakeOff=true resolves a runway end before calling
                // Change_Up, which is the branch AerocraftTakeOffPatch keys on
                // (If_TuringByGlidingTakeOffOrDownNow). Nothing else in the scenario reaches it.
                aerocraftPhase = 8;
                var wingDef = DefDatabase<ThingDef>.GetNamedSilentFail("MYDE_AF_FWCraft_Bombardment");
                if (wingDef == null) { Check("fixed-wing-def-loads", false, "MYDE_AF_FWCraft_Bombardment"); break; }
                // A 26x26 clear apron on the SURFACE band: 20-tick runway plus turning room, no
                // roof, no structural slab above, so every cell passes CanLand.
                foreach (IntVec3 c in new CellRect(30, 30, 26, 26))
                {
                    var cell = At(0, c.x, c.z);
                    foreach (Thing t in cell.GetThingList(map).ToList())
                        if (t is not Pawn) { if (t.def.destroyable) t.Destroy(DestroyMode.Vanish); else t.DeSpawn(); }
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                    map.roofGrid.SetRoof(cell, null);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                // This def is not madeFromStuff (unlike RT-63), so no stuff argument.
                var wing = GenSpawn.Spawn(ThingMaker.MakeThing(wingDef), At(0, 33, 33), map, Rot4.North, WipeMode.Vanish);
                wing.SetFaction(Faction.OfPlayer);
                wing.TryGetComp<CompRefuelable>()?.Refuel(900f);
                wing.TryGetComp<CompPowerBattery>()?.SetStoredEnergyPct(1f);
                var wingFlight = AerocraftCompatibility.FlightOf(wing, owner);
                Check("fixed-wing-spawns-on-the-surface-band", wingFlight.ProxyLayer == 0, $"band={wingFlight.ProxyLayer}");
                Check("fixed-wing-needs-a-glide-run",
                    AccessTools.Field(AerocraftCompatibility.BodyType, "If_NeedGlidingWhenTakeOff")!.GetValue(wing) is true,
                    "def must be a gliding type");
                Check("runway-accepts-a-gliding-takeoff",
                    AerocraftLanding.CanLand(owner, At(0, 33, 33), wing.def.Size), "runway start");
                // Drive the real gliding command: Change_Up_GlidingTakeOff opens the targeter, so
                // instead assert the branch condition and then invoke Change_Up directly, which is
                // what that command calls once the runway end is chosen.
                AccessTools.Field(AerocraftCompatibility.BodyType, "If_TuringByGlidingTakeOffOrDownNow")!.SetValue(wing, true);
                AccessTools.Method(AerocraftCompatibility.BodyType, "Change_Up", Type.EmptyTypes)!.Invoke(wing, null);
                Check("gliding-takeoff-accepted", AerocraftCompatibility.UpOrDown.Read(wing),
                    $"up={AerocraftCompatibility.UpOrDown.Read(wing)}");
                Check("gliding-takeoff-keeps-its-band", wingFlight.ProxyLayer == 0, $"band={wingFlight.ProxyLayer}");
                wing.Destroy(DestroyMode.Vanish);
                break;

            case 8:
                aerocraftPhase = 99;
                GameDataSaveLoader.SaveGame("RimLayers_V1Aerocraft");
                Check("aerocraft-band-survives-a-save", AerocraftLayers.For(map).Of(aerocraft) != null,
                    $"tracked={AerocraftCompatibility.TrackedFlights}");
                Finish();
                break;
        }
    }

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
        Check("loaded-record-matches-track-geometry",
            flight.ProxyLayer == AerocraftCompatibility.BandOf(aerocraft, owner),
            $"record={flight.ProxyLayer}, geometry={AerocraftCompatibility.BandOf(aerocraft, owner)}, pos={aerocraft.Position}");
        Check("loaded-aircraft-position-is-decoded",
            owner.TryDecode(aerocraft.Position, out var at) && at.LayerId == flight.ProxyLayer,
            $"pos={aerocraft.Position}, band={flight.ProxyLayer}");
        Check("loaded-aircraft-keeps-a-home-floor",
            flight.HomeLayer >= 0 && flight.HomeLayer < owner.Layout!.LayerCount, $"home={flight.HomeLayer}");
        Finish();
    }
}
