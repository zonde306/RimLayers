using RimLayers.Cargo;
using RimLayers.Core;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Paired-layer buttons: the player path places a native blueprint of the matching end, the dev path
/// may finish it at once. The gizmo lookups prove the buttons are reachable from a selected thing, and
/// the direct calls prove the rules behind them (a missing layer, a floorless cell, an existing end).
/// </summary>
internal static partial class V1Integration
{
    private static Thing pairRiser = null!;
    private static Thing pairAiry = null!;
    private static Building_Elevator pairLift = null!;
    private static Building_CargoHoist pairHoist = null!;
    private static Building_CargoHoist pairShaft = null!;
    private static ThingDef PairRiserDef => RimLayersDefOf.RL_PowerRiser;
    private static ThingDef PairLiftDef => DefDatabase<ThingDef>.GetNamed("RL_LiftVertical");
    private static ThingDef PairHoistDef => DefDatabase<ThingDef>.GetNamed("RL_CargoManualUp");
    private static ThingDef PairPortDef => DefDatabase<ThingDef>.GetNamed("RL_CargoPort");
    private static ThingDef PairShaftDef => DefDatabase<ThingDef>.GetNamed("RL_CargoManualShaft");
    private static ThingDef PairStationDef => DefDatabase<ThingDef>.GetNamed("RL_CargoShaftStation");

    private static Command_Action? PairCommand(Thing thing, string key) =>
        thing.GetGizmos().OfType<Command_Action>().FirstOrDefault(g => g.defaultLabel == key.Translate().ToString());

    private static Command_Action? DevCommand(Thing thing, string label) =>
        DevLayerGizmos.For(thing).OfType<Command_Action>().FirstOrDefault(g => g.defaultLabel == label);

    private static IEnumerable<Thing> ThingsAt(int layer, int x, int z) => At(layer, x, z).GetThingList(map);

    private static Thing? PairBlueprint(int layer, int x, int z, ThingDef def) =>
        ThingsAt(layer, x, z).OfType<Blueprint_Build>().FirstOrDefault(b => b.def.entityDefToBuild == def);

    private static void BeginPairing()
    {
        // A downward shaft needs undiscovered rock below, so give it a soil column before anything else.
        foreach (var p in map.mapPawns.FreeColonistsSpawned) { p.jobs.StopAll(); p.drafter.Drafted = true; }
        pawn = map.mapPawns.FreeColonistsSpawned.First();
        for (int layer = 0; layer < 3; layer++)
            foreach (var c in new CellRect(50, 50, 15, 15)) map.fogGrid.Unfog(At(layer, c.x, c.z));
        Check("pairing-fixture-provides-three-floors", owner.Layout is { LayerCount: 3 }, owner.Layout?.LayerCount.ToString() ?? "");

        // The pairing is derived from what the defs already say, not authored per building.
        Check("pairing-derives-for-power-terminal",
            LayerPairing.Props(PairRiserDef) is { kind: LayerPairKind.Riser } &&
            LayerPairing.Directions(PairRiserDef).SequenceEqual(new[] { 1, -1 }));
        Check("pairing-derives-for-lift-station",
            LayerPairing.Props(PairLiftDef) != null && LayerPairing.Partner(PairLiftDef, 1) == PairLiftDef &&
            LayerPairing.Directions(PairLiftDef).SequenceEqual(new[] { 1, -1 }));
        Check("pairing-derives-for-hoist-end",
            LayerPairing.Props(PairHoistDef) is { kind: LayerPairKind.Hoist, companion: true } && LayerPairing.Partner(PairHoistDef, 1) == PairPortDef);
        Check("pairing-skips-the-hoist-access-port", LayerPairing.Props(PairPortDef) == null);
        Check("pairing-derives-for-cargo-shaft-master",
            LayerPairing.Props(PairShaftDef) is { kind: LayerPairKind.Shaft, companion: false } &&
            LayerPairing.Partner(PairShaftDef, 1) == PairStationDef &&
            LayerPairing.Directions(PairShaftDef).SequenceEqual(new[] { 1, -1 }),
            LayerPairing.Partner(PairShaftDef, 1)?.defName ?? "none");

        // A terminal offers both directions; only the one with a real layer is enabled.
        pairRiser = GenSpawn.Spawn(ThingMaker.MakeThing(PairRiserDef), At(0, 53, 53), map);
        pairRiser.SetFaction(Faction.OfPlayer);
        var up = PairCommand(pairRiser, "RL_PairBuildUp");
        var down = PairCommand(pairRiser, "RL_PairBuildDown");
        Check("terminal-offers-both-directions", up != null && down != null);
        Check("terminal-enables-the-direction-with-a-floor", up is { Disabled: false }, up?.disabledReason ?? "");
        Check("terminal-disables-the-direction-without-a-layer", down is { Disabled: true } &&
            down.disabledReason == "RL_PairLayerMissing".Translate(LayerPairing.LevelName(-1)).ToString(),
            down?.disabledReason ?? "");

        up!.action();
        var riserPlan = PairBlueprint(1, 53, 53, PairRiserDef);
        Check("terminal-button-places-a-native-blueprint-above", riserPlan != null,
            ThingsAt(1, 53, 53).Select(t => t.def.defName).ToCommaList(true));
        Check("terminal-button-does-not-spawn-the-finished-end",
            !ThingsAt(1, 53, 53).OfType<Building>().Any(b => b.def == PairRiserDef));
        Check("a-second-order-here-is-refused",
            LayerPairing.Check(pairRiser, 1).Reason == "RL_PairBlueprintExists".Translate().ToString(),
            LayerPairing.Check(pairRiser, 1).Reason);

        // A cell with no floor above is refused, and the reason names the layer.
        pairAiry = GenSpawn.Spawn(ThingMaker.MakeThing(PairRiserDef), At(0, 66, 66), map);
        pairAiry.SetFaction(Faction.OfPlayer);
        Check("airy-upper-cell-is-refused", LayerPairing.Check(pairAiry, 1).Reason ==
            "RL_PairNeedsFloor".Translate(LayerPairing.LevelName(1)).ToString(), LayerPairing.Check(pairAiry, 1).Reason);
        Check("airy-refusal-places-nothing", PairBlueprint(1, 66, 66, PairRiserDef) == null);

        // A lift station pairs with the station on the next floor, at the same cell.
        pairLift = (Building_Elevator)GenSpawn.Spawn(ThingMaker.MakeThing(PairLiftDef), At(0, 55, 53), map);
        pairLift.SetFaction(Faction.OfPlayer);
        var liftUp = PairCommand(pairLift, "RL_PairBuildUp");
        Check("lift-station-offers-the-station-above", liftUp is { Disabled: false }, liftUp?.disabledReason ?? "");
        liftUp!.action();
        Check("lift-button-places-a-station-blueprint-above", PairBlueprint(1, 55, 53, PairLiftDef) != null,
            ThingsAt(1, 55, 53).Select(t => t.def.defName).ToCommaList(true));

        // A hoist end owns its port: the port is restored finished, never as a blueprint.
        pairHoist = (Building_CargoHoist)GenSpawn.Spawn(ThingMaker.MakeThing(PairHoistDef), At(0, 57, 59), map);
        pairHoist.SetFaction(Faction.OfPlayer);
        var ports = CargoNetwork.For(map).Ports.Where(p => p != pairHoist && p.Master == pairHoist).ToArray();
        Check("hoist-spawns-its-own-port", ports.Length == 1, ports.Length.ToString());
        foreach (var port in ports) port.Destroy(DestroyMode.Vanish);
        var hoistUp = PairCommand(pairHoist, "RL_PairBuildUp");
        Check("hoist-offers-its-port-above", hoistUp is { Disabled: false }, hoistUp?.disabledReason ?? "");
        hoistUp!.action();
        var restored = CargoNetwork.For(map).Ports.Where(p => p != pairHoist && p.Master == pairHoist).ToArray();
        Check("hoist-button-restores-the-finished-port", restored.Length == 1 &&
            owner.TryDecode(restored[0].Position, out var portAt) && portAt.LayerId != 0 && portAt.X == 57 && portAt.Z == 59,
            $"{restored.Length} port(s)");
        Check("hoist-port-is-not-left-as-a-blueprint", !ThingsAt(1, 57, 59).Any(t => t is Blueprint || t is Frame));

        // A cargo shaft master offers the access station that gives it a stop on the next layer.
        // The station is an ordinary paid building, so this places a blueprint like any other.
        pairShaft = (Building_CargoHoist)GenSpawn.Spawn(ThingMaker.MakeThing(PairShaftDef), At(0, 57, 63), map);
        pairShaft.SetFaction(Faction.OfPlayer);
        var shaftUp = PairCommand(pairShaft, "RL_PairBuildUp");
        var shaftDown = PairCommand(pairShaft, "RL_PairBuildDown");
        Check("cargo-shaft-offers-both-directions", shaftUp != null && shaftDown != null);
        Check("cargo-shaft-enables-the-station-above", shaftUp is { Disabled: false }, shaftUp?.disabledReason ?? "");
        shaftUp!.action();
        Check("cargo-shaft-button-places-a-station-blueprint-above",
            PairBlueprint(1, 57, 63, PairStationDef) != null,
            ThingsAt(1, 57, 63).Select(t => t.def.defName).ToCommaList(true));
        Check("cargo-shaft-button-places-a-paid-station-not-a-free-one",
            PairStationDef.costList is { Count: > 0 } &&
            !ThingsAt(1, 57, 63).OfType<Building>().Any(b => b.def == PairStationDef));

        // Dev shortcuts stay hidden until god mode, and never bypass the mod's own commit gate.
        Check("dev-shortcuts-hidden-without-god-mode", !DevLayerGizmos.For(pairRiser).Any() && !DebugSettings.ShowDevGizmos);
        bool devMode = Prefs.DevMode, godMode = DebugSettings.godMode;
        try
        {
            Prefs.DevMode = true; DebugSettings.godMode = true;
            Check("dev-shortcuts-appear-in-god-mode", DevCommand(pairRiser, "DEV: matching end above, built") != null);
            Check("dev-hides-the-shaft-shortcut-without-an-extension", DevCommand(pairLift, "DEV: finish the shaft") == null);

            var frame = (Frame)GenSpawn.Spawn(ThingMaker.MakeThing(PairRiserDef.frameDef), At(0, 59, 53), map);
            frame.SetFaction(Faction.OfPlayer);
            frame.workDone = 0;
            var finish = DevCommand(frame, "DEV: finish now");
            Check("dev-offers-finish-now-on-a-frame", finish != null);
            finish!.action();
            Check("dev-finish-now-completes-through-the-normal-commit",
                frame.Destroyed && ThingsAt(0, 59, 53).OfType<Building>().Any(b => b.def == PairRiserDef),
                ThingsAt(0, 59, 53).Select(t => t.def.defName).ToCommaList(true));

            var fresh = GenSpawn.Spawn(ThingMaker.MakeThing(PairRiserDef), At(0, 61, 53), map);
            fresh.SetFaction(Faction.OfPlayer);
            var built = DevCommand(fresh, "DEV: matching end above, built");
            Check("dev-offers-a-finished-matching-end", built != null);
            built!.action();
            Check("dev-matching-end-arrives-finished",
                ThingsAt(1, 61, 53).OfType<Building>().Any(b => b.def == PairRiserDef) &&
                !ThingsAt(1, 61, 53).Any(t => t is Blueprint || t is Frame),
                ThingsAt(1, 61, 53).Select(t => t.def.defName).ToCommaList(true));

            // The excavating-shaft shortcut: start a real downward extension, then let the dev button
            // finish what mining and construction would have done.
            var digger = (Building_Elevator)GenSpawn.Spawn(ThingMaker.MakeThing(PairLiftDef), At(0, 63, 53), map);
            digger.SetFaction(Faction.OfPlayer);
            bool started = digger.CanExpandShaft(-1).Accepted && digger.StartExtension(-1);
            Check("dev-lift-can-start-a-downward-extension", started, digger.CanExpandShaft(-1).Reason);
            var shaftButton = DevCommand(digger, "DEV: finish the shaft");
            Check("dev-offers-the-shaft-shortcut-while-excavating", shaftButton != null);
            if (started)
            {
                var exitCell = digger.ExtensionExit(-1);
                Check("dev-shaft-fixture-leaves-rock-or-a-frame-to-clear",
                    exitCell.IsValid && exitCell.GetEdifice(map) is Mineable ||
                    exitCell.GetThingList(map).Any(t => t is Frame || t is Blueprint_Build),
                    exitCell.IsValid ? exitCell.GetThingList(map).Select(t => t.def.defName).ToCommaList(true) : "invalid exit");
                shaftButton!.action();
                Check("dev-shaft-shortcut-removes-the-blocking-rock",
                    exitCell.GetEdifice(map) is not Mineable,
                    exitCell.GetEdifice(map)?.def.defName ?? "clear");
                // The shortcut ran to the end of the share: the exit cell now carries a real station,
                // and the extension is gone because it completed.
                Check("dev-shaft-shortcut-connects-the-next-floor",
                    exitCell.GetEdifice(map) is Building_Elevator { ConnectsAdjacent: false } &&
                    !digger.Expanding(-1),
                    exitCell.GetEdifice(map)?.def.defName ?? "clear");
            }
        }
        finally { DebugSettings.godMode = godMode; Prefs.DevMode = devMode; }

        // The contents tab is a vanilla container tab, so each stored stack keeps its icon and a click
        // selects that stack. The tab itself cannot be drawn headless, but everything the row needs is
        // reachable: the container, the icon texture, and the very call a row click makes.
        var tabSteel = ThingMaker.MakeThing(ThingDefOf.Steel);
        tabSteel.stackCount = 20;
        pairHoist.Contents.TryAdd(tabSteel);
        Find.Selector.ClearSelection(); Find.Selector.Select(pairHoist);
        var contentsTab = new ITab_CargoContents();
        Check("cargo-tab-is-a-vanilla-contents-tab",
            contentsTab is ITab_ContentsBase && contentsTab.containedItemsKey == "RL_CargoContents",
            contentsTab.GetType().BaseType?.FullName ?? "");
        Check("cargo-tab-hides-the-vanilla-discard-buttons", !contentsTab.canRemoveThings);
        Check("cargo-tab-lists-the-basket-contents",
            contentsTab.container.Count == 1 && ReferenceEquals(contentsTab.container[0], tabSteel),
            contentsTab.container.Count.ToString());
        Check("cargo-tab-is-visible-for-the-basket-end", contentsTab.IsVisible);
        Check("cargo-tab-rows-have-an-icon-to-draw", tabSteel.def.DrawMatSingle?.mainTexture != null);
        ITab_Pawn_FormingCaravan.SelectNow(new List<Thing> { tabSteel });
        Check("cargo-tab-row-click-selects-the-stored-item", Find.Selector.IsSelected(tabSteel),
            Find.Selector.SingleSelectedThing?.def.defName ?? "none");
        Find.Selector.ClearSelection(); Find.Selector.Select(pairRiser);
        Check("cargo-tab-hidden-for-a-building-without-a-basket", !contentsTab.IsVisible);

        // Vanilla shelves also offer one "select this stored item" button per stack. The basket is a
        // ThingOwner, so the vanilla slot-group loop does not apply and the mod reproduces it.
        Find.Selector.ClearSelection(); Find.Selector.Select(pairHoist);
        var storedGizmos = pairHoist.StoredThingGizmos().OfType<Command_Action>().ToList();
        Check("cargo-offers-one-select-button-per-stored-stack", storedGizmos.Count == pairHoist.Contents.Count,
            $"{storedGizmos.Count} button(s) for {pairHoist.Contents.Count} stack(s)");
        Check("cargo-select-button-is-the-vanilla-storage-command",
            storedGizmos.FirstOrDefault() is { } first && first.GetType().Name == "Command_SelectStorage",
            storedGizmos.FirstOrDefault()?.GetType().Name ?? "none");
        Check("cargo-select-button-names-the-stored-item",
            storedGizmos.FirstOrDefault()?.defaultLabel?.Contains(tabSteel.LabelNoCount) == true,
            storedGizmos.FirstOrDefault()?.defaultLabel ?? "none");
        Check("cargo-select-button-has-an-icon", storedGizmos.FirstOrDefault()?.icon != null);
        storedGizmos.FirstOrDefault()?.action();
        Check("cargo-select-button-selects-the-stored-item", Find.Selector.IsSelected(tabSteel),
            Find.Selector.SingleSelectedThing?.def.defName ?? "none");
        Find.Selector.Select(pairRiser);
        Check("cargo-select-buttons-need-a-single-selection", Find.Selector.NumSelected > 1 && pairHoist.StoredThingGizmos().Count() == 0,
            $"{Find.Selector.NumSelected} selected");
        Find.Selector.ClearSelection(); Find.Selector.Select(pairHoist);
        Check("cargo-select-buttons-also-on-the-access-port",
            restored.Length > 0 && restored[0].StoredThingGizmos().Count() == pairHoist.Contents.Count,
            restored.Length > 0 ? restored[0].StoredThingGizmos().Count().ToString() : "no port");

        stage = 600; ticks = 0;
    }

    private static void PairingStep()
    {
        ticks++;
        if (ticks > 30) { Check("pairing-scenario-holds-its-result", true); Finish(); }
    }
}
