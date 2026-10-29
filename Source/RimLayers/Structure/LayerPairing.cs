using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimLayers.Cargo;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Environment;
using RimLayers.Layout;
using RimLayers.Mountains;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Structure;

/// <summary>Where the pairing came from, for diagnostics and for the derived registration.</summary>
public enum LayerPairKind
{
    None,
    /// <summary>A power terminal or pipe riser: its own partner, above and below.</summary>
    Riser,
    /// <summary>A lift station that forms a shaft with the station on the adjacent layer.</summary>
    Lift,
    /// <summary>A cargo hoist end: paired with the access port its CargoProperties points at.</summary>
    Hoist,
}

/// <summary>
/// Marks a building that only does its job when the same column is occupied on the layer directly
/// above (+1) or below (-1): power terminals, pipe risers, lift stations, hoist ends.
///
/// <para>The extension only declares the pairing. The button places a <b>native blueprint</b> of the
/// partner through <see cref="GenConstruct.PlaceBlueprintForBuild"/>, so the other end costs its own
/// materials, is hauled and built by the normal jobs, and completes through vanilla construction. The
/// one exception is <see cref="companion"/>: a partner the mod owns rather than the player (a hoist's
/// access port), which is spawned finished, exactly as the hoist itself would have spawned it.</para>
///
/// <para>Nothing here opens layers. A missing layer or a spot without a floor is reported, never
/// conjured: the target must already exist and be clear before a blueprint is worth placing.</para>
/// </summary>
public sealed class LayerPairProperties : DefModExtension
{
    /// <summary>Partner buildable; null means "this same def".</summary>
    public ThingDef? partner;
    /// <summary>Directions to offer. 1 = the layer above, -1 = the layer below.</summary>
    public List<int> directions = new() { 1 };
    /// <summary>The partner is a mod-owned helper: spawn it finished instead of as a blueprint.</summary>
    public bool companion;
    /// <summary>Set by the derived registration; not authored in XML.</summary>
    public LayerPairKind kind;
}

/// <summary>
/// Rules and actions for the paired-layer buttons. Everything except <see cref="Place"/> and
/// <see cref="PlaceCompanion"/> is a side-effect free query, so it is safe to call from a gizmo that
/// rebuilds every frame.
/// </summary>
internal static class LayerPairing
{
    internal static LayerPairProperties? Props(BuildableDef? def) =>
        def is ThingDef thing ? thing.GetModExtension<LayerPairProperties>() : null;

    internal static ThingDef? Partner(BuildableDef? def, int direction) =>
        Props(def) is { } props && props.directions.Contains(direction) ? props.partner ?? def as ThingDef : null;

    internal static IEnumerable<int> Directions(BuildableDef? def)
    {
        if (Props(def)?.directions is not { } declared) yield break;
        var seen = new HashSet<int>();
        foreach (int direction in declared)
            if (LayerPairMath.IsVertical(direction) && seen.Add(direction)) yield return direction;
    }

    internal static bool IsCompanion(BuildableDef? def) => Props(def)?.companion == true;

    /// <summary>
    /// The material the source will be built from, so the partner is quoted in the same one. A
    /// blueprint stores its own choice; a finished building exposes it through <c>Stuff</c>.
    /// </summary>
    internal static ThingDef? StuffOf(Thing source) =>
        source is Blueprint_Build blueprint ? blueprint.stuffToUse : source.Stuff;

    internal static string LevelName(int elevation) =>
        elevation < 0 ? "RL_UndergroundLevel".Translate(-elevation).ToString()
                      : "RL_SurfaceLevel".Translate(elevation + 1).ToString();

    /// <summary>Elevation of the layer the thing stands on, or null when it is not on a layered map.</summary>
    internal static int? ElevationOf(Thing thing)
    {
        if (!thing.Spawned) return null;
        var owner = LayerMapComponent.For(thing.Map);
        return owner?.Layout != null && owner.TryDecode(thing.Position, out var logical) ? owner.Elevation(logical.LayerId) : null;
    }

    /// <summary>
    /// Rotation for the mirrored footprint. A square footprint can face either way, so it keeps the
    /// source's facing and its interaction cell; a rectangular one keeps its long side on the same axis.
    /// </summary>
    internal static Rot4 RotationOf(Thing source, CellRect area) =>
        !source.def.rotatable || source.def.size.x == source.def.size.z
            ? source.Rotation
            : LayerPairMath.LongSideHorizontal(area.Width, area.Height) ? Rot4.North : Rot4.East;

    /// <summary>
    /// The footprint translated onto the adjacent layer's band. Rows are re-based by the difference of
    /// the two band origins, never by an offset added to an already offset row.
    /// </summary>
    internal static CellRect? Shift(LayerMapComponent owner, CellRect footprint, int direction)
    {
        if (owner.Layout == null) return null;
        if (!owner.TryDecode(footprint.Cells.First(), out var logical)) return null;
        if (!owner.Layout.TryGetAdjacentLayer(logical.LayerId, direction, out int layer)) return null;
        int fromBand = owner.LayerRect(logical.LayerId).minZ;
        int toBand = owner.LayerRect(layer).minZ;
        return new CellRect(footprint.minX, LayerPairMath.ShiftZ(footprint.minZ, fromBand, toBand),
            footprint.Width, footprint.Height);
    }

    /// <summary>
    /// Everything that must hold before anything may be placed: the layer exists, the mirrored cells
    /// have a floor, no end is already there, and the game itself accepts the blueprint (edge area,
    /// fog, terrain, occupancy and every place worker, this mod's own included). Pure query.
    /// </summary>
    internal static AcceptanceReport Check(Thing source, int direction)
    {
        var def = source.def.entityDefToBuild ?? source.def;
        var partner = Partner(def, direction);
        if (partner == null) return "RL_PairNotPaired".Translate();
        if (!source.Spawned) return "RL_NoSurface".Translate();
        var map = source.Map;
        var owner = LayerMapComponent.For(map);
        if (owner?.Layout == null) return "RL_RequiresLayerMap".Translate();

        var footprint = source.OccupiedRect();
        if (!owner.TryDecode(footprint.Cells.First(), out var logical)) return "RL_NoSurface".Translate();
        int target = owner.Elevation(logical.LayerId) + direction;
        if (target > LayerLayout.MaxElevation) return "RL_TopLayer".Translate();
        if (target < LayerLayout.MinElevation) return "RL_PairBottomLayer".Translate();
        if (!owner.Layout.TryGetLayerAtElevation(target, out _))
            return "RL_PairLayerMissing".Translate(LevelName(target));

        var shifted = Shift(owner, footprint, direction);
        if (shifted == null) return "RL_PairLayerMissing".Translate(LevelName(target));
        var area = shifted.Value;
        foreach (var cell in area)
        {
            if (!cell.InBounds(map)) return "RL_NoSurface".Translate();
            if (!owner.Structure.HasSurface(cell)) return "RL_PairNeedsFloor".Translate(LevelName(target));
            foreach (var thing in cell.GetThingList(map))
                if (thing.def == partner || thing.def.entityDefToBuild == partner)
                    return thing.def == partner ? "RL_PairBuilt".Translate() : "RL_PairBlueprintExists".Translate();
        }

        var rot = RotationOf(source, area);
        if (IsCompanion(def))
            return GenSpawn.CanSpawnAt(partner, area.CenterCell, map, rot)
                ? AcceptanceReport.WasAccepted : "RL_PairPortBlocked".Translate();
        // A def with neither a blueprint nor a frame cannot be queued for construction at all.
        if (partner.blueprintDef == null && partner.frameDef == null)
            return "RL_PairNotPaired".Translate();
        var report = GenConstruct.CanPlaceBlueprintAt(partner, area.CenterCell, rot, map, false, null, null, StuffOf(source));
        return report.Accepted ? AcceptanceReport.WasAccepted : "RL_PairBlocked".Translate(report.Reason);
    }

    /// <summary>
    /// Places the partner as a native blueprint on the adjacent layer. The caller has already run
    /// <see cref="Check"/>, so this only re-resolves the target and spawns.
    /// </summary>
    internal static Blueprint_Build? Place(Thing source, int direction)
    {
        var partner = Partner(source.def.entityDefToBuild ?? source.def, direction);
        var owner = LayerMapComponent.For(source.Map);
        if (partner == null || owner?.Layout == null) return null;
        var area = Shift(owner, source.OccupiedRect(), direction);
        if (area == null) return null;
        var blueprint = GenConstruct.PlaceBlueprintForBuild(partner, area.Value.CenterCell, source.Map,
            RotationOf(source, area.Value), source.Faction, StuffOf(source));
        if (blueprint != null)
        {
            owner.Connectors.Invalidate();
            owner.Invalidate(area.Value.CenterCell, LayerChangeKind.Structure);
        }
        return blueprint;
    }

    /// <summary>
    /// Spawns a mod-owned companion (a hoist's access port) on the adjacent layer and wires it to its
    /// master, mirroring what <c>Building_CargoHoist.SpawnSetup</c> does when the layer exists but the
    /// port does not. No blueprint and no cost: the hoist already pays for both ends.
    /// </summary>
    internal static bool PlaceCompanion(Thing source, int direction)
    {
        var partner = Partner(source.def.entityDefToBuild ?? source.def, direction);
        var owner = LayerMapComponent.For(source.Map);
        if (partner == null || owner?.Layout == null || source is not Building_CargoHoist master) return false;
        var area = Shift(owner, source.OccupiedRect(), direction);
        if (area == null) return false;
        var port = (Building_CargoPort)ThingMaker.MakeThing(partner);
        port.Master = master;
        port.SetFactionDirect(source.Faction);
        GenSpawn.Spawn(port, area.Value.CenterCell, source.Map, RotationOf(source, area.Value));
        CargoNetwork.For(source.Map).Reconnect();
        owner.Connectors.Invalidate();
        return true;
    }

    /// <summary>The button body: check, act, report. Shared so the player and dev paths cannot drift.</summary>
    internal static void Run(Thing source, int direction)
    {
        var report = Check(source, direction);
        if (!report.Accepted)
        {
            Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false);
            return;
        }
        bool placed = IsCompanion(source.def.entityDefToBuild ?? source.def)
            ? PlaceCompanion(source, direction)
            : Place(source, direction) != null;
        if (!placed) Messages.Message("RL_PairPlaceFailed".Translate(), MessageTypeDefOf.RejectInput, false);
    }
}

/// <summary>
/// The player-facing command: one "build the matching end" button per direction the def declares.
/// Implemented once for terminals, risers, lift stations and hoists because the def owns the pairing.
/// </summary>
[StaticConstructorOnStartup]
public static class PairedLayerGizmos
{
    private static readonly Texture2D PartnerIcon = ContentFinder<Texture2D>.Get("Things/Building/RimLayers/LiftVertical");

    /// <summary>Buttons for a finished building.</summary>
    internal static IEnumerable<Gizmo> For(Thing thing)
    {
        if (thing is not Building || !thing.Spawned || thing.Faction != Faction.OfPlayer) return Enumerable.Empty<Gizmo>();
        if (LayerMapComponent.For(thing.Map)?.Layout == null) return Enumerable.Empty<Gizmo>();
        return Buttons(thing);
    }

    /// <summary>The same buttons on a blueprint or frame, so both ends can be queued before either is built.</summary>
    internal static IEnumerable<Gizmo> ForPlan(Thing plan)
    {
        if (plan.Faction != Faction.OfPlayer || !plan.Spawned) return Enumerable.Empty<Gizmo>();
        if (LayerMapComponent.For(plan.Map)?.Layout == null) return Enumerable.Empty<Gizmo>();
        if (LayerPairing.IsCompanion(plan.def.entityDefToBuild ?? plan.def)) return Enumerable.Empty<Gizmo>();
        return Buttons(plan);
    }

    private static IEnumerable<Gizmo> Buttons(Thing source)
    {
        var def = source.def.entityDefToBuild ?? source.def;
        if (LayerPairing.Props(def) == null) yield break;
        var elevation = LayerPairing.ElevationOf(source);
        if (elevation == null) yield break;
        foreach (int direction in LayerPairing.Directions(def))
        {
            var partner = LayerPairing.Partner(def, direction);
            if (partner == null) continue;
            var report = LayerPairing.Check(source, direction);
            var command = new Command_Action
            {
                defaultLabel = (direction > 0 ? "RL_PairBuildUp" : "RL_PairBuildDown").Translate(),
                defaultDesc = "RL_PairBuildHelp".Translate(partner.label, LayerPairing.LevelName(elevation.Value + direction)),
                icon = PartnerIcon,
                action = () => LayerPairing.Run(source, direction),
            };
            if (!report.Accepted) command.Disable(report.Reason);
            yield return command;
        }
    }
}

/// <summary>
/// Development shortcuts. They appear only under <see cref="DebugSettings.ShowDevGizmos"/> (dev mode
/// plus god mode), so no player sees them and no save keeps a half-applied shortcut. Each one drives
/// the mod's own commit path rather than bypassing it, so a shortcut cannot produce a state the normal
/// game would have refused.
/// </summary>
public static class DevLayerGizmos
{
    internal static IEnumerable<Gizmo> For(Thing thing)
    {
        if (!DebugSettings.ShowDevGizmos || !thing.Spawned) yield break;

        if (thing is Blueprint or Frame)
            yield return new Command_Action
            {
                defaultLabel = "DEV: finish now",
                defaultDesc = "Finish this construction instantly, as if a builder had completed it.",
                action = () => FinishConstruction(thing),
            };

        if (VerticalMining.Rock(thing) is { } rock)
            yield return new Command_Action
            {
                defaultLabel = "DEV: clear the way",
                defaultDesc = $"Destroy the rock at {rock.Position} that blocks this connector, then advance the shaft.",
                action = () => ClearBlockage(thing),
            };

        // An excavating lift shaft: rock, landing and station done in one press.
        if (thing is Building_Elevator station && (station.Expanding(-1) || station.Expanding(1)))
            yield return new Command_Action
            {
                defaultLabel = "DEV: finish the shaft",
                defaultDesc = "Clear the rock, open the landing and complete the station on the next floor, as if the whole excavation and build job had run.",
                action = () => FinishShaft(station),
            };

        var def = thing.def.entityDefToBuild ?? thing.def;
        foreach (int direction in LayerPairing.Directions(def))
        {
            if (LayerPairing.Partner(def, direction) == null) continue;
            yield return new Command_Action
            {
                defaultLabel = direction > 0 ? "DEV: matching end above, built" : "DEV: matching end below, built",
                defaultDesc = "Place the matching end and complete it at once. For testing only.",
                action = () => BuildPartnerNow(thing, direction),
            };
        }
    }

    private static void FinishConstruction(Thing thing)
    {
        var map = thing.Map;
        if (thing is Frame frame)
        {
            var worker = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            if (worker == null)
            {
                Messages.Message("RL_PairNeedsWorker".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // CompleteConstruction needs a real worker and runs the normal commit pipeline, so every
            // mod-side construction check still applies.
            frame.workDone = frame.WorkToBuild;
            frame.CompleteConstruction(worker);
            return;
        }
        if (thing.def.entityDefToBuild is not ThingDef buildable) return;
        var stuff = LayerPairing.StuffOf(thing);
        SpawnCompleted(buildable, stuff, thing.Faction ?? Faction.OfPlayer, thing.Position, thing.Rotation, map);
        thing.Destroy(DestroyMode.Cancel);
    }

    private static void ClearBlockage(Thing thing)
    {
        if (VerticalMining.Rock(thing) is { } rock)
        {
            var at = rock.Position;
            // Exactly what a miner's final strike does, including the ore it drops.
            rock.Destroy(DestroyMode.KillFinalize);
            if (LayerMapComponent.For(thing.Map) is { } owner && owner.Connectors.TouchesColumn(at)) owner.Connectors.Invalidate();
        }
        if (thing is Building_Elevator station) station.TickExtensions();
    }

    /// <summary>
    /// One shaft extension driven to completion the way the real loop would over many ticks: mine the
    /// exit, let <c>TickExtensions</c> open the landing and place the native blueprint, then finish that
    /// plan. The station therefore arrives through the same commit checks as a manned build.
    /// </summary>
    private static void FinishShaft(Building_Elevator station)
    {
        var map = station.Map;
        var worker = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        foreach (int direction in new[] { -1, 1 })
        {
            if (!station.Expanding(direction)) continue;
            var exit = station.ExtensionExit(direction);
            if (!exit.IsValid) continue;
            var work = station.WorkCell(direction);

            // Rock first: a frame cannot commit while its exit is still mineable rock.
            foreach (var cell in new[] { exit, work })
                if (cell.IsValid && cell.GetEdifice(map) is Mineable rock) rock.Destroy(DestroyMode.KillFinalize);

            station.TickExtensions();
            foreach (var plan in exit.GetThingList(map).ToArray())
            {
                if (plan is Frame frame)
                {
                    if (worker == null) continue;
                    frame.workDone = frame.WorkToBuild;
                    frame.CompleteConstruction(worker);
                }
                else if (plan is Blueprint_Build blueprint && blueprint.def.entityDefToBuild is ThingDef buildable)
                {
                    SpawnCompleted(buildable, blueprint.stuffToUse, blueprint.Faction ?? Faction.OfPlayer,
                        blueprint.Position, blueprint.Rotation, map);
                    blueprint.Destroy(DestroyMode.Cancel);
                }
            }
            station.TickExtensions();
        }
        if (LayerMapComponent.For(map) is { } owner)
        {
            owner.Connectors.Invalidate();
            owner.Invalidate(station.Position, LayerChangeKind.Structure);
        }
    }

    private static void BuildPartnerNow(Thing thing, int direction)
    {
        if (LayerPairing.IsCompanion(thing.def.entityDefToBuild ?? thing.def))
        {
            LayerPairing.Run(thing, direction);
            return;
        }
        var report = LayerPairing.Check(thing, direction);
        if (!report.Accepted)
        {
            Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false);
            return;
        }
        var blueprint = LayerPairing.Place(thing, direction);
        if (blueprint == null)
        {
            Messages.Message("RL_PairPlaceFailed".Translate(), MessageTypeDefOf.RejectInput, false);
            return;
        }
        if (blueprint.def.entityDefToBuild is not ThingDef buildable) return;
        var map = blueprint.Map;
        var pos = blueprint.Position;
        var rot = blueprint.Rotation;
        var stuff = blueprint.stuffToUse;
        var faction = blueprint.Faction;
        SpawnCompleted(buildable, stuff, faction ?? Faction.OfPlayer, pos, rot, map);
        blueprint.Destroy(DestroyMode.Cancel);
    }

    private static void SpawnCompleted(ThingDef buildable, ThingDef? stuff, Faction faction, IntVec3 pos, Rot4 rot, Map map)
    {
        var completed = ThingMaker.MakeThing(buildable, stuff);
        completed.SetFactionDirect(faction);
        GenSpawn.Spawn(completed, pos, map, rot);
    }
}

/// <summary>
/// Every gizmo chain, for buildings, blueprints and frames alike, ends in <c>Thing.GetGizmos</c>,
/// because each override starts with <c>base.GetGizmos()</c>. One postfix there therefore reaches all
/// three without patching virtual methods that third-party mods also override.
/// </summary>
[HarmonyPatch(typeof(Thing), nameof(Thing.GetGizmos))]
internal static class PairedLayerGizmosPatch
{
    private static void Postfix(Thing __instance, ref IEnumerable<Gizmo> __result)
    {
        if (__instance is not (Building or Blueprint or Frame)) return;
        var extra = new List<Gizmo>();
        extra.AddRange(__instance is Building ? PairedLayerGizmos.For(__instance) : PairedLayerGizmos.ForPlan(__instance));
        extra.AddRange(DevLayerGizmos.For(__instance));
        extra.AddRange(BlockedStairGizmos.For(__instance));
        if (extra.Count == 0) return;
        __result = (__result ?? Enumerable.Empty<Gizmo>()).Concat(extra);
    }
}

/// <summary>
/// Derives the pairing for defs that already describe it elsewhere, so no XML repeats itself: every
/// riser (power terminal or pipe riser) pairs with itself above and below, and every hoist end pairs
/// with the access port its own <c>CargoProperties.direction</c> points at. Lift stations and any
/// other def that wants a different partner declare the extension in XML.
/// </summary>
[StaticConstructorOnStartup]
internal static class LayerPairingBootstrap
{
    static LayerPairingBootstrap()
    {
        int derived = 0;
        foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
        {
            if (def.GetModExtension<LayerPairProperties>() != null) continue;
            if (Derive(def) is not { } properties) continue;
            (def.modExtensions ??= new List<DefModExtension>()).Add(properties);
            derived++;
        }
        if (derived > 0) Log.Message($"[RimLayers] {derived} def(s) registered as paired-layer buildings.");
    }

    private static LayerPairProperties? Derive(ThingDef def)
    {
        // HasComp(Type) compares against comps[i].compClass, which is the Comp type, not the properties type.
        // Pipe risers use one of two comp families: Dubs-style nets carry CompLayerPort, Vanilla
        // Expanded-style nets carry CompResourcePort. Both bridge one layer up and down.
        if (def.HasComp(typeof(CompPowerRiser)) || def.HasComp(typeof(CompLayerPort)) ||
            def.HasComp(typeof(CompResourcePort)))
            return new LayerPairProperties
            {
                kind = LayerPairKind.Riser,
                directions = new List<int> { 1, -1 },
            };
        if (def.GetModExtension<CargoProperties>() is { direction: not 0, shaft: false, station: false } cargo
            && DefDatabase<ThingDef>.GetNamedSilentFail("RL_CargoPort") is { } port)
            return new LayerPairProperties
            {
                kind = LayerPairKind.Hoist,
                partner = port,
                directions = new List<int> { cargo.direction },
                companion = true,
            };
        return null;
    }
}
