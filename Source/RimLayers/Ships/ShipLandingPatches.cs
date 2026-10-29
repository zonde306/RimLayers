using HarmonyLib;
using RimLayers.Core;
using RimLayers.Diagnostics;
using RimLayers.Generation;
using RimLayers.Layout;
using RimLayers.Presentation;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Ships;

internal static class ShipLanding
{
    // Ordinary items and plants are allowed; native SpawnFoundations clears actual deck cells only.
    // Pawn policy matches native IsValidCell; colony structures remain protected.
    internal static bool BlocksLanding(Thing thing)=>thing.def.preventGravshipLandingOn||
        thing is Building or Blueprint or Frame||thing is Pawn pawn&&(pawn.RaceProps.Humanlike||pawn.HostileTo(Faction.OfPlayer));

    internal static AcceptanceReport Check(Gravship ship,IntVec3 root,Map map)
    {
        if(!ShipLayers.IsLayerShip(ship))return true;
        var owner=LayerMapComponent.For(map);
        if(owner==null)return ShipLayers.RequiredLayers(ship)==1?true:"RL_ShipNeedsLayers".Translate();
        if(!owner.TryDecode(root,out var origin))return "RL_ShipUpperOutOfBounds".Translate();
        ShipLayers.TryData(ship,out var data);
        var height=CheckHeight(ship,owner.Elevation(origin.LayerId));
        if(!height.Accepted)return height;
        var quota=LayerExpansionQuota.Current?.Check(map,RequiredAt(ship,root,owner)-1,ship.Engine.thingIDNumber)??true;
        if(!quota.Accepted)return quota;
        var targets=data.floors.Select(f=>PrefabUtility.GetAdjustedLocalPosition(f.local,ship.Rotation))
            .Select(c=>new LayerCell(owner.Layout!.LayerOrFuture(owner.Elevation(origin.LayerId)+c.y),origin.X+c.x,origin.Z+c.z)).ToHashSet();
        foreach(var target in targets)
        {
            if((uint)target.X>=(uint)owner.Layout!.Width||(uint)target.Z>=(uint)owner.Layout.Height)return "RL_ShipUpperOutOfBounds".Translate();
            // Jammers cover the logical footprint, including decks not allocated yet.
            var external=Compatibility.VanillaGravshipCompatibility.LandingCell(new IntVec3(target.X,0,target.Z),map);if(!external.Accepted)return external;
            if(target.LayerId>=owner.Layout.LayerCount)continue; // Unallocated upper bands are empty; preview never expands.
            var cell=owner.ToStorage(target);
            bool bottom=target.LayerId==origin.LayerId;
            bool host=bottom&&owner.Underlays.CanHost(cell);
            // The native ground landing contract permits an existing orbital substructure pad.
            // Above ground, another ship foundation remains an occupied deck.
            if(target.LayerId>0&&owner.map.terrainGrid.FoundationAt(cell)?.IsSubstructure==true||
                owner.Mountains.IsNaturalFloor(cell)||owner.StructuralCells.Contains(cell)&&!host)return "RL_ShipUpperBlocked".Translate();
            if(bottom&&origin.LayerId>0&&!host&&!owner.Structure.HasRequiredRoof(cell))return "RL_ShipUnsupportedLanding".Translate();
            if(map.roofGrid.RoofAt(cell)!=null||cell.Fogged(map)||
                cell.GetThingList(map).Any(ShipLanding.BlocksLanding))
                return "RL_ShipUpperBlocked".Translate();
        }
        // Swept columns include hollow intermediate decks; a stationary floor cannot be flown through.
        foreach(var column in targets.GroupBy(c=>(c.X,c.Z)))
        {
            foreach(int layer in owner.Layout!.OrderedLayers.Where(id=>owner.Elevation(id)>=column.Min(c=>owner.Layout.ElevationOrFuture(c.LayerId))))
            {
                var cell=owner.ToStorage(new(layer,column.Key.X,column.Key.Z));
                bool host=layer==origin.LayerId&&owner.Underlays.CanHost(cell);
                if((owner.StructuralCells.Contains(cell)&&!host)||owner.Mountains.IsNaturalFloor(cell)||
                    map.roofGrid.RoofAt(cell)!=null||
                    cell.GetThingList(map).Any(ShipLanding.BlocksLanding))
                    return "RL_ShipUpperBlocked".Translate();
            }
        }
        foreach(var support in data.supportRoofs)
        {
            var local=PrefabUtility.GetAdjustedLocalPosition(support.local,ship.Rotation);
            var target=new LayerCell(owner.Layout!.LayerOrFuture(owner.Elevation(origin.LayerId)+local.y),origin.X+local.x,origin.Z+local.z);
            if(target.LayerId>=owner.Layout!.LayerCount)continue;
            var cell=owner.ToStorage(target);int roofOwner=owner.Underlays.SupportRoofOwner(cell);
            if(roofOwner!=0&&roofOwner!=ship.Engine.thingIDNumber||
                map.roofGrid.RoofAt(cell) is {} existing&&existing!=support.roof)return "RL_ShipUpperBlocked".Translate();
        }
        var previousShip=ShipLayers.Placing;var previousMap=ShipLayers.PlacementMap;int previousBase=ShipLayers.PlacementBaseLayer;
        try
        {
            ShipLayers.Placing=ship;ShipLayers.PlacementMap=owner;ShipLayers.PlacementBaseLayer=origin.LayerId;
            foreach(var pair in ship.ThingPlacements.Select(p=>(thing:(Thing)p.Key,placement:p.Value))
                .Concat(ship.PawnPlacements.Select(p=>(thing:(Thing)p.Key,placement:p.Value))))
            {
                var position=root+pair.placement.local;
                if(!owner.TryDecode(position,out var logical))
                {
                    // Future band coordinates still retain their exact stride and logical dimensions.
                    int layer=position.z/owner.Layout!.Stride,z=position.z%owner.Layout.Stride;
                    logical=new(layer,position.x,z);
                    if((uint)logical.X>=(uint)owner.Layout.Width||(uint)logical.Z>=(uint)owner.Layout.Height||
                       owner.Layout.ElevationOrFuture(layer)>LayerLayout.MaxElevation)return "RL_ShipUpperOutOfBounds".Translate();
                }
                if(logical.LayerId==0)continue; // Preserve native ground placement contract.
                foreach(var cell in GenAdj.OccupiedRect(position,pair.placement.rotation,pair.thing.def.size))
                {
                    var here=new LayerCell(cell.z/owner.Layout!.Stride,cell.x,cell.z%owner.Layout.Stride);
                    if(here.LayerId!=logical.LayerId||(uint)here.X>=(uint)owner.Layout.Width||(uint)here.Z>=(uint)owner.Layout.Height)
                        return "RL_ShipUpperOutOfBounds".Translate();
                    if(here.LayerId>=owner.Layout.LayerCount)continue;
                    bool host=here.LayerId==origin.LayerId&&owner.Underlays.CanHost(cell);
                    if((owner.StructuralCells.Contains(cell)&&!host)||owner.Mountains.IsNaturalFloor(cell)||
                        map.roofGrid.RoofAt(cell)!=null||cell.GetThingList(map).Any(ShipLanding.BlocksLanding))
                        return "RL_ShipUpperBlocked".Translate();
                }
            }
        }
        finally{ShipLayers.Placing=previousShip;ShipLayers.PlacementMap=previousMap;ShipLayers.PlacementBaseLayer=previousBase;}
        return true;
    }
    internal static AcceptanceReport CheckHeight(Gravship ship,int bottomElevation)
    {
        if(bottomElevation<0)return "RL_DigShipUnsupported".Translate();
        // Payload span includes hollow decks; storage band IDs are not physical elevations.
        int topElevation=bottomElevation+ShipLayers.RequiredLayers(ship)-1;
        return topElevation<=LayerLayout.MaxElevation?true:
            "RL_ShipLandingLayerOverflow".Translate(bottomElevation+1,topElevation+1,LayerLayout.MaxElevation+1);
    }
    internal static int RequiredAt(Gravship ship,IntVec3 root,LayerMapComponent owner)=>
        owner.TryDecode(root,out var at)?owner.Elevation(at.LayerId)+ShipLayers.RequiredLayers(ship):LayerLayout.PrototypeLayers+1;
    internal static void EnsureCapacity(Gravship ship,Map map,int top)
    {
        int previous=LayerExpansionQuota.ActiveCommitment;
        LayerExpansionQuota.ActiveCommitment=ship.Engine.thingIDNumber;
        try { LiveMapExpansion.EnsureElevation(map,top); }
        finally { LayerExpansionQuota.ActiveCommitment=previous; }
    }
    internal static void PreserveJourney(Gravship ship)
    {
        if(ShipLayers.IsLayerShip(ship))LayerExpansionQuota.Current?.PreserveInFlight(ship.Engine.thingIDNumber,ship.destinationTile,ShipLayers.RequiredLayers(ship)-1);
    }
    internal static void PreparePlacement(Gravship ship,IntVec3 root,LayerMapComponent owner)
    {
        ShipLayers.TryData(ship,out var data);owner.TryDecode(root,out var at);
        foreach(var roof in data.supportRoofs)
        {
            var local=PrefabUtility.GetAdjustedLocalPosition(roof.local,ship.Rotation);
            owner.Underlays.CoverSupportRoof(root+ShipLayers.Offset(owner,at.LayerId,local),ship.Engine.thingIDNumber);
        }
        foreach(var floor in data.floors)
        {
            var local=PrefabUtility.GetAdjustedLocalPosition(floor.local,ship.Rotation);
            var cell=root+ShipLayers.Offset(owner,at.LayerId,local);
            if(local.y==0&&at.LayerId>0)owner.Underlays.Cover(cell);
            owner.Ships.Assign(cell,ship.Engine.thingIDNumber);
        }
    }
    internal static void RestoreFloors(Gravship ship,Map map,IntVec3 root)
    {
        if(!ShipLayers.IsPlacing(ship)||!ShipLayers.TryData(ship,out var data))return;
        var owner=ShipLayers.PlacementMap!;
        foreach(var floor in data.floors)
        {
            var relative=PrefabUtility.GetAdjustedLocalPosition(floor.local,ship.Rotation);
            var cell=root+ShipLayers.DecodeOffset(relative);
            map.terrainGrid.SetTerrain(cell,floor.top!);
            // Native 1.7 SetUnderTerrain dereferences null; non-layerable top already clears it.
            if(floor.under!=null)map.terrainGrid.SetUnderTerrain(cell,floor.under);

            if(owner!=null&&owner.TryDecode(cell,out var l)&&l.LayerId>0)owner.StructuralCells.Add(cell);
        }
        foreach(var (local,color) in ship.TerrainColors)if(color!=null)map.terrainGrid.SetTerrainColor(root+local,color);
        // Support facts precede native Pawn spawn safety and stair SpawnSetup.
        foreach(var (local,roof) in ship.Roofs.OrderBy(x=>x.Item1.z))map.roofGrid.SetRoof(root+local,roof);
        foreach(var floor in data.floors)
        {
            var cell=root+ShipLayers.DecodeOffset(PrefabUtility.GetAdjustedLocalPosition(floor.local,ship.Rotation));
            if(owner==null)continue;
            if(owner.Structure.HasSurface(cell))owner.PendingCollapse.Remove(cell);
            owner.Invalidate(cell,LayerChangeKind.Structure|LayerChangeKind.Movement|LayerChangeKind.Air|LayerChangeKind.Heat|LayerChangeKind.Power|LayerChangeKind.Light);
        }
    }
    internal static void RestoreSurfaceLayers(Gravship ship,Map map,IntVec3 root)
    {
        if(!ShipLayers.IsPlacing(ship)||!ShipLayers.TryData(ship,out var data))return;
        foreach(var floor in data.floors)
        {
            var cell=root+ShipLayers.DecodeOffset(PrefabUtility.GetAdjustedLocalPosition(floor.local,ship.Rotation));
            // Native TerrainAt captures the temporary cover; its affordance fallback can replace
            // the top/under grid with soil. Restore the owned surface stack before spawning Things.
            map.terrainGrid.SetTerrain(cell,floor.top!);
            if(floor.under!=null)map.terrainGrid.SetUnderTerrain(cell,floor.under);
            if(floor.temp!=null)map.terrainGrid.SetTempTerrain(cell,floor.temp);
        }
        foreach(var (local,color) in ship.TerrainColors)if(color!=null)map.terrainGrid.SetTerrainColor(root+local,color);
    }

}
internal sealed class ShipPlacementState
{
    internal Gravship? PreviousShip;
    internal LayerMapComponent? PreviousMap;
    internal int PreviousBase;
}
[HarmonyPatch(typeof(GravshipPlacementUtility),nameof(GravshipPlacementUtility.PlaceGravshipInMap))]
internal static class LayerShipPlacement
{
    static void Prefix(Gravship gravship,IntVec3 root,Map map,out ShipPlacementState? __state)
    {
        __state=null;if(!ShipLayers.IsLayerShip(gravship))return;
        var report=ShipLanding.Check(gravship,root,map);
        if(!report.Accepted)throw new InvalidOperationException("[RimLayers] Unsafe layered ship placement: "+report.Reason);
        var owner=LayerMapComponent.For(map);
        if(owner!=null)
        {
            ShipLanding.EnsureCapacity(gravship,map,ShipLanding.RequiredAt(gravship,root,owner)-1);
            ShipLanding.PreparePlacement(gravship,root,owner);
        }
        __state=new(){PreviousShip=ShipLayers.Placing,PreviousMap=ShipLayers.PlacementMap,PreviousBase=ShipLayers.PlacementBaseLayer};
        ShipLayers.Placing=gravship;ShipLayers.PlacementMap=owner;
        ShipLayers.PlacementBaseLayer=owner!=null&&owner.TryDecode(root,out var at)?at.LayerId:0;
    }
    static void Postfix(Gravship gravship,Map map)
    {
        if(!ShipLayers.IsPlacing(gravship))return;
        LayerExpansionQuota.Current?.Release(gravship.Engine.thingIDNumber);
        var owner=ShipLayers.PlacementMap;
        if(owner==null)return;
        owner.Connectors.Invalidate();owner.Ships.MarkDirty();owner.Ships.Tick();owner.Power.Rebuild();owner.Air.Rebuild();owner.Sky.Rebuild();
        map.pathing.RecalculateAllPerceivedPathCosts();owner.SetViewedLayer(ShipLayers.PlacementBaseLayer);
    }
    static Exception? Finalizer(Exception? __exception,ShipPlacementState? __state)
    {if(__state!=null){ShipLayers.Placing=__state.PreviousShip;ShipLayers.PlacementMap=__state.PreviousMap;ShipLayers.PlacementBaseLayer=__state.PreviousBase;}return __exception;}
}
[HarmonyPatch(typeof(GravshipPlacementUtility),"SpawnTerrain",new[]{typeof(Gravship),typeof(Map),typeof(IntVec3)})]
internal static class LayerShipFloorRestore
{
    static void Prefix(Gravship gravship,Map map,IntVec3 root)=>ShipLanding.RestoreFloors(gravship,map,root);
    static void Postfix(Gravship gravship,Map map,IntVec3 root)=>ShipLanding.RestoreSurfaceLayers(gravship,map,root);
}
[HarmonyPatch(typeof(GravshipPlacementUtility),nameof(GravshipPlacementUtility.ApplyTemperatureVacuumFromBase))]
internal static class LayerShipRoomRestore
{
    static void Prefix(Gravship gravship,Map map,out ShipPlacementState? __state)
    {
        __state=null;if(!ShipLayers.IsLayerShip(gravship))return;
        __state=new(){PreviousShip=ShipLayers.Placing,PreviousMap=ShipLayers.PlacementMap,PreviousBase=ShipLayers.PlacementBaseLayer};
        ShipLayers.Placing=gravship;ShipLayers.PlacementMap=LayerMapComponent.For(map);
        if(ShipLayers.PlacementMap is {} owner&&owner.TryDecode(gravship.Engine.Position,out var at)&&ShipLayers.TryData(gravship,out var data))
            ShipLayers.PlacementBaseLayer=owner.Layout!.LayerAt(owner.Elevation(at.LayerId)-data.engineDeckOffset);
    }
    static Exception? Finalizer(Exception? __exception,ShipPlacementState? __state)
    {if(__state!=null){ShipLayers.Placing=__state.PreviousShip;ShipLayers.PlacementMap=__state.PreviousMap;ShipLayers.PlacementBaseLayer=__state.PreviousBase;}return __exception;}
}
[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.ArriveNewMap))]
internal static class LayerShipIncomingMap
{
    static void Prefix(Gravship gravship,out Gravship? __state)
    {__state=ShipLayers.Incoming;ShipLayers.AttachNativeSingle(gravship);ShipLanding.PreserveJourney(gravship);if(ShipLayers.IsLayerShip(gravship))ShipLayers.Incoming=gravship;}
    static Exception? Finalizer(Exception? __exception,Gravship? __state){ShipLayers.Incoming=__state;return __exception;}
}
[HarmonyPatch(typeof(GravshipUtility),nameof(GravshipUtility.ArriveExistingMap))]
internal static class LayerShipExistingMap
{
    static void Prefix(Gravship gravship,out int? __state)
    {
        __state=null;
        ShipLayers.AttachNativeSingle(gravship);
        if(!ShipLayers.IsLayerShip(gravship))return;
        var map=Find.WorldObjects.MapParentAt(gravship.destinationTile)?.Map;
        __state=LayerMapComponent.For(map)?.ViewedLayer??0;
        ShipLanding.PreserveJourney(gravship);
        if(map!=null)ShipLanding.EnsureCapacity(gravship,map,ShipLayers.RequiredLayers(gravship)-1);
        // Capture before native arrival: its random marker/camera jump can change ViewedLayer.
    }
    static void Postfix(Gravship gravship,int? __state)
    {
        if(!ShipLayers.IsLayerShip(gravship)||!__state.HasValue)return;
        var map=Find.WorldObjects.MapParentAt(gravship.destinationTile)?.Map;
        if(LayerMapComponent.For(map) is not {} owner)return;
        LongEventHandler.ExecuteWhenFinished(()=>
        {
            owner.SetViewedLayer(__state.Value);
            var center=owner.ToStorage(new(__state.Value,owner.Layout!.Width/2,owner.Layout.Height/2));
            CameraJumper.TryJump(center,map,CameraJumper.MovementMode.Cut);
        });
    }
}
[HarmonyPatch(typeof(WorldComponent_GravshipController),nameof(WorldComponent_GravshipController.InitiateTakeoff))]
internal static class LayerShipTakeoffView
{
    static bool Prefix(Building_GravEngine engine,PlanetTile targetTile)
    {
        if(!ShipLayers.Eligible(engine)||LayerMapComponent.For(engine.Map) is not {} owner)return true;
        if(!LayerShipDestination.TryPrepare(engine,targetTile))return false;
        return true;
    }
}
[HarmonyPatch(typeof(Designator_MoveGravship),"ValidGravshipLocation")]
internal static class LayerShipLandingPreview
{
    static void Postfix(Designator_MoveGravship __instance,GravshipLandingMarker ___marker,Map ___map,IntVec3 loc,List<IntVec3>? validCells,List<IntVec3>? invalidCells,ref AcceptanceReport __result)
    {
        if(!ShipLayers.IsLayerShip(___marker.gravship))return;
        var adjusted=(IntVec3)AccessTools.Method(typeof(Designator_MoveGravship),"GetSizeRotAdjustedCell").Invoke(__instance,new object[]{loc})!;
        var root=PrefabUtility.GetRoot(adjusted,___marker.gravship.Bounds.Size,___marker.GravshipRotation);
        // Height is a whole-ship constraint: report it even if native bottom-cell checks failed.
        if(LayerMapComponent.For(___map) is {} owner&&owner.TryDecode(root,out var at))
        {
            var height=ShipLanding.CheckHeight(___marker.gravship,owner.Elevation(at.LayerId));
            if(!height.Accepted)
            {
                // SelectedUpdate colors these lists, not AcceptanceReport itself.
                validCells?.Clear();
                if(invalidCells!=null)
                {
                    invalidCells.Clear();invalidCells.AddRange(___marker.GravshipCells.Select(c=>root+c));
                }
                __result=height;return;
            }
        }
        if(__result.Accepted)__result=ShipLanding.Check(___marker.gravship,root,___map);
    }
}
[HarmonyPatch(typeof(Designator_MoveGravship),"IsValidCell")]
internal static class LayerShipLandingBand
{
    [HarmonyBefore("vanillaexpanded.gravship")]
    static bool Prefix(IntVec3 cell,Map map,ref AcceptanceReport __result)
    {
        if(LayerMapComponent.For(map) is not {} owner)return true;
        if(!owner.TryDecode(cell,out var at)){__result="RL_ShipUpperOutOfBounds".Translate();return false;}
        if(owner.Elevation(at.LayerId)<0){__result="RL_DigShipUnsupported".Translate();return false;}
        if(at.LayerId==0)return true;
        var ground=new IntVec3(at.X,0,at.Z);
        if(at.X<1||at.Z<1||at.X>=owner.Layout!.Width-1||at.Z>=owner.Layout.Height-1||ground.InNoBuildEdgeArea(map))
        {__result="GravshipInNoBuildArea".Translate();return false;}
        if(map.landingBlockers?.Any(r=>r.Contains(cell)||r.Contains(ground))==true)
        {__result="GravshipInBlockedArea".Translate();return false;}
        if(cell.Fogged(map)){__result="GravshipBlockedByFog".Translate();return false;}
        if(map.roofGrid.RoofAt(cell)!=null||cell.GetThingList(map).Any(ShipLanding.BlocksLanding))
        {__result="RL_ShipUpperBlocked".Translate();return false;}
        __result=owner.Underlays.CanHost(cell)||!owner.StructuralCells.Contains(cell)&&
            !owner.Mountains.IsNaturalFloor(cell)&&owner.Structure.HasRequiredRoof(cell)?true:"RL_ShipUnsupportedLanding".Translate();
        return false;
    }
}

// Selection is read-only. Confirmed departure reserves capacity before native fuel use; arrival allocates it.
internal static class LayerShipDestination
{
    [ThreadStatic] internal static Building_GravEngine? Selecting;
    private static int RequiredTop(Building_GravEngine engine)=>LayerMapComponent.For(engine.Map)?.Ships.Get(engine) is {} ship?ship.Top-ship.Bottom:0;
    internal static AcceptanceReport Report(Building_GravEngine engine,PlanetTile tile)
    {
        if(!ShipLayers.Eligible(engine))return true;
        if(!tile.Valid)return "RL_ShipDestinationUnsupported".Translate();
        int top=RequiredTop(engine);
        var target=Find.WorldObjects.MapParentAt(tile)?.Map;
        if(top>0&&target!=null&&LayerMapComponent.For(target)==null&&!LiveMapExpansion.CanExpand(target))return "RL_ShipDestinationUnsupported".Translate();
        return LayerExpansionQuota.Current?.Preview(tile,top,engine.thingIDNumber)??true;
    }
    internal static bool TryPrepare(Building_GravEngine engine,PlanetTile tile)
    {
        var report=Report(engine,tile);
        if(report.Accepted&&ShipLayers.Eligible(engine))report=LayerExpansionQuota.Current?.Reserve(engine.thingIDNumber,tile,RequiredTop(engine))??true;
        if(report.Accepted)return true;
        LayerExpansionQuota.Current?.Release(engine.thingIDNumber);
        // Departure is a confirmed player action; a bare red line does not say which mod is at fault.
        RejectionReporter.Reject(RejectionAction.ShipTakeoff,report.Reason,true,
            engine?.def.defName,engine?.def.LabelCap,engine?.Map,engine?.Position,
            RejectionReporter.ElevationAt(engine?.Map,engine?.Position??IntVec3.Invalid));
        Messages.Message(report.Reason,MessageTypeDefOf.RejectInput,false);return false;
    }
}
// Native console supplies this action before DestroyTrees / ConsumeFuel / InitiateTakeoff.
// Wrap the confirmed action, not tile hover/validation or a still-cancellable dialog.
[HarmonyPatch(typeof(SettlementProximityGoodwillUtility),nameof(SettlementProximityGoodwillUtility.CheckConfirmSettle))]
internal static class LayerShipConfirmedDeparture
{
    static void Prefix(PlanetTile tile,Building_GravEngine gravEngine,ref Action settleAction)
    {
        // VGE selects the tile before its cancellable ritual dialog. Reserve at ExecuteGravshipLaunch instead.
        if(Compatibility.VanillaGravshipCompatibility.SelectingRitual)return;
        if(!ShipLayers.Eligible(gravEngine)||LayerMapComponent.For(gravEngine.Map)==null)return;
        var native=settleAction;
        settleAction=()=>ShipSeparation.Confirm(gravEngine,()=>
        {
            if(!LayerShipDestination.TryPrepare(gravEngine,tile))return;
            try { native(); }
            catch
            {
                // An exception after native takeoff began must not revoke an in-flight promise.
                if(!WorldComponent_GravshipController.CutsceneInProgress&&Find.CurrentGravship?.Engine!=gravEngine)
                    LayerExpansionQuota.Current?.Release(gravEngine.thingIDNumber);
                throw;
            }
        });
    }
}
[HarmonyPatch(typeof(CompPilotConsole),nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
internal static class LayerShipChoosingDestination
{
    static void Prefix(CompPilotConsole __instance,bool launching,out Building_GravEngine? __state)
    {__state=LayerShipDestination.Selecting;if(launching)LayerShipDestination.Selecting=__instance.engine;}
    static Exception? Finalizer(Exception? __exception,Building_GravEngine? __state)
    {LayerShipDestination.Selecting=__state;return __exception;}
}
[HarmonyPatch(typeof(TilePicker),nameof(TilePicker.StartTargeting_NewTemp))]
internal static class LayerShipDestinationValidation
{
    static void Prefix(ref Func<PlanetTile,bool> validator)
    {
        var engine=LayerShipDestination.Selecting;
        if(!ShipLayers.Eligible(engine)||LayerMapComponent.For(engine!.Map)==null)return;
        var native=validator;
        validator=tile=>
        {
            if(!native(tile))return false;
            var report=LayerShipDestination.Report(engine,tile);
            if(report.Accepted)return true;
            Messages.Message(report.Reason,MessageTypeDefOf.RejectInput,false);return false;
        };
    }
}

internal sealed class ShipConfirmationState
{
    internal ShipConfirmationState? Previous;
    internal GravshipLandingMarker? Rejected;
    [ThreadStatic] internal static ShipConfirmationState? Active;
}
[HarmonyPatch(typeof(WorldComponent_GravshipController),nameof(WorldComponent_GravshipController.WorldComponentOnGUI))]
internal static class LayerShipConfirmationUi
{
    static void Prefix(GravshipLandingMarker ___landingMarker,out ShipConfirmationState? __state)
    {
        __state=null;
        if(___landingMarker==null||!ShipLayers.IsLayerShip(___landingMarker.gravship))return;
        __state=new(){Previous=ShipConfirmationState.Active};ShipConfirmationState.Active=__state;
    }
    static Exception? Finalizer(Exception? __exception,ref GravshipLandingMarker ___landingMarker,ShipConfirmationState? __state)
    {
        if(__state!=null)
        {
            // Native UI clears its marker after the void BeginLanding call; keep it when rejected.
            if(__state.Rejected!=null&&___landingMarker==null)___landingMarker=__state.Rejected;
            ShipConfirmationState.Active=__state.Previous;
        }
        return __exception;
    }
}
[HarmonyPatch(typeof(GravshipLandingMarker),nameof(GravshipLandingMarker.BeginLanding))]
internal static class LayerShipConfirmLanding
{
    static bool Prefix(GravshipLandingMarker __instance)
    {
        if(!ShipLayers.IsLayerShip(__instance.gravship))return true;
        var report=ShipLanding.Check(__instance.gravship,__instance.Position,__instance.Map);
        Exception? failure=null;
        if(report.Accepted)
        {
            if(LayerMapComponent.For(__instance.Map) is {} owner)
            {
                try{ShipLanding.EnsureCapacity(__instance.gravship,__instance.Map,ShipLanding.RequiredAt(__instance.gravship,__instance.Position,owner)-1);}
                catch(Exception e){failure=e;Log.Error("[RimLayers] Landing expansion failed: "+e);report="RL_ShipExpansionFailed".Translate();}
            }
            if(report.Accepted)return true;
        }
        if(ShipConfirmationState.Active is {} state)state.Rejected=__instance;
        // A player pressed "land" and the layers refused; explain why instead of only a red line.
        if(failure!=null)
            RejectionReporter.Reject(RejectionAction.ShipLanding,failure,true,"RL_ShipExpansionFailed".Translate(),
                __instance.gravship?.def.defName,__instance.gravship?.def.LabelCap,__instance.Map,__instance.Position);
        else
            RejectionReporter.Reject(RejectionAction.ShipLanding,report.Reason,true,
                __instance.gravship?.def.defName,__instance.gravship?.def.LabelCap,__instance.Map,__instance.Position,
                RejectionReporter.ElevationAt(__instance.Map,__instance.Position));
        Messages.Message(report.Reason,MessageTypeDefOf.RejectInput,false);return false;
    }
}
