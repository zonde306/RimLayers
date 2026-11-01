using LudeonTK;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Debug-only probe: click a powered building to see, floor by floor, why its power net is what it is.
/// </summary>
/// <remarks>
/// A "not connected to a power net" label on an upper deck has several possible causes that all look
/// identical on screen: the part is not standing on a recognised floor (so <c>TransmitsPowerNow</c> is
/// forced off), a floor exists but nothing above it counts as a support roof, or the net is fine but it
/// sits on its own deck with no riser to its neighbour. Guessing between those from a screenshot is
/// what cost the last few rounds, so this prints the actual predicate results for the clicked column.
/// </remarks>
internal static class DebugAction_LayerProbe
{
    [DebugAction("RimLayers", "Probe layer at cursor", actionType = DebugActionType.Action,
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void Probe()
    {
        if (LayerMapComponent.For(Find.CurrentMap) is not { } owner)
        {
            Log.Message("[RimLayers] probe: this map has no layer component.");
            return;
        }
        IntVec3 cell = UI.MouseCell();
        if (!cell.IsValid) return;
        Log.Message("[RimLayers] probe at " + cell + " on " + owner.map);
        if (!owner.TryDecode(cell, out var at))
        {
            Log.Message("  not inside any layer band.");
            return;
        }
        var grid = owner.map.terrainGrid;
        for (int layer = 0; layer < owner.Layout!.LayerCount; layer++)
        {
            IntVec3 probe = owner.ToStorage(new Layout.LayerCell(layer, at.X, at.Z));
            Log.Message("  layer " + layer + " (" + owner.LevelName(layer) + ") " + probe
                + ": surface=" + owner.Structure.HasSurface(probe)
                + " structural=" + owner.StructuralCells.Contains(probe)
                + " needsRoof=" + owner.Structure.HasRequiredRoof(probe)
                + " roof=" + (owner.map.roofGrid.RoofAt(probe)?.defName ?? "none")
                + " terrain=" + (grid.TerrainAt(probe)?.defName ?? "null")
                + " foundation=" + (grid.FoundationAt(probe)?.defName ?? "none")
                + " under=" + (grid.UnderTerrainAt(probe)?.defName ?? "none"));
            foreach (Thing thing in probe.GetThingList(owner.map))
            {
                if (thing is not Building building) continue;
                CompPower? power = building.PowerComp;
                CompPowerTrader? trader = building.TryGetComp<CompPowerTrader>();
                // CompPower.PowerNet is transNet ?? connectParent.transNet. Printing transNet alone
                // cannot tell "no net at all" from "connected to someone else's net", which is the
                // exact distinction this probe exists to settle.
                PowerNet? net = power?.PowerNet;
                Log.Message("    " + building.def.defName + " transmits=" + building.TransmitsPowerNow
                    + " net=" + (net?.GetHashCode().ToString() ?? "none")
                    + " transNet=" + (power?.transNet?.GetHashCode().ToString() ?? "none")
                    + " connectParent=" + (power?.connectParent?.parent?.def.defName ?? "none")
                    + " flick=" + (building.TryGetComp<CompFlickable>()?.SwitchIsOn.ToString() ?? "-")
                    + " on=" + (trader?.PowerOn.ToString() ?? "-")
                    + " riser=" + (building.TryGetComp<RimLayers.Environment.CompPowerRiser>() != null));
                string inspect = power?.CompInspectStringExtra() ?? "<none>";
                Log.Message("      inspect: " + inspect.Replace("\n", " | "));
                // A riser only joins the decks above and below when all of them sit on the SAME column.
                // The check is silent when they do not: the pipe still shows a green grid, the decks
                // simply never merge, and the only symptom is a whole deck with no power. Print the
                // column this riser would have to share, so a misplaced one is visible in one click.
                if (building.TryGetComp<RimLayers.Environment.CompPowerRiser>() is { } riserComp)
                {
                    List<string> peers = owner.Power.VerticalNeighbors(building)
                        .Select(c => owner.TryDecode(c, out var peer) ? owner.LevelName(peer.LayerId) : "?")
                        .ToList();
                    Log.Message("      riser column: (" + cell.x + "," + cell.z + ") peers="
                        + (peers.Count == 0 ? "none" : string.Join(",", peers))
                        + " switch=" + (building.TryGetComp<CompFlickable>()?.SwitchIsOn.ToString() ?? "-"));
                }
                if (net != null)
                    Log.Message("      net has: powerComps=" + net.powerComps.Count
                        + " batteryComps=" + net.batteryComps.Count
                        + " connectors=" + net.connectors.Count);
            }
        }
    }
}
