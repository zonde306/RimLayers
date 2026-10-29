using HarmonyLib;
using RimLayers.Core;
using RimLayers.Diagnostics;
using RimLayers.Ships;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Structure;

internal static class ConstructionSupport
{
    internal static AcceptanceReport Validate(Map? map,BuildableDef buildable,IntVec3 position,Rot4 rotation)
    {
        if(map==null)return true; // Preserve native handling of unspawned construction queries.
        if(LayerMapComponent.For(map) is {} owner)return owner.Structure.Validate(buildable,position,rotation);
        return ConnectorDefs.IsAdjacent(buildable)?StairLayerBootstrap.Validate(map,position,rotation,buildable):true;
    }
}

[HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanConstruct), new[] {
    typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) })]
internal static class ConstructionSupportPatch
{
    static bool Prefix(Thing t, ref bool __result)
    {
        if(t is Frame f && RimLayers.Mountains.VerticalMining.Ready(f)){__result=false;return false;}
        if (t.def.entityDefToBuild == null || ConstructionSupport.Validate(t.Map,t.def.entityDefToBuild,t.Position,t.Rotation).Accepted) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
internal static class ConstructionCommitPatch
{
    static bool Prefix(Frame __instance, Pawn worker)
    {
        bool stair=ConnectorDefs.IsAdjacent(__instance.def.entityDefToBuild);
        // Only a colonist's own build is a player action; a raider's frame is not worth a window.
        bool playerBuild=worker.Faction==Faction.OfPlayer;
        if(stair&&!StairLayerBootstrap.Prepare(__instance.Map,__instance.Position,__instance.Rotation,__instance.def.entityDefToBuild,report:playerBuild))
        {
            if(worker.CurJob?.targetA.Thing==__instance)worker.jobs.curDriver?.EndJobWith(JobCondition.Incompletable);
            return false;
        }
        if(stair&&RimLayers.Mountains.VerticalMining.BlockCommit(__instance,worker))return false;
        var owner = LayerMapComponent.For(__instance.Map);
        if (owner == null) return true;
        var refused = owner.Structure.Validate(__instance.def.entityDefToBuild, __instance.Position, __instance.Rotation);
        if (refused.Accepted) return true;
        // A player-directed build the layers refuse. Say so: the job just ends otherwise.
        if(playerBuild)
            RejectionReporter.Reject(RejectionAction.Build, refused.Reason, true,
                __instance.def.entityDefToBuild.defName, __instance.def.entityDefToBuild.LabelCap,
                __instance.Map, __instance.Position, RejectionReporter.ElevationAt(__instance.Map, __instance.Position));
        // No resources, signals, products or Frame destruction have been committed.
        if (worker.CurJob?.targetA.Thing == __instance) worker.jobs.curDriver?.EndJobWith(JobCondition.Incompletable);
        return false;
    }
}

[HarmonyPatch(typeof(JobDriver_ConstructFinishFrame), "MakeNewToils")]
internal static class ConstructionTickPatch
{
    static IEnumerable<Toil> Postfix(IEnumerable<Toil> __result, JobDriver_ConstructFinishFrame __instance)
    {
        foreach (var toil in __result)
        {
            var action = toil.tickIntervalAction;
            if (action != null)
                toil.tickIntervalAction = delta =>
                {
                    Pawn pawn = __instance.pawn;
                    Thing? frame = pawn.CurJob?.targetA.Thing;
                    if (frame?.def.entityDefToBuild != null &&
                        !ConstructionSupport.Validate(pawn.Map,frame.def.entityDefToBuild,frame.Position,frame.Rotation).Accepted)
                    {
                        __instance.EndJobWith(JobCondition.Incompletable);
                        return; // Do not run the native continuation after a blocked void completion.
                    }
                    action(delta);
                };
            yield return toil;
        }
    }
}

[HarmonyPatch(typeof(GenGrid), nameof(GenGrid.Walkable))]
internal static class SupportWalkabilityPatch
{
    static bool Prefix(IntVec3 c, Map map, ref bool __result)
    {
        var owner = LayerMapComponent.For(map);
        if (owner == null || owner.Structure.HasSurface(c)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(PathGrid), nameof(PathGrid.Walkable))]
internal static class SupportPathGridPatch
{
    static bool Prefix(IntVec3 loc, Map ___map, ref bool __result)
    {
        var owner = LayerMapComponent.For(___map);
        if (owner == null || owner.Structure.HasSurface(loc)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveTopLayer))]
internal static class StructuralFloorRemovalPatch
{
    static bool Prefix(IntVec3 c, TerrainGrid __instance, Map ___map, bool doLeavings)
    {
        var owner = LayerMapComponent.For(___map);
        if (owner == null) return true;
        // A finish layer (carpet, wood, ...) laid over a ship deck: native CanRemoveTopLayerAt demands
        // underGrid != null, but SetFoundation refuses an under terrain, so the finish could never be
        // removed and RemoveTopLayer would fall through to RemoveFoundation and delete the whole deck.
        if (ShipDeckFinish.IsFinishOverDeck(owner, c))
        {
            if (!ShipDeckFinish.CanRemoveFinish(owner, c)) return false;
            ShipDeckFinish.RemoveFinish(owner, c, doLeavings);
            return false;
        }
        if (!owner.Structure.IsBareSlab(c) || ___map.terrainGrid.FoundationAt(c)!=null) return true;
        if (!owner.Structure.CanDismantleSlab(c)) return false;
        IntVec3 refundCell=GenAdj.AdjacentCells.Select(offset=>c+offset)
            .First(cell=>owner.Structure.HasSurface(cell) && cell.Standable(___map));
        __instance.SetTerrain(c, RimLayersDefOf.RL_OpenAir);
        if (doLeavings)
            foreach (var cost in RimLayersDefOf.RL_StructuralFloor.CostListAdjusted(null))
            {
                int count=GenMath.RoundRandom(cost.count*RimLayersDefOf.RL_StructuralFloor.resourcesFractionWhenDeconstructed);
                if (count==0) continue;
                Thing material=ThingMaker.MakeThing(cost.thingDef);
                material.stackCount=count;
                owner.TryDecode(refundCell,out var layer);
                owner.Returns.Place(material,refundCell);
            }
        return false;
    }
}

/// <summary>
/// Finish-layer handling for RimLayers ship decks. A deck is a foundation with no under terrain, so
/// the native top-layer rules (which require an under terrain) never match.
/// </summary>
internal static class ShipDeckFinish
{
    internal static bool IsFinishOverDeck(LayerMapComponent owner, IntVec3 c) =>
        owner.map.terrainGrid.FoundationAt(c)==ShipDeckTerrain.Def &&
        owner.map.terrainGrid.UnderTerrainAt(c)==null &&
        owner.map.terrainGrid.TempTerrainAt(c)==null &&
        owner.map.terrainGrid.TopTerrainAt(c)!=ShipDeckTerrain.Def &&
        owner.map.terrainGrid.TerrainAt(c)!=RimLayersDefOf.RL_OpenAir;

    internal static bool CanRemoveFinish(LayerMapComponent owner, IntVec3 c) =>
        IsFinishOverDeck(owner,c) && owner.map.terrainGrid.TerrainAt(c).Removable &&
        !c.GetThingList(owner.map).Any(t=>t is Pawn || t is Building || t is Blueprint);

    internal static void RemoveFinish(LayerMapComponent owner, IntVec3 c, bool doLeavings)
    {
        var grid=owner.map.terrainGrid;
        var finish=grid.TerrainAt(c);
        if (doLeavings) GenLeaving.DoLeavingsFor(finish, c, owner.map);
        grid.SetTerrain(c, RimLayersDefOf.RL_OpenAir);
        grid.SetTerrainColor(c, null);
        owner.map.pathing.RecalculatePerceivedPathCostAt(c);
        owner.Invalidate(c,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Light);
    }
}

[HarmonyPatch(typeof(PathGrid), nameof(PathGrid.CalculatedCostAt))]
internal static class StructuralPathCostPatch
{
    static bool Prefix(IntVec3 c, Map ___map, ref int __result)
    {
        var owner = LayerMapComponent.For(___map);
        if (owner == null || owner.Structure.HasSurface(c)) return true;
        __result = 10000;
        return false;
    }
}


[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.CanRemoveTopLayerAt))]
internal static class StructuralRemovalAvailabilityPatch
{
    static bool Prefix(IntVec3 c,Map ___map,ref bool __result)
    {
        var owner=LayerMapComponent.For(___map);
        if (owner==null) return true;
        if (ShipDeckFinish.IsFinishOverDeck(owner,c)) { __result=ShipDeckFinish.CanRemoveFinish(owner,c); return false; }
        if (!owner.Structure.IsBareSlab(c) || ___map.terrainGrid.FoundationAt(c)!=null) return true;
        __result=owner.Structure.CanDismantleSlab(c);
        return false;
    }
}

[HarmonyPatch(typeof(JobDriver_AffectFloor), "MakeNewToils")]
internal static class StructuralRemovalWorkPatch
{
    static IEnumerable<Toil> Postfix(IEnumerable<Toil> __result,JobDriver_AffectFloor __instance)
    {
        foreach (var toil in __result)
        {
            var action=toil.tickIntervalAction;
            if (__instance is JobDriver_RemoveFloor && action!=null)
                toil.tickIntervalAction=delta=>
                {
                    var pawn=__instance.pawn;
                    var owner=LayerMapComponent.For(pawn.Map);
                    var cell=pawn.CurJob.targetA.Cell;
                    if (owner!=null && owner.Structure.IsBareSlab(cell) && !owner.Structure.CanDismantleSlab(cell))
                    {
                        __instance.EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                    if (owner!=null && ShipDeckFinish.IsFinishOverDeck(owner,cell) && !ShipDeckFinish.CanRemoveFinish(owner,cell))
                    {
                        __instance.EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                    action(delta);
                };
            yield return toil;
        }
    }
}
