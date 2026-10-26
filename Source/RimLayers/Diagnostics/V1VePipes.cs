using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Verifies compatibility with Vanilla Expanded pipe networks:
/// - Vanilla Chemfuel Expanded (VCHE)
/// - Vanilla Helixien Gas Expanded (VHGE)
/// - Vanilla Nutrient Paste Expanded (VNPE)
///
/// Asserts that:
/// 1. Vertical pipe risers cross layers and unify pipe networks between floors.
/// 2. Deep extractors (deepchem pumpjack, helixien pump) cannot be placed on upper floors (elevation &gt; 0).
/// 3. Deep gas deposits and geysers cannot be scattered or spawned on upper floors.
/// </summary>
internal static partial class V1Integration
{
    private static void BeginVePipes()
    {
        Check("vefpipes-start-on-a-single-layer-map", LayerMapComponent.For(map) == null);

        foreach (var p in map.mapPawns.FreeColonistsSpawned)
        {
            p.jobs.StopAll();
            p.drafter.Drafted = true;
            p.Position = new IntVec3(24, 0, 24);
            p.Notify_Teleported();
        }

        // Clear working rectangle on ground
        foreach (var c in new CellRect(18, 18, 84, 84))
        {
            foreach (var thing in c.GetThingList(map).ToList())
            {
                if (thing is not Pawn)
                {
                    if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                    else thing.DeSpawn();
                }
            }
            map.roofGrid.SetRoof(c, null);
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
            map.fogGrid.Unfog(c);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.Clear();

        var settings = RimLayersMod.Settings;
        settings.UpperLimit = 2;
        settings.UndergroundLimit = 2;
        settings.GlobalLimit = 20;
        RimLayers.Generation.LiveMapExpansion.EnsureElevation(map, 1);
        RimLayers.Generation.LiveMapExpansion.EnsureElevation(map, -1);
        owner = LayerMapComponent.For(map)!;

        Check("vefpipes-has-ground-upper-and-dug-layers",
            owner.Layout!.Elevations.SequenceEqual(new[] { 0, 1, -1 }),
            string.Join(",", owner.Layout.Elevations));

        // Prepare upper deck at (40..50, 40..50)
        var deckCenter = owner.AtElevation(1, 45, 45);
        for (int x = 38; x <= 52; x++)
        {
            for (int z = 38; z <= 52; z++)
            {
                var under = owner.AtElevation(0, x, z);
                var deck = owner.AtElevation(1, x, z);
                map.roofGrid.SetRoof(under, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(deck, RimLayersDefOf.RL_StructuralFloor);
                map.roofGrid.SetRoof(deck, RoofDefOf.RoofConstructed);
            }
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();

        // -------------------------------------------------------------
        // 1. Deep Extractor Placement Rejection on Upper Floors (Elevation > 0)
        // -------------------------------------------------------------
        var deepchemPumpDef = DefDatabase<ThingDef>.GetNamedSilentFail("VCHE_DeepchemPumpjack");
        var helixienPumpDef = DefDatabase<ThingDef>.GetNamedSilentFail("VHGE_HelixienPump");

        if (deepchemPumpDef != null)
        {
            var deckReport = owner.Structure.Validate(deepchemPumpDef, deckCenter, Rot4.North);
            Check("upper-deck-rejects-deepchem-pumpjack-structurally",
                !deckReport.Accepted && deckReport.Reason == DeepResourceScope.UpperLayerReasonKey.Translate(),
                deckReport.Reason);

            var bpReport = GenConstruct.CanPlaceBlueprintAt(deepchemPumpDef, deckCenter, Rot4.North, map, true);
            Check("upper-deck-blueprint-rejects-deepchem-pumpjack", !bpReport.Accepted, bpReport.Reason);

            var groundReport = owner.Structure.Validate(deepchemPumpDef, owner.AtElevation(0, 45, 45), Rot4.North);
            Check("ground-layer-allows-deepchem-pumpjack-structurally", groundReport.Accepted, groundReport.Reason);
        }

        if (helixienPumpDef != null)
        {
            var deckReport = owner.Structure.Validate(helixienPumpDef, deckCenter, Rot4.North);
            Check("upper-deck-rejects-helixien-pump-structurally",
                !deckReport.Accepted && deckReport.Reason == DeepResourceScope.UpperLayerReasonKey.Translate(),
                deckReport.Reason);

            var bpReport = GenConstruct.CanPlaceBlueprintAt(helixienPumpDef, deckCenter, Rot4.North, map, true);
            Check("upper-deck-blueprint-rejects-helixien-pump", !bpReport.Accepted, bpReport.Reason);

            var groundReport = owner.Structure.Validate(helixienPumpDef, owner.AtElevation(0, 45, 45), Rot4.North);
            Check("ground-layer-allows-helixien-pump-structurally", groundReport.Accepted, groundReport.Reason);
        }

        // -------------------------------------------------------------
        // 2. Underground Scatter & Spawn Rejection for Helixien Gas
        // -------------------------------------------------------------
        if (AccessTools.TypeByName("VHelixienGasE.HelixienGasHandler") is { } gasHandlerType)
        {
            var handler = map.GetComponent(gasHandlerType);
            if (handler != null)
            {
                var canScatter = AccessTools.Method(gasHandlerType, "CanScatterAt", new[] { typeof(IntVec3), typeof(Map) });
                if (canScatter != null)
                {
                    bool deckScatter = (bool)canScatter.Invoke(handler, new object[] { deckCenter, map })!;
                    Check("upper-deck-rejects-helixien-deposit-scatter", !deckScatter, $"scatter result: {deckScatter}");
                }

                var canSpawn = AccessTools.Method(gasHandlerType, "CanSpawnAt");
                if (canSpawn != null)
                {
                    var emptyList1 = Activator.CreateInstance(typeof(List<IntVec3>));
                    var emptyList2 = Activator.CreateInstance(typeof(List<IntVec3>));
                    bool deckSpawn = (bool)canSpawn.Invoke(handler, new[] { (object)deckCenter, map, emptyList1!, emptyList2! })!;
                    Check("upper-deck-rejects-helixien-deposit-spawn", !deckSpawn, $"spawn result: {deckSpawn}");
                }
            }
        }

        // -------------------------------------------------------------
        // 3. Vertical Pipe Risers & Cross-Layer Network Bridging
        // -------------------------------------------------------------
        TestPipeNetBridging("VCHE_ChemfuelNet", "VCHE_ChemfuelPipe", "RL_ChemfuelPipeRiser", 41, 41);
        TestPipeNetBridging("VCHE_DeepchemNet", "VCHE_DeepchemPipe", "RL_DeepchemPipeRiser", 44, 41);
        TestPipeNetBridging("VHGE_HelixienNet", "VHGE_HelixienPipe", "RL_HelixienPipeRiser", 47, 41);
        TestPipeNetBridging("VNPE_NutrientPasteNet", "VNPE_NutrientPastePipe", "RL_NutrientPastePipeRiser", 50, 41);

        Finish();
    }

    private static void TestPipeNetBridging(string netDefName, string pipeDefName, string riserDefName, int x, int z)
    {
        var pipeDef = DefDatabase<ThingDef>.GetNamedSilentFail(pipeDefName);
        var riserDef = DefDatabase<ThingDef>.GetNamedSilentFail(riserDefName);

        if (pipeDef == null) return;
        Check($"{netDefName}-riser-def-exists", riserDef != null, $"defName: {riserDefName}");
        if (riserDef == null) return;

        var groundRiserCell = owner.AtElevation(0, x, z);
        var upperRiserCell = owner.AtElevation(1, x, z);
        var groundPipeCell = owner.AtElevation(0, x + 1, z);
        var upperPipeCell = owner.AtElevation(1, x + 1, z);

        // Spawn pipes and risers
        var groundPipe = GenSpawn.Spawn(pipeDef, groundPipeCell, map);
        groundPipe.SetFaction(Faction.OfPlayer);

        var groundRiser = GenSpawn.Spawn(riserDef, groundRiserCell, map);
        groundRiser.SetFaction(Faction.OfPlayer);

        var upperRiser = GenSpawn.Spawn(riserDef, upperRiserCell, map);
        upperRiser.SetFaction(Faction.OfPlayer);

        var upperPipe = GenSpawn.Spawn(pipeDef, upperPipeCell, map);
        upperPipe.SetFaction(Faction.OfPlayer);

        // Get PipeNetManager and update/tick
        var groundComp = (groundPipe as ThingWithComps)?.AllComps.FirstOrDefault(c => c.GetType().Name == "CompResource");
        var upperComp = (upperPipe as ThingWithComps)?.AllComps.FirstOrDefault(c => c.GetType().Name == "CompResource");

        Check($"{netDefName}-comps-exist", groundComp != null && upperComp != null);
        if (groundComp != null && upperComp != null)
        {
            var groundNetProp = AccessTools.Property(groundComp.GetType(), "PipeNet");
            var upperNetProp = AccessTools.Property(upperComp.GetType(), "PipeNet");

            var groundNet = groundNetProp?.GetValue(groundComp);
            var upperNet = upperNetProp?.GetValue(upperComp);

            Check($"{netDefName}-pipe-nets-connected-across-floors",
                groundNet != null && upperNet != null && ReferenceEquals(groundNet, upperNet),
                $"groundNet={groundNet}, upperNet={upperNet}");

            // Verify disconnection when upper riser is removed
            upperRiser.Destroy(DestroyMode.Vanish);
            var groundNetAfter = groundNetProp?.GetValue(groundComp);
            var upperNetAfter = upperNetProp?.GetValue(upperComp);

            Check($"{netDefName}-disconnects-when-riser-removed",
                !ReferenceEquals(groundNetAfter, upperNetAfter),
                $"groundNetAfter={groundNetAfter}, upperNetAfter={upperNetAfter}");
        }

        // Clean up spawned test buildings
        groundPipe.Destroy(DestroyMode.Vanish);
        groundRiser.Destroy(DestroyMode.Vanish);
        if (upperRiser.Spawned) upperRiser.Destroy(DestroyMode.Vanish);
        upperPipe.Destroy(DestroyMode.Vanish);
    }
}
