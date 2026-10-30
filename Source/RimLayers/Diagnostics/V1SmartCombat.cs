using System.Text.Json;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Structure;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

/// <summary>
/// Smart Combat (Immersive Warfare) allocates <c>OrdinaryRoadMap.offsets</c>, a private
/// <c>NativeArray&lt;ushort&gt;</c> sized to <c>cellIndices.NumGridCells</c>, the first time a colonist
/// takes an ordinary job. The generic expansion scan refuses any unregistered native per-cell buffer,
/// so a stair silently failed to create a floor on every map where a colonist had already walked
/// around: "Unmapped native per-cell buffer: SmartCombat.OrdinaryRoadMap.offsets".
///
/// <para>The adapter releases that grid inside the transaction instead of growing it, because Smart
/// Combat's own <c>PrepareSafeBatch</c> already rebuilds from scratch whenever the length no longer
/// matches. This scenario proves the guard is installed, that the grid is really the blocker when it
/// is missing, that a stair now creates the floor, and that the grid comes back at the new size.</para>
/// </summary>
internal static partial class V1Integration
{
    private static Map? smartCombatMap;
    private static Pawn? smartCombatPawn;
    private static Frame? smartCombatFrame;
    private static MapComponent? smartCombatComponent;
    private static int smartCombatPhase;
    private static bool smartCombatBatchChanged;
    private static readonly IntVec3 smartCombatSite = new(35, 0, 35);

    /// <summary>
    /// Reads a boxed <c>NativeArray&lt;T&gt;</c> without a generic constraint: <c>IsCreated</c> is the
    /// idempotent guard the adapter relies on, and <c>Length</c> is what Smart Combat compares against
    /// <c>NumGridCells</c> to decide it must rebuild.
    /// </summary>
    private static bool TryReadNative(object? buffer, out bool created, out int length)
    {
        created = false;
        length = 0;
        if (buffer == null) return false;
        var type = buffer.GetType();
        created = type.GetProperty("IsCreated", typeof(bool))?.GetValue(buffer) is true;
        length = type.GetProperty("Length", typeof(int))?.GetValue(buffer) as int? ?? 0;
        return true;
    }

    private static ushort[] ReadNativeUShorts(object buffer)
    {
        if (!TryReadNative(buffer, out var created, out var length) || !created) return Array.Empty<ushort>();
        var indexer = buffer.GetType().GetProperty("Item", new[] { typeof(int) })
            ?? throw new InvalidOperationException($"{buffer.GetType().Name} has no per-index accessor.");
        var values = new ushort[length];
        for (int i = 0; i < length; i++) values[i] = (ushort)indexer.GetValue(buffer, new object[] { i })!;
        return values;
    }

    private static void BeginSmartCombat()
    {
        var construction = DefDatabase<WorkTypeDef>.GetNamed("Construction");
        pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(construction));
        pawn.jobs.StopAll();
        pawn.DeSpawn();
        map = MakeUnmanagedMap(BootstrapTile(), 120, 137);
        Current.Game.CurrentMap = map;
        BootstrapSite(map);
        GenSpawn.Spawn(pawn, new IntVec3(33, 0, 33), map);
        pawn.drafter.Drafted = false;
        pawn.skills.GetSkill(SkillDefOf.Construction).Level = 20;
        pawn.needs.food.CurLevelPercentage = 1;
        pawn.needs.rest.CurLevelPercentage = 1;
        foreach (var work in DefDatabase<WorkTypeDef>.AllDefs) pawn.workSettings.SetPriority(work, 0);
        pawn.workSettings.SetPriority(construction, 1);
        map.roofGrid.SetRoof(smartCombatSite, RoofDefOf.RoofConstructed);
        smartCombatMap = map;
        smartCombatPawn = pawn;
        Check("smart-combat-fixture-starts-unexpanded",LayerMapComponent.For(map)==null&&map.Size==new IntVec3(120,1,137));
        if (!SmartCombatCompatibility.Active)
        {
            Check("smart-combat-adapter-is-installed",false,"Smart Combat is not loaded; run with -SmartCombat.");
            Finish();
            return;
        }
        Check("smart-combat-adapter-is-installed",true);
        smartCombatComponent = SmartCombatCompatibility.ComponentOn(map);
        Check("smart-combat-ordinary-road-component-is-attached",smartCombatComponent!=null,
            $"components={string.Join(",",map.components.Select(c=>c.GetType().Name))}");
        if (smartCombatComponent == null) { Finish(); return; }
        // The whole report is about this state: the grid exists before the first expansion.
        Check("ordinary-road-grid-is-lazy-until-a-job-starts",
            TryReadNative(SmartCombatCompatibility.GridOn(smartCombatComponent),out var created,out var length)&&!created,
            $"created={created}, length={length}");
        Check("unexpanded-stair-expansion-is-accepted",StairLayerBootstrap.Validate(map,smartCombatSite,Rot4.North).Accepted,
            StairLayerBootstrap.Validate(map,smartCombatSite,Rot4.North).Reason);
        // Ask Smart Combat to run its own batch instead of writing its private fields by hand.
        smartCombatBatchChanged = SmartCombatCompatibility.TryPrepareBatch(smartCombatComponent, out var allocated);
        Check("smart-combat-can-be-asked-to-allocate-its-grid",smartCombatBatchChanged,
            "AccessTools could not bind PrepareSafeBatch on the resolved type.");
        Check("allocating-the-grid-changes-native-path-costs",allocated,
            "PrepareSafeBatch reported no change while allocating the grid.");
        Check("ordinary-road-grid-allocates-at-the-unexpanded-size",
            TryReadNative(SmartCombatCompatibility.GridOn(smartCombatComponent),out created,out length)&&created&&length==map.cellIndices.NumGridCells,
            $"created={created}, length={length}, cells={map.cellIndices.NumGridCells}");
        var def = RimLayersDefOf.RL_Stairs;
        smartCombatFrame = (Frame)ThingMaker.MakeThing(def.frameDef);
        smartCombatFrame.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(smartCombatFrame, smartCombatSite, map);
        foreach (var cost in def.CostListAdjusted(null))
        {
            var material = ThingMaker.MakeThing(cost.thingDef);
            material.stackCount = cost.count;
            smartCombatFrame.resourceContainer.TryAdd(material);
        }
        smartCombatFrame.workDone = smartCombatFrame.WorkToBuild;
        smartCombatPhase = 0;
        stage = 602;
        ticks = 0;
    }

    private static void SmartCombatStep()
    {
        var road = smartCombatComponent!;
        var target = smartCombatMap!;
        if (smartCombatPhase == 0)
        {
            // The exact path the report failed on: a finished stair frame runs the real expansion.
            Check("fixture-grid-is-live-immediately-before-the-expansion",
                TryReadNative(SmartCombatCompatibility.GridOn(road),out var createdBefore,out var lengthBefore)&&createdBefore&&lengthBefore==target.cellIndices.NumGridCells,
                $"created={createdBefore}, length={lengthBefore}, cells={target.cellIndices.NumGridCells}");
            int cellsBefore = target.cellIndices.NumGridCells;
            smartCombatFrame!.CompleteConstruction(smartCombatPawn!);
            owner = LayerMapComponent.For(target)!;
            Check("completed-stair-creates-the-second-floor",
                owner != null && owner.Layout is { Height: 137, LayerCount: 2 } && target.cellIndices.NumGridCells > cellsBefore,
                $"cells={cellsBefore}->{target.cellIndices.NumGridCells}, layers={owner?.Layout?.LayerCount}");
            Check("expansion-was-not-refused-for-a-stair",
                !(LayerMapComponent.Raw(target) is { } raw && raw.BlockedStairs.Contains(smartCombatSite)));
            Check("ordinary-road-grid-is-released-by-the-transaction",
                TryReadNative(SmartCombatCompatibility.GridOn(road),out var createdAfter,out var lengthAfter)&&!createdAfter,
                $"created={createdAfter}, length={lengthAfter}");
            Check("released-grid-is-not-a-short-unresized-buffer",
                !(createdAfter && lengthAfter != target.cellIndices.NumGridCells),
                $"created={createdAfter}, length={lengthAfter}, cells={target.cellIndices.NumGridCells}");
            Check("stair-keeps-its-upper-landing-on-the-new-floor",
                owner!.Structure.HasSurface(owner.ToStorage(new LayerCell(1, smartCombatSite.x, smartCombatSite.z))));
            smartCombatPhase = 1;
            ticks = 0;
            return;
        }
        if (smartCombatPhase == 1)
        {
            // Smart Combat's next path batch self-heals: the missing grid is rebuilt at the new size.
            bool batches = SmartCombatCompatibility.TryPrepareBatch(road, out var changed);
            Check("smart-combat-rebuilds-its-grid-at-the-expanded-size",batches,
                "AccessTools could not bind PrepareSafeBatch after the expansion.");
            Check("rebuilt-grid-matches-the-expanded-cell-count",
                TryReadNative(SmartCombatCompatibility.GridOn(road),out var created,out var length)&&created&&length==target.cellIndices.NumGridCells,
                $"created={created}, length={length}, cells={target.cellIndices.NumGridCells}");
            Check("rebuilt-grid-was-recomputed-not-zero-filled",changed,
                "PrepareSafeBatch reported no change; the rebuilt grid may be a blank allocation.");
            var costs = ReadNativeUShorts(SmartCombatCompatibility.GridOn(road)!);
            Check("rebuilt-grid-holds-real-off-road-costs",costs.Any(v=>v!=0),
                $"distinct={string.Join(",",costs.Distinct().OrderBy(v=>v).Take(6))}, cells={costs.Length}");
            smartCombatPhase = 2;
            ticks = 0;
            return;
        }
        if (smartCombatPhase == 2)
        {
            // The report also covers maps that already have floors: expanding again with a live grid
            // must release it just as well. This is the same call Dubs compatibility uses.
            Check("grid-is-live-again-before-the-second-expansion",
                TryReadNative(SmartCombatCompatibility.GridOn(road),out var createdBefore,out _)&&createdBefore);
            int cellsBefore = target.cellIndices.NumGridCells;
            LiveMapExpansion.EnsureElevation(target, 2);
            owner = LayerMapComponent.For(target)!;
            Check("second-expansion-adds-a-third-floor",
                owner != null && owner.Layout!.LayerCount == 3 && target.cellIndices.NumGridCells > cellsBefore,
                $"layers={owner?.Layout?.LayerCount}, cells={cellsBefore}->{target.cellIndices.NumGridCells}");
            Check("second-expansion-also-releases-the-grid",
                TryReadNative(SmartCombatCompatibility.GridOn(road),out var createdAfter,out _)&&!createdAfter);
            File.WriteAllText(Path.Combine(directory, "smartcombat-buffers.json"), JsonSerializer.Serialize(new
            {
                packageId = SmartCombatCompatibility.PackageId,
                component = road.GetType().FullName,
                grid = SmartCombatCompatibility.GridOn(road)?.GetType().FullName,
                cells = target.cellIndices.NumGridCells,
                layers = owner!.Layout!.LayerCount,
                mode
            }, new JsonSerializerOptions { WriteIndented = true }));
            CameraJumper.TryJump(smartCombatPawn!, CameraJumper.MovementMode.Cut);
            pendingScreenshot = Path.Combine(directory, "smartcombat-expansion.png");
            screenshotHold = 3;
            smartCombatPhase = 3;
            ticks = 0;
            return;
        }
        if (smartCombatPhase == 3)
        {
            stage = 11;
            ticks = 0;
        }
    }
}
