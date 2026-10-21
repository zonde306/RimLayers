using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Environment;

internal static class TornadoColumn
{
    internal static readonly Action<Tornado,IntVec3,float> Damage=AccessTools.Method(typeof(Tornado),"DoDamage").CreateDelegate<Action<Tornado,IntVec3,float>>();
    internal static readonly AccessTools.FieldRef<Tornado,Vector2> Real=AccessTools.FieldRefAccess<Tornado,Vector2>("realPosition");
    internal static bool Bounds(IntVec3 c,Map map)=>LayerMapComponent.For(map) is {} o&&o.Environment.Enabled?
        c.x>=0&&c.x<o.Layout!.Width&&c.z>=0&&c.z<o.Layout.Height:c.InBounds(map);
    internal static bool Exposed(LayerMapComponent owner,IntVec3 cell)
    {
        if(!owner.TryDecode(cell,out var at)||owner.Elevation(at.LayerId)<0||owner.Environment.RockShielded(cell))return false;
        var edifice=cell.GetEdifice(owner.map);
        if(edifice?.def.building?.isNaturalRock==true||edifice?.def==ThingDefOf.Wall&&edifice.Faction==null)return false;
        if(owner.Environment.WindExposed(cell))return true;
        return edifice!=null&&GenAdj.CardinalDirections.Any(d=>owner.TryDecode(cell+d,out var q)&&q.LayerId==at.LayerId&&owner.Environment.WindExposed(cell+d));
    }
    internal static IEnumerable<IntVec3> Cells(LayerMapComponent owner,IntVec3 ground)
    {
        if(!Bounds(ground,owner.map))yield break;
        foreach(int layer in owner.Layout!.OrderedLayers.Where(l=>owner.Elevation(l)>=0))
        {
            var cell=owner.ToStorage(new(layer,ground.x,ground.z));
            if(owner.Structure.HasSurface(cell)&&Exposed(owner,cell))yield return cell;
        }
    }
    internal static void Hit(Tornado tornado,IntVec3 cell,float factor)
    {
        if(LayerEnvironment.Settings.TornadoDamage<=0)return;
        var old=Real(tornado);
        try{var owner=LayerMapComponent.For(tornado.Map)!;owner.TryDecode(cell,out var local);
            Real(tornado)=new(old.x,old.y+cell.z-local.Z);Damage(tornado,cell,factor*LayerEnvironment.Settings.TornadoDamage);}
        finally{Real(tornado)=old;}
    }
}
[HarmonyPatch(typeof(Tornado),nameof(Tornado.SpawnSetup))]
internal static class TornadoGroundAnchor
{
    static void Postfix(Tornado __instance,Map map,bool respawningAfterLoad,ref Vector2 ___realPosition)
    {
        var owner=LayerMapComponent.For(map);
        if(owner?.Environment.Enabled!=true||respawningAfterLoad)return;
        int x,z;
        if(owner.TryDecode(__instance.Position,out var c)){x=c.X;z=c.Z;}
        else{x=Math.Clamp(__instance.Position.x,0,owner.Layout!.Width-1);z=Rand.Range(0,owner.Layout.Height);}
        __instance.Position=new(x,0,z);___realPosition=new(x+.5f,z+.5f);
    }
}
[HarmonyPatch(typeof(Tornado),"Tick")]
internal static class TornadoLogicalBounds
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>SkyQueries.Replace(instructions,
        AccessTools.Method(typeof(GenGrid),nameof(GenGrid.InBounds),new[]{typeof(IntVec3),typeof(Map)}),
        AccessTools.Method(typeof(TornadoColumn),nameof(TornadoColumn.Bounds)),1);
}
[HarmonyPatch(typeof(Tornado),"DamageCloseThings")]
internal static class TornadoCloseColumn
{
    static bool Prefix(Tornado __instance)
    {
        var owner=LayerMapComponent.For(__instance.Map);if(owner?.Environment.Enabled!=true)return true;
        foreach(var offset in GenRadial.RadialCellsAround(IntVec3.Zero,4.2f,true))
            foreach(var cell in TornadoColumn.Cells(owner,__instance.Position+offset))
            {
                var pawn=cell.GetFirstPawn(owner.map);if(pawn?.Downed==true&&Rand.Bool)continue;
                float factor=1-.8f*Math.Min(1,offset.LengthHorizontal/4.2f);
                TornadoColumn.Hit(__instance,cell,factor);
            }
        return false;
    }
}
[HarmonyPatch(typeof(Tornado),"DamageFarThings")]
internal static class TornadoFarColumn
{
    static bool Prefix(Tornado __instance)
    {
        var owner=LayerMapComponent.For(__instance.Map);if(owner?.Environment.Enabled!=true)return true;
        var candidates=GenRadial.RadialCellsAround(__instance.Position,10,true).Where(c=>TornadoColumn.Bounds(c,owner.map)).ToList();
        if(candidates.Count>0)foreach(var c in TornadoColumn.Cells(owner,candidates.RandomElement()))TornadoColumn.Hit(__instance,c,.5f);
        return false;
    }
}
[HarmonyPatch(typeof(Tornado),"DestroyRoofs")]
internal static class TornadoColumnRoofs
{
    static bool Prefix(Tornado __instance)
    {
        var owner=LayerMapComponent.For(__instance.Map);if(owner?.Environment.Enabled!=true)return true;
        if(LayerEnvironment.Settings.TornadoDamage<=0)return false;
        var removed=new List<IntVec3>();
        foreach(var offset in GenRadial.RadialCellsAround(IntVec3.Zero,4.2f,true))
            foreach(var cell in TornadoColumn.Cells(owner,__instance.Position+offset).ToArray())
                if(cell.GetRoof(owner.map) is {isThickRoof:false,isNatural:false})
                {RoofCollapserImmediate.DropRoofInCells(cell,owner.map);removed.Add(cell);}
        if(removed.Count>0)RoofCollapseCellsFinder.CheckCollapseFlyingRoofs(removed,owner.map,true);
        return false;
    }
}
[HarmonyPatch(typeof(Verb),nameof(Verb.CanHitTargetFrom))]
internal static class UndergroundTornadoGenerator
{
    static bool Prefix(Verb __instance,IntVec3 root,LocalTargetInfo targ,ref bool __result)
    {
        if(__instance.verbProps.spawnDef?.thingClass!=typeof(Tornado))return true;
        var owner=LayerMapComponent.For(__instance.caster?.Map);
        if(owner?.Environment.Enabled!=true)return true;
        if(owner.TryDecode(root,out var a)&&owner.TryDecode(targ.Cell,out var b)&&owner.Elevation(a.LayerId)>=0&&owner.Elevation(b.LayerId)>=0)return true;
        __result=false;return false;
    }
}
