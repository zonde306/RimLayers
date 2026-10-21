using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Recovery;

internal readonly record struct RecoveryTarget(IntVec3 Cell,RecoveryStage Stage);

/// <summary>Read-only candidates. Final placement rechecks the entire footprint before moving anything.</summary>
internal static class RecoveryLanding
{
    internal static bool HasSupport(LayerMapComponent owner,Thing thing,IntVec3 at)
    {
        if(!owner.TryDecode(at,out var center))return false;
        foreach(var c in GenAdj.OccupiedRect(at,thing.Rotation,thing.def.Size))
            if(!owner.TryDecode(c,out var local)||local.LayerId!=center.LayerId||!owner.Structure.HasSurface(c)||
                (owner.map.terrainGrid.TerrainAt(c)==RimLayersDefOf.RL_OpenAir||owner.map.terrainGrid.TerrainAt(c)==RimLayersDefOf.RL_Isolation||owner.map.terrainGrid.TerrainAt(c).defName=="Space"))return false;
        return true;
    }
    internal static bool Legal(LayerMapComponent owner,Thing thing,IntVec3 at)=>Legal(owner.map,thing,at);
    internal static bool Legal(Map map,Thing thing,IntVec3 at)
    {
        if(map.Disposed)return false;
        var owner=LayerMapComponent.For(map);
        if(owner!=null){if(!HasSupport(owner,thing,at))return false;}
        else
        {
            // Explicit manual recovery may target an ordinary native base map. No layer allocation.
            var footprint=GenAdj.OccupiedRect(at,thing.Rotation,thing.def.Size);
            if(!footprint.InBounds(map)||footprint.Any(c=>map.terrainGrid.TerrainAt(c) is {} t&&
                (t==RimLayersDefOf.RL_OpenAir||t==RimLayersDefOf.RL_Isolation||t.defName=="Space")))return false;
        }
        foreach(var c in GenAdj.OccupiedRect(at,thing.Rotation,thing.def.Size))
        {
            if(!c.Standable(map))return false;
            foreach(var other in c.GetThingList(map))
            {
                if(other==thing)continue;
                if(GenSpawn.SpawningWipes(thing.def,other.def))return false;
                if((thing is Pawn||thing.def.passability==Traversability.Impassable) && other is Pawn)return false;
                if(thing.def.category==ThingCategory.Item&&other.def.category==ThingCategory.Item&&
                    c.GetItemCount(map)-(thing.Spawned&&thing.OccupiedRect().Contains(c)?1:0)>=c.GetMaxItemsAllowedInCell(map))return false;
            }
            if(thing is Pawn pawn)
            {
                if(c.GetFirstThing<Fire>(map)!=null||PawnUtility.KnownDangerAt(c,map,pawn))return false;
                if(c.GetRegion(map,RegionType.Set_All) is {} region&&region.DangerFor(pawn)==Danger.Deadly)return false;
                if(map.Tile.Layer.Def.isSpace&&(owner?.Sky.IsExposed(c)??(map.roofGrid.RoofAt(c)==null))&&pawn.HarmedByVacuum)return false;
                if(c.GetRoom(map) is {} room&&room.Vacuum>.5f&&pawn.HarmedByVacuum)return false;
            }
        }
        return true;
    }
    internal static IEnumerable<RecoveryTarget> Candidates(LayerMapComponent owner,Thing thing,RecoveryRecord record)
    {
        var seen=new HashSet<IntVec3>();
        bool known=owner.TryDecode(record.Origin,out var origin);
        if((record.StructuralFall||RimLayersMod.Settings.RecoveryMode==RecoveryPreference.FallThenReset)&&known)
        {
            foreach(var at in FallCandidates(owner,thing,record.Origin))if(seen.Add(at))yield return new(at,RecoveryStage.Falling);
        }
        if(record.LastMapId==owner.map.uniqueID&&record.LastValid.IsValid&&seen.Add(record.LastValid))yield return new(record.LastValid,RecoveryStage.Resetting);
        if(known)
        {
            foreach(var cell in GenRadial.RadialCellsAround(record.Origin,12,true))
                if(owner.TryDecode(cell,out var local)&&local.LayerId==origin.LayerId&&seen.Add(cell))yield return new(cell,RecoveryStage.Resetting);
            foreach(var cell in owner.LayerRect(origin.LayerId))if(!seen.Contains(cell))yield return new(cell,RecoveryStage.Resetting);
        }
        // Invalid coordinates carry no inferred altitude. Never use modulo or a colonist anchor.
        foreach(int layer in owner.Layout!.OrderedLayers)
        {
            if(known&&layer==origin.LayerId)continue;
            foreach(var cell in owner.LayerRect(layer))if(!seen.Contains(cell))yield return new(cell,RecoveryStage.Resetting);
        }
    }
    internal static int ContactElevation(LayerMapComponent owner,IntVec3 source)
    {
        if(!owner.TryDecode(source,out var origin))return 0;
        foreach(int layer in owner.Layout!.Below(origin.LayerId))
        {
            var at=owner.ToStorage(new LayerCell(layer,origin.X,origin.Z));
            if(owner.Structure.HasSurface(at)||owner.map.roofGrid.RoofAt(at)!=null||at.GetEdifice(owner.map) is Mineable)return owner.Elevation(layer);
        }
        return owner.Elevation(origin.LayerId);
    }
    internal static IEnumerable<IntVec3> FallCandidates(LayerMapComponent owner,Thing thing,IntVec3 source)
    {
        if(!owner.TryDecode(source,out var origin))yield break;
        // The first intercepted plane owns the fall. A blocked landing never resumes below it.
        foreach(int layer in owner.Layout!.Below(origin.LayerId))
        {
            var center=owner.ToStorage(new LayerCell(layer,origin.X,origin.Z));
            bool contact=GenAdj.OccupiedRect(center,thing.Rotation,thing.def.Size).Any(c=>
                owner.TryDecode(c,out _)&&(owner.Structure.HasSurface(c)||owner.map.roofGrid.RoofAt(c)!=null||c.GetEdifice(owner.map) is Mineable));
            if(!contact)continue;
            // A roof is a barrier, not permission to place an entity underneath it.
            bool roof=GenAdj.OccupiedRect(center,thing.Rotation,thing.def.Size).Any(c=>c.InBounds(owner.map)&&owner.map.roofGrid.RoofAt(c)!=null);
            if(roof)yield break;
            foreach(var cell in GenRadial.RadialCellsAround(center,8,true))
                if(owner.TryDecode(cell,out var local)&&local.LayerId==layer)yield return cell;
            yield break;
        }
    }
}
