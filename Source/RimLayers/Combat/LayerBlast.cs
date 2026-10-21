using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Combat;

/// <summary>The native Explosion owns its pending cells and damagedThings receipt list.</summary>
public sealed class LayerBlast : IExposable
{
    public Explosion Explosion=null!;
    public Vector3 Origin;
    public int Version=1;
    public float? GasRadius;
    public int GasAmount=255;
    public void ExposeData()
    {
        if(Scribe.mode==LoadSaveMode.Saving && Explosion!=null)
        { GasRadius=Explosion.postExplosionGasRadiusOverride;GasAmount=Explosion.postExplosionGasAmount; }
        Scribe_Values.Look(ref Version,"version",1);
        Explosion? reference=Explosion;
        Scribe_References.Look(ref reference,"explosion");
        Explosion=reference!;
        Scribe_Values.Look(ref Origin,"origin");
        // These two native Explosion fields are not included by 1.7 Explosion.ExposeData.
        Scribe_Values.Look(ref GasRadius,"gasRadius");
        Scribe_Values.Look(ref GasAmount,"gasAmount",255);
        if(Scribe.mode==LoadSaveMode.PostLoadInit && Version!=1)
            throw new InvalidOperationException("Unsupported RimLayers blast version.");
    }
}

internal static class BlastAccess
{
    internal static readonly AccessTools.FieldRef<Explosion,List<IntVec3>> Cells=AccessTools.FieldRefAccess<Explosion,List<IntVec3>>("cellsToAffect");
    internal static readonly AccessTools.FieldRef<Explosion,List<Thing>> Damaged=AccessTools.FieldRefAccess<Explosion,List<Thing>>("damagedThings");
    internal static readonly AccessTools.FieldRef<Explosion,List<Thing>> Ignored=AccessTools.FieldRefAccess<Explosion,List<Thing>>("ignoredThings");
    internal static readonly AccessTools.FieldRef<Explosion,HashSet<IntVec3>> Neighbors=AccessTools.FieldRefAccess<Explosion,HashSet<IntVec3>>("addedCellsAffectedOnlyByDamage");
    internal static readonly AccessTools.FieldRef<Explosion,int> Started=AccessTools.FieldRefAccess<Explosion,int>("startTick");
    internal static readonly Action<Explosion,ThingDef,IntVec3,int> Spawn=AccessTools.MethodDelegate<Action<Explosion,ThingDef,IntVec3,int>>(
        AccessTools.Method(typeof(Explosion),"TrySpawnExplosionThing"));
    [ThreadStatic] internal static Explosion? Current;
    [ThreadStatic] internal static IntVec3 Cell;
    [ThreadStatic] internal static (Map Map,IntVec3 Cell,Vector3 Point)? Impact;
}

public sealed partial class CombatService
{
    private List<LayerBlast> blasts=new();
    private static readonly ConditionalWeakTable<Explosion,LayerBlast> blastIndex=new();
    public IReadOnlyList<LayerBlast> Blasts=>blasts;
    public static LayerBlast? Blast(Explosion explosion)=>blastIndex.TryGetValue(explosion,out var result)?result:null;
    internal LayerBlast BeginBlast(Explosion explosion)
    {
        if(Blast(explosion) is {} existing) return existing;
        var scene=new LayerShotScene(owner);
        ShotPoint point=scene.Point(explosion.Position,1);
        if(BlastAccess.Impact is {} impact && impact.Map==owner.map && impact.Cell==explosion.Position)
        {
            // Swept solid contact already records the incoming side. Its physical height can
            // be above the highest roof even though storage projects into the last valid band.
            point=new(impact.Point.x,impact.Point.y,impact.Point.z);
        }
        var blast=new LayerBlast {Explosion=explosion,Origin=Vector(point),
            GasRadius=explosion.postExplosionGasRadiusOverride,GasAmount=explosion.postExplosionGasAmount};
        blasts.Add(blast);blastIndex.Add(explosion,blast);
        return blast;
    }
    internal float BlastDistance(Explosion explosion,IntVec3 cell)
    {
        var blast=Blast(explosion);
        if(blast==null || !owner.TryDecode(cell,out var decoded)) return float.PositiveInfinity;
        return Vector3.Distance(blast.Origin,new Vector3(decoded.X+0.5f,owner.Elevation(decoded.LayerId)*3+1,decoded.Z+0.5f));
    }
    internal List<IntVec3> BlastCells(Explosion explosion)
    {
        LayerBlast blast=BeginBlast(explosion);
        var result=new List<IntVec3>();
        // Freeze geometric coverage at detonation. A wall destroyed by this blast does not let
        // the same wave retroactively reach rooms that were shielded at its start.
        var scene=new LayerShotScene(owner,ignoredCell:explosion.Position);
        ShotPoint origin=new(blast.Origin.x,blast.Origin.y,blast.Origin.z);
        int minX=Math.Max(0,(int)Math.Floor(origin.X-explosion.radius));
        int maxX=Math.Min(owner.Layout!.Width-1,(int)Math.Ceiling(origin.X+explosion.radius));
        int minZ=Math.Max(0,(int)Math.Floor(origin.Z-explosion.radius));
        int maxZ=Math.Min(owner.Layout.Height-1,(int)Math.Ceiling(origin.Z+explosion.radius));
        for(int layer=0;layer<owner.Layout.LayerCount;layer++)
        {
            if(Math.Abs(owner.Elevation(layer)*3+1-origin.Height)>explosion.radius) continue;
            for(int z=minZ;z<=maxZ;z++) for(int x=minX;x<=maxX;x++)
            {
                var cell=owner.ToStorage(new LayerCell(layer,x,z));
                float distance=BlastDistance(explosion,cell);
                if(distance>explosion.radius+0.0001f) continue;
                if(explosion.affectedAngle is {} angle)
                {
                    double dx=x+0.5-origin.X,dz=z+0.5-origin.Z,flat=Math.Sqrt(dx*dx+dz*dz);
                    float theta=(float)(Math.Atan2(-dz,dx)*180/Math.PI),margin=(float)(0.5*flat/explosion.radius);
                    if(flat<=0.5 || theta-angle.min < -margin || theta-angle.max > margin) continue;
                }
                ShotPoint destination=scene.Point(cell,1);
                if(!BlastVisible(scene,origin,destination,owner.Elevation(layer),x,z)) continue;
                if(explosion.needLOSToCell1.HasValue || explosion.needLOSToCell2.HasValue)
                {
                    bool VisibleFrom(IntVec3? from)=>from.HasValue && owner.TryDecode(from.Value,out _) &&
                        BlastVisible(scene,scene.Point(from.Value,1),destination,owner.Elevation(layer),x,z);
                    if(!VisibleFrom(explosion.needLOSToCell1) && !VisibleFrom(explosion.needLOSToCell2)) continue;
                }
                result.Add(cell);
            }
        }
        return result;
    }
    private static bool BlastVisible(LayerShotScene scene,ShotPoint a,ShotPoint b,int layer,int x,int z)
    {
        var obstruction=ShotGeometry.First(a,b,scene);
        return obstruction==null || obstruction.Value.Kind==ObstructionKind.Wall &&
            obstruction.Value.Layer==layer && obstruction.Value.X==x && obstruction.Value.Z==z;
    }
    internal void AffectBlastCell(Explosion explosion,IntVec3 cell)
    {
        if(!owner.TryDecode(cell,out _)) return;
        float distance=BlastDistance(explosion,cell);
        if(explosion.excludeRadius>0 && distance<explosion.excludeRadius) return;
        TerrainDef terrain=cell.GetTerrain(owner.map);
        bool damageOnly=explosion.applyDamageToExplosionCellsNeighbors && BlastAccess.Neighbors(explosion).Contains(cell);
        if(!damageOnly && Rand.Chance(explosion.preExplosionSpawnChance) && cell.Walkable(owner.map))
            BlastAccess.Spawn(explosion,explosion.preExplosionSpawnThingDef,cell,explosion.preExplosionSpawnThingCount);
        var previous=(BlastAccess.Current,BlastAccess.Cell);
        BlastAccess.Current=explosion;BlastAccess.Cell=cell;
        try { explosion.damType.Worker.ExplosionAffectCell(explosion,cell,BlastAccess.Damaged(explosion),BlastAccess.Ignored(explosion),!damageOnly); }
        finally { (BlastAccess.Current,BlastAccess.Cell)=previous; }
        if(!damageOnly)
        {
            if(Rand.Chance(explosion.postExplosionSpawnChance) && cell.Walkable(owner.map))
                BlastAccess.Spawn(explosion,terrain.IsWater ? explosion.postExplosionSpawnThingDefWater??explosion.postExplosionSpawnThingDef :
                    explosion.postExplosionSpawnThingDef,cell,explosion.postExplosionSpawnThingCount);
            if(explosion.postExplosionGasType.HasValue && distance<=(explosion.postExplosionGasRadiusOverride??explosion.radius))
                GasUtility.AddGas(cell,owner.map,explosion.postExplosionGasType.Value,explosion.postExplosionGasAmount);
        }
        float chance=explosion.chanceToStartFire;
        if(explosion.damageFalloff) chance*=Mathf.Lerp(1,0.2f,distance/explosion.radius);
        if(Rand.Chance(chance)) FireUtility.TryStartFireIn(cell,owner.map,Rand.Range(0.1f,0.925f),explosion.instigator,explosion.flammabilityChanceCurve);
        if(terrain.temporary && terrain.tempTerrain?.removedByExplosions==true) owner.map.terrainGrid.RemoveTempTerrain(cell);
    }
}
