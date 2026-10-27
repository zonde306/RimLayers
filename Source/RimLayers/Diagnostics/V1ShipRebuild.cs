using RimLayers.Ships;
using RimWorld;
using Verse;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    // Player report (2026-10-27): dismantle the engine, build a new one, every deck turns red and the
    // console can no longer launch. The saved shipCellOwners map still named the destroyed engine, and
    // the replacement's seed loop refuses any footprint cell claimed by a foreign id, so no ship was
    // ever produced. Adoption used to live only in Tick(), which early-returns on !dirty while Refresh()
    // clears that flag, so any same-frame read consumed the MarkDirty that SpawnSetup issued.
    private static void CheckCoreReplacementAdoption()
    {
        // 11x11 of deck: big enough for two 3x3 cores that do NOT overlap. IntVec3 is the CENTRE of a
        // footprint, so a 7x7 rect with a core at (110,90) put its own cells outside the deck and
        // adoption correctly refused it - the fixture was wrong, not the rule.
        var rect=new CellRect(108,88,11,11);
        foreach(var c in rect)
        {
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn&&t.def.destroyable)t.Destroy(DestroyMode.Vanish);
            if(map.terrainGrid.UnderTerrainAt(c)!=null)map.terrainGrid.RemoveTopLayer(c,false);
            map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);
            map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);map.fogGrid.Unfog(c);
            for(int layer=1;layer<=2;layer++)
            {
                var at=At(layer,c.x,c.z);
                ShipDeckTerrain.Install(owner,at,false);
                map.roofGrid.SetRoof(at,RoofDefOf.RoofConstructed);
            }
        }
        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        var first=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(113,0,93),map);
        first.SetFaction(Faction.OfPlayer);owner.Ships.MarkDirty();owner.Ships.Tick();
        var seeded=owner.Ships.Get(first);
        Check("replacement-fixture-seeds-every-deck-of-its-hull",seeded is {Ambiguous:false}&&seeded.Cells.Count==rect.Area*3,$"cells={seeded?.Cells.Count},status={seeded?.Ambiguous}");
        int deadId=first.thingIDNumber;
        var deadRect=first.OccupiedRect();
        first.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
        Check("destroyed-core-leaves-its-deck-claims-behind",owner.Ships.ClaimAt(new IntVec3(113,0,93))==deadId);
        // Different position with NO footprint overlap: this is the everyday live case (a 1x1 VGE core
        // replaced by a 3x3 vanilla engine rarely lands on the same cells). No MarkDirty/Tick here - the
        // first read must repair the map, because that is the same-frame path the placement UI takes.
        // An overlapping replacement would let a footprint-overlap rule pass, which is why the earlier
        // fixture stayed green while the player's ship went all-red.
        // A decoy orphan far away on its own deck patch: the flood must stop before it, or one engine
        // would silently annex another dismantled ship's hull.
        var decoy=new IntVec3(140,0,90);
        owner.Ships.Assign(decoy,999999);
        // Corner of the deck, deliberately NOT overlapping the old 3x3 footprint (x112..114, z92..94).
        // The first version of this fixture sat 2x2 on top of the dead footprint and therefore passed
        // under a footprint-overlap rule too - it could not tell the two rules apart.
        var replacement=(Building_GravEngine)GenSpawn.Spawn(ThingDefOf.GravEngine,new IntVec3(109,0,89),map);
        replacement.SetFaction(Faction.OfPlayer);
        Check("replacement-fixture-does-not-overlap-the-dead-footprint",
            !replacement.OccupiedRect().Overlaps(deadRect),$"dead={deadRect},live={replacement.OccupiedRect()}");
        var adopted=owner.Ships.Get(replacement);
        Check("replacement-core-adopts-orphan-decks-on-first-read",adopted is {Ambiguous:false}&&adopted.Cells.Count==rect.Area*3,
            $"cells={adopted?.Cells.Count},claim={owner.Ships.ClaimAt(new IntVec3(113,0,93))},live={replacement.thingIDNumber}");
        Check("replacement-core-does-not-annex-a-distant-dead-hull",owner.Ships.ClaimAt(decoy)==999999,$"claim={owner.Ships.ClaimAt(decoy)}");
        owner.Ships.Forget(decoy);
        Check("replacement-core-restores-native-onboard-cell",replacement.ValidSubstructureAt(At(2,113,93)));
        replacement.Destroy(DestroyMode.Vanish);owner.Ships.MarkDirty();owner.Ships.Tick();
    }
}
