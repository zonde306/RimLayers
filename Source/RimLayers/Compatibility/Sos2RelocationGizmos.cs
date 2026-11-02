using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Replaces SOS2's move / flip / rotate buttons on a multi-deck bridge with RimLayers' own relocation.
/// </summary>
/// <remarks>
/// The three buttons all end in <c>ShipInteriorMod2.MoveShip</c>, which assumes one map band. Rather
/// than correct the values that mover reads (which only moved the disagreement and split hulls in two),
/// this postfix takes the buttons off the list and yields replacements that call
/// <see cref="Sos2ShipRelocation"/>. A hull that fits in one band keeps SOS2's buttons untouched: there
/// is nothing to gain by reimplementing a move that already works.
/// </remarks>
[HarmonyPatch]
internal static class Sos2RelocationGizmos
{
    private static MethodBase? TargetMethod()
    {
        System.Type? bridge = Sos2Compatibility.ShipBridgeType;
        MethodInfo? original = bridge == null ? null : AccessTools.Method(bridge, "GetGizmos");
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, object __instance)
    {
        var kept = new List<Gizmo>();
        string? moveLabel = null, flipLabel = null, rotLabel = null;
        Command_Action? move = null, flip = null, rot = null;
        foreach (Gizmo gizmo in gizmos)
        {
            if (gizmo is Command_Action command && Sos2Compatibility.ShipBridgeType != null
                && __instance is Building bridge)
            {
                // The buttons are identified by their own labels, not by position: SOS2 yields a
                // different list on a combat console, an inactive bridge or a non-player faction.
                if (moveLabel == null)
                {
                    moveLabel = "SoS.Move".Translate().ToString();
                    flipLabel = "SoS.MoveFlip".Translate().ToString();
                    rotLabel = "SoS.MoveRot".Translate().ToString();
                }
                string label = command.defaultLabel ?? string.Empty;
                if (label == moveLabel) { move = command; continue; }
                if (label == flipLabel) { flip = command; continue; }
                if (label == rotLabel) { rot = command; continue; }
            }
            kept.Add(gizmo);
        }
        foreach (Gizmo gizmo in kept) yield return gizmo;
        if (move == null && flip == null && rot == null) yield break;
        if (__instance is not Building building || !MultiDeck(building)) yield break;

        if (move != null) yield return Replaced(move, building, 0);
        if (flip != null) yield return Replaced(flip, building, 2);
        if (rot != null) yield return Replaced(rot, building, 1);
    }

    /// <summary>Whether the hull this bridge belongs to spans more than one deck.</summary>
    private static bool MultiDeck(Building bridge)
    {
        if (!Sos2Compatibility.Active || bridge.Map == null) return false;
        LayerMapComponent? owner = LayerMapComponent.For(bridge.Map);
        if (owner?.Layout is not { LayerCount: > 1 }) return false;
        if (Sos2Compatibility.ShipCellsOf(bridge.Map) is not { } cells) return false;
        int index = Sos2Compatibility.ShipIndexAt(bridge.Map, bridge.Position);
        if (index < 0) return false;
        var layers = new HashSet<int>();
        foreach (object? key in cells.Keys)
        {
            if (key is not IntVec3 cell) continue;
            if (cells[cell] is not System.Tuple<int, int> claim || claim.Item1 != index) continue;
            if (owner.TryDecode(cell, out LayerCell logical)) layers.Add(logical.LayerId);
        }
        return layers.Count > 1;
    }

    private static Gizmo Replaced(Command_Action original, Building bridge, byte rotNum)
    {
        var command = new Command_Action
        {
            groupable = original.groupable,
            defaultLabel = original.defaultLabel,
            defaultDesc = original.defaultDesc,
            icon = original.icon,
            // A layout move costs no fuel and no lift-off check, so the replaced button is only disabled
            // for the reasons SOS2 itself already decided on.
            action = delegate
            {
                Map map = bridge.Map;
                LayerMapComponent? owner = LayerMapComponent.For(map);
                if (owner == null) return;
                Find.Targeter.BeginTargeting(
                    new TargetingParameters
                    {
                        canTargetLocations = true,
                        validator = target => target.Cell.IsValid
                            && owner.TryDecode(target.Cell, out LayerCell logical)
                            && logical.LayerId == owner.ViewedLayer,
                    },
                    target => Apply(bridge, owner, rotNum, target.Cell));
            },
        };
        if (original.Disabled) command.Disable(original.disabledReason);
        return command;
    }

    private static void Apply(Building bridge, LayerMapComponent owner, byte rotNum, IntVec3 storageCell)
    {
        if (!owner.TryDecode(storageCell, out LayerCell logical)) return;
        AcceptanceReport report = Sos2ShipRelocation.Relocate(bridge, rotNum, new IntVec3(logical.X, 0, logical.Z));
        if (report.Accepted) return;
        Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false);
    }
}
