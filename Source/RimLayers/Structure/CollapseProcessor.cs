using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Recovery;
using RimWorld;
using Verse;

namespace RimLayers.Structure;

/// <summary>Persist the pending cells, not transient paths or duplicate owners of falling things.</summary>
public sealed class CollapseProcessor
{
    private readonly LayerMapComponent owner;
    public CollapseProcessor(LayerMapComponent owner) => this.owner = owner;
    [ThreadStatic] internal static Map? EffectMap;
    [ThreadStatic] internal static IntVec3 EffectLanding;
    [ThreadStatic] internal static Thing? EffectThing;

    public void Tick()
    {
        if (owner.PendingCollapse.Count == 0) return;
        // Work is bounded per tick; safety queries have already invalidated the complete column.
        foreach (var cell in owner.PendingCollapse.OrderBy(c => c.z).ThenBy(c => c.x).Take(32).ToList())
        {
            if (!owner.StructuralCells.Contains(cell) || owner.Structure.HasSurface(cell))
            { owner.PendingCollapse.Remove(cell); continue; }
            if (!TryLanding(cell, out var landing))
            {
                // Keep unsupported structures queued, but suspend their exposed Pawn/item contents.
                foreach(var thing in cell.GetThingList(owner.map).ToArray())EntityRecoveryService.Current?.StructuralFall(thing,owner);
                continue;
            }
            Collapse(cell, landing);
            owner.PendingCollapse.Remove(cell);
        }
    }

    private bool TryLanding(IntVec3 source, out IntVec3 result)
    {
        owner.TryDecode(source, out var logical);
        foreach (int layer in owner.Layout!.Below(logical.LayerId))
        {
            IntVec3 center = owner.ToStorage(new LayerCell(layer, logical.X, logical.Z));
            if(owner.map.roofGrid.RoofAt(center)!=null){result=IntVec3.Invalid;return false;}
            if (!owner.Structure.HasSurface(center)) continue;
            foreach (IntVec3 candidate in GenRadial.RadialCellsAround(center, 8f, true))
                if (owner.TryDecode(candidate, out var decoded) && decoded.LayerId == layer &&
                    owner.Structure.HasSurface(candidate) && candidate.Standable(owner.map))
                { result = candidate; return true; }
            // Crowding at the first contact plane is not permission to fall through it.
            result=IntVec3.Invalid;return false;
        }
        // Keep the cell queued rather than deleting an entity if every local landing is blocked.
        result = IntVec3.Invalid;
        return false;
    }

    private void Collapse(IntVec3 cell, IntVec3 landing)
    {
        Map map = owner.map;
        var affected = cell.GetThingList(map).ToList();
        if (GenCommandLine.CommandLineArgPassed("rimlayersv1test")) Log.Message($"[RimLayers.Collapse] {cell}->{landing}: {string.Join("; ", affected.Select(t => t.ThingID))}");
        var footprint = new HashSet<IntVec3> { cell };
        var originalContents = new HashSet<Thing>(affected);
        foreach (Thing thing in affected)
            foreach (var pos in thing.OccupiedRect())
                foreach (Thing present in pos.GetThingList(map)) originalContents.Add(present);
        owner.StructuralCells.Remove(cell); // Claim this cell before any native callbacks.
        foreach (Thing thing in affected)
        {
            if (!thing.Spawned || thing.Destroyed) continue;
            if (thing is Building || thing is Blueprint)
            {
                foreach (IntVec3 occupied in thing.OccupiedRect()) footprint.Add(occupied);
                EffectMap = map; EffectLanding = landing; EffectThing = thing;
                try
                {
                    // Frames return only their actual container; no recipe cost is manufactured.
                    thing.Destroy(thing is Frame ? DestroyMode.Cancel :
                        thing is Blueprint ? DestroyMode.Vanish : DestroyMode.KillFinalize);
                }
                finally { EffectMap = null; EffectThing = null; }
            }
        }
        // Native container components may have released contents during destruction.
        foreach (var pos in footprint)
            foreach (Thing thing in pos.GetThingList(map).ToList())
                if (!affected.Contains(thing) && (!owner.Structure.HasSurface(pos) || !originalContents.Contains(thing))) affected.Add(thing);
        foreach (Thing thing in affected)
        {
            if (!thing.Spawned || thing.Destroyed || thing is Building || thing is Blueprint) continue;
            if (!owner.TryDecode(thing.Position, out var from) || !owner.TryDecode(landing, out var to) ||
                owner.Elevation(from.LayerId) <= owner.Elevation(to.LayerId)) continue;
            EntityRecoveryService.Current?.StructuralFall(thing,owner);
        }
        if (map.roofGrid.RoofAt(cell) != null) map.roofGrid.SetRoof(cell, null);
        TerrainDef top = map.terrainGrid.TerrainAt(cell);
        TerrainDef slab=owner.Structure.SlabAt(cell);
        if(map.terrainGrid.FoundationAt(cell)!=null)map.terrainGrid.RemoveFoundation(cell,false);
        map.terrainGrid.SetTerrain(cell, RimLayersDefOf.RL_OpenAir);
        // Accident salvage is a quarter of the structural/decorative material cost, once.
        LeaveSalvage(slab, landing);
        if (top != slab && top!=RimLayersDefOf.RL_OpenAir) LeaveSalvage(top, landing);
        owner.Underlays.Release(cell);owner.Ships.Forget(cell);
        owner.Structure.RoofChanged(cell);
        owner.Invalidate(cell, LayerChangeKind.Structure | LayerChangeKind.Movement |
            LayerChangeKind.Air | LayerChangeKind.Heat | LayerChangeKind.Light);
    }

    private void LeaveSalvage(TerrainDef terrain, IntVec3 landing)
    {
        if (terrain.costList == null) return;
        foreach (var cost in terrain.costList)
        {
            int count = cost.count / 4;
            if (count == 0) continue;
            Thing item = ThingMaker.MakeThing(cost.thingDef);
            item.stackCount = count;
            owner.Returns.Place(item,landing);
        }
    }
}

[HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor), new[] {
    typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect),
    typeof(Predicate<IntVec3>), typeof(List<Thing>) })]
internal static class CollapseLeavingsPatch
{
    static void Prefix(Thing diedThing, Map map, ref CellRect leavingsRect, ref Predicate<IntVec3>? nearPlaceValidator)
    {
        if (map != CollapseProcessor.EffectMap || diedThing != CollapseProcessor.EffectThing) return;
        var owner = LayerMapComponent.For(map)!;
        IntVec3 landing = CollapseProcessor.EffectLanding;
        owner.TryDecode(landing, out var logical);
        leavingsRect = CellRect.SingleCell(landing);
        var original = nearPlaceValidator;
        nearPlaceValidator = c => owner.TryDecode(c, out var cell) && cell.LayerId == logical.LayerId &&
            owner.Structure.HasSurface(c) && (original == null || original(c));
    }
}

[HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.RemoveRoofUnsafe))]
internal static class UnsafeRoofSupportPatch
{
    static void Postfix(int index, Map ___map)
    {
        var owner = LayerMapComponent.For(___map);
        if (owner == null) return;
        IntVec3 cell = ___map.cellIndices.IndexToCell(index);
        owner.Structure.RoofChanged(cell);
        owner.Invalidate(cell, LayerChangeKind.Structure | LayerChangeKind.Movement |
            LayerChangeKind.Air | LayerChangeKind.Heat | LayerChangeKind.Light);
    }
}
