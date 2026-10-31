using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Rewrites the three SOS2 places that read <c>Map.Size</c> as if it were the logical rectangle.
/// On a layered map <c>Map.Size.z</c> spans every band plus the padding, so the native versions
/// relocate ships and shots into the storage gap.
/// </summary>
internal static class Sos2GeometryPatches
{
    private static readonly FieldInfo? ShipArea =
        AccessTools.Field(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "Area");

    /// <summary>
    /// <c>Building_ShipTurret.MapEdgeCell</c> aims every shot at the map's storage edge. Replaced with
    /// the band-local edge of the turret's own deck.
    /// </summary>
    [HarmonyPatch]
    private static class TurretEdgeCellPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.Building_ShipTurret"), "MapEdgeCell", new[] { typeof(int) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance, int miss, ref LocalTargetInfo __result)
        {
            if (__instance is not Building turret) return true;
            Map map = turret.Map;
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            IntVec3 pos = turret.Position;
            if (miss > 0) miss = Rand.RangeInclusive(-miss, miss);

            // SOS2 fires along the engine axis, reversed while retreating.
            int engineRot = EngineRotation(__instance);
            int heading = Heading(__instance);
            int side = (engineRot, heading) switch
            {
                (0, not -1) => 0,
                (2, -1) => 0,
                (1, not -1) => 1,
                (3, -1) => 1,
                (2, not -1) => 2,
                (0, -1) => 2,
                _ => 3,
            };
            (int x, int y, int z) = Sos2Geometry.EdgeCell(
                pos.x, 0, pos.z, owner.Layout!.Width, height, stride, layers, miss, side);
            __result = new LocalTargetInfo(new IntVec3(x, y, z));
            return false;
        }

        private static int EngineRotation(object turret)
        {
            object? comp = AccessTools.Field(turret.GetType(), "mapComp")?.GetValue(turret);
            return comp == null ? 0 : AccessTools.Field(comp.GetType(), "EngineRot")?.GetValue(comp) as int? ?? 0;
        }

        private static int Heading(object turret)
        {
            object? comp = AccessTools.Field(turret.GetType(), "mapComp")?.GetValue(turret);
            return comp == null ? 0 : AccessTools.Field(comp.GetType(), "Heading")?.GetValue(comp) as int? ?? 0;
        }
    }

    /// <summary>
    /// <c>ShipInteriorMod2.TransformForShipMove</c> mirrors a rotated ship against <c>Map.Size</c>.
    /// Replaced with a band-preserving mirror so a multi-deck hull keeps its decks.
    /// </summary>
    [HarmonyPatch]
    private static class TransformForShipMovePatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipInteriorType, "TransformForShipMove",
                new[] { typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(int) });
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(Map targetMap, IntVec3 pos, IntVec3 adjustment, int rotb, ref IntVec3 __result)
        {
            if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            (int x, int y, int z) = Sos2Geometry.Mirror(
                pos.x, pos.y, pos.z, owner.Layout!.Width, height, stride, layers, rotb);
            __result = new IntVec3(x, y, z) + adjustment;
            return false;
        }
    }

    /// <summary>
    /// <c>SpaceShipCache.CenterShipOnMap</c> centres against <c>Map.Size.z</c>, which on a layered map
    /// parks the whole hull in the middle band. Replaced with a centre inside the ground band.
    /// </summary>
    [HarmonyPatch]
    private static class CenterShipOnMapPatch
    {
        private static MethodBase? TargetMethod()
        {
            MethodInfo? original = AccessTools.Method(AccessTools.TypeByName("SaveOurShip2.SpaceShipCache"), "CenterShipOnMap");
            return original == null ? null : new HarmonyMethod(original).method;
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance, ref IntVec3 __result)
        {
            if (ShipArea?.GetValue(__instance) is not HashSet<IntVec3> { Count: > 0 } area) return true;
            int minX = int.MaxValue, maxX = int.MinValue;
            foreach (IntVec3 v in area)
            {
                if (v.x < minX) minX = v.x;
                if (v.x > maxX) maxX = v.x;
            }
            Map map = (AccessTools.Field(__instance.GetType(), "Map")?.GetValue(__instance) as Map) ?? Find.CurrentMap;
            if (!Sos2CellGraph.BandOf(map, out LayerMapComponent owner, out int stride, out int height, out int layers)) return true;
            (int x, int y, int z) = Sos2Geometry.CenterOnMap(
                owner.Layout!.Width, map.Size.z, height, stride, layers,
                maxX - minX, minX, area.Select(v => v.z));
            __result = new IntVec3(x, y, z);
            return false;
        }
    }
}

/// <summary>
/// <c>ShipInteriorMod2.ReSpawnThingOnMap</c> is the second half of a ship move: every part is moved
/// here and then spawned. It does not call <c>TransformForShipMove</c>; it repeats the rotation
/// arithmetic inline, and for both rotb 2 and 3 it subtracts <c>Position.z</c> from <c>Map.Size.x</c>.
/// </summary>
/// <remarks>
/// On a layered map those are different quantities. <c>Size.x</c> is one logical side (250) while a
/// part on deck 3 has <c>Position.z</c> around 630, so <c>250 - 630 = -380</c> and the part is spawned
/// at <c>z = -397</c> once the adjustment is added. Vanilla destroys it with "tried to register out of
/// bounds" and the move throws, which is what a player sees when flipping a multi-deck hull: the parts
/// vanish and the ship is left half transferred.
/// </remarks>
[HarmonyPatch]
internal static class Sos2ReSpawnRotatePatch
{
    private static MethodBase? TargetMethod()
    {
        MethodInfo? original = AccessTools.Method(Sos2Compatibility.ShipInteriorType, "ReSpawnThingOnMap", new[]
        {
            typeof(Thing), typeof(Map), typeof(IntVec3), typeof(int), typeof(Faction)
        });
        return original == null ? null : new HarmonyMethod(original).method;
    }

    private static bool Prefix(Thing spawnThing, Map targetMap, IntVec3 adjustment, int rotb, Faction fac)
    {
        if (!Sos2Compatibility.Active || rotb is not (2 or 3)) return true;
        if (!Sos2CellGraph.BandOf(targetMap, out LayerMapComponent owner, out int stride, out int height, out int layers))
            return true;
        if (owner.Layout is not { } layout) return true;
        if (spawnThing.Destroyed) return true;
        if (!owner.TryDecode(spawnThing.Position, out LayerCell local)
            && !TryLocal(targetMap, spawnThing.Position, stride, layers, out local)) return true;

        // SOS2's arithmetic is right on a one-deck map, where every position it reads is band-local.
        // Rotating the band-local cell and putting the band back is that same computation with the
        // storage offset removed, so a part on deck 3 is not mirrored against deck 0's coordinates.
        (int x, int y, int z) = Sos2Geometry.Mirror(local.X, spawnThing.Position.y, local.Z,
            layout.Width, height, stride, layers, (byte)rotb);
        IntVec3 mirrored = new IntVec3(x, y, z) + adjustment;

        // Rotating the thing itself happens before any coordinate work in the original and does not
        // depend on the map, so it is kept exactly as it was.
        if (spawnThing.def.rotatable)
            spawnThing.Rotation = new Rot4(spawnThing.Rotation.AsByte + rotb);
        spawnThing.Position = mirrored;
        if (fac != null && spawnThing is not Pawn && spawnThing.def.CanHaveFaction)
            spawnThing.SetFaction(fac);
        // The original skips respawn post-processing for defs carrying SOS2's override extension; the
        // check is repeated by name because RimLayers does not reference the SOS2 assembly.
        spawnThing.SpawnSetup(targetMap, !HasSpawnOverride(spawnThing.def));
        if (spawnThing is Pawn pawn) pawn.pather.ResetToCurrentPosition();
        return false;
    }

    private static bool TryLocal(Map map, IntVec3 storage, int stride, int layers, out LayerCell local)
    {
        local = default;
        if (!storage.InBounds(map)) return false;
        int band = Sos2Geometry.LayerOf(storage.z, stride, layers);
        if (band < 0) return false;
        local = new LayerCell(band, storage.x, Sos2Geometry.LocalZ(storage.z, stride));
        return true;
    }

    private static Type? spawnOverride;
    private static bool spawnOverrideLooked;

    private static bool HasSpawnOverride(ThingDef def)
    {
        if (!spawnOverrideLooked)
        {
            spawnOverrideLooked = true;
            spawnOverride = AccessTools.TypeByName("SaveOurShip2.SoSSpawnOverride");
        }
        if (spawnOverride == null || def.modExtensions == null) return false;
        foreach (DefModExtension extension in def.modExtensions)
            if (spawnOverride.IsInstanceOfType(extension)) return true;
        return false;
    }
}
