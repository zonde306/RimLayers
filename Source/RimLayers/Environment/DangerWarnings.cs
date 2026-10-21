using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Environment;

public sealed class DangerWarnings
{
    private readonly LayerMapComponent owner;
    private static readonly Action<Pawn_MindState,Thing> Notify=AccessTools.MethodDelegate<Action<Pawn_MindState,Thing>>(AccessTools.Method(typeof(Pawn_MindState),"Notify_DangerousExploderAboutToExplode"));
    private readonly List<(Thing Source,DamageDef Damage,Faction? Faction,Thing? Instigator)> restored=new();
    private static readonly AccessTools.FieldRef<Projectile,bool> Landed=AccessTools.FieldRefAccess<Projectile,bool>("landed");
    private static readonly AccessTools.FieldRef<CompExplosive,Thing> Instigator=AccessTools.FieldRefAccess<CompExplosive,Thing>("instigator");
    public DangerWarnings(LayerMapComponent owner)=>this.owner=owner;
    private ShotPoint Point(Thing danger)
    {
        if(danger is Projectile projectile && CombatService.Flight(projectile) is {ImpactCommitted:true} flight)
                return new(flight.ImpactPoint.x,flight.ImpactPoint.y,flight.ImpactPoint.z);
        return new LayerShotScene(owner).Point(danger.Position,1);
    }
    public double DistanceSquared(Thing danger,IntVec3 cell)
    {
        var a=Point(danger);var b=new LayerShotScene(owner).Point(cell,1);
        return (a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z)+(a.Height-b.Height)*(a.Height-b.Height);
    }
    public bool Exposed(Thing danger,IntVec3 cell)
    {
        var scene=new LayerShotScene(owner,danger);
        return ShotGeometry.First(Point(danger),scene.Point(cell,1),scene)==null;
    }
    public void NotifyOtherFloors(Thing source,DamageDef damage,Faction? onlyFaction,Thing? instigator)
    {
        if(!source.Spawned || source.Map!=owner.map || !owner.TryDecode(source.Position,out var origin))return;
        foreach(Pawn pawn in owner.map.mapPawns.AllPawnsSpawned.ToList())
        {
            if(pawn==instigator || !owner.TryDecode(pawn.Position,out var place) || place.LayerId==origin.LayerId ||
                (int)pawn.RaceProps.intelligence<2 || onlyFaction!=null && pawn.Faction!=onlyFaction ||
                !damage.ExternalViolenceFor(pawn) || DistanceSquared(source,pawn.Position)>81 || !Exposed(source,pawn.Position))continue;
            Notify(pawn.mindState,source);
        }
    }
    public Job? Flee(Pawn pawn,Thing danger)
    {
        if((int)pawn.RaceProps.intelligence<2 || pawn.Downed && !pawn.health.CanCrawl ||
            PawnUtility.PlayerForcedJobNowOrSoon(pawn) || pawn.Drafted || DistanceSquared(danger,pawn.Position)>81 || !Exposed(danger,pawn.Position))return null;
        owner.TryDecode(pawn.Position,out var origin);
        float safeDistance=JobGiver_FleePotentialExplosion.FleeDist;
        if(danger is Projectile p)safeDistance=Math.Max(safeDistance,p.def.projectile.explosionRadius+1);
        if(danger.TryGetComp<CompExplosive>() is { } explosive)safeDistance=Math.Max(safeDistance,explosive.ExplosiveRadius()+1);
        int radius=(int)Math.Ceiling(safeDistance+3);
        var candidates=new List<(IntVec3 Cell,int Distance)>();
        for(int l=0;l<owner.Layout!.LayerCount;l++)
            foreach(IntVec3 local in new CellRect(origin.X-radius,origin.Z-radius,radius*2+1,radius*2+1))
            {
                if(!owner.Layout.Contains(new LayerCell(l,local.x,local.z)))continue;
                IntVec3 cell=owner.ToStorage(new LayerCell(l,local.x,local.z));
                if(cell==pawn.Position || !owner.Structure.HasSurface(cell) || !cell.WalkableBy(owner.map,pawn) ||
                    cell.GetDoor(owner.map) is {Open:false})continue;
                if(DistanceSquared(danger,cell)<safeDistance*safeDistance)continue;
                int dx=local.x-origin.X,dz=local.z-origin.Z,dy=(owner.Elevation(l)-owner.Elevation(origin.LayerId))*3;
                candidates.Add((cell,dx*dx+dz*dz+dy*dy));
            }
        foreach(var target in candidates.OrderBy(c=>c.Distance).ThenBy(c=>c.Cell.z).ThenBy(c=>c.Cell.x))
        {
            using var path=owner.map.pathFinder.FindPathNow(pawn.Position,target.Cell,pawn);
            if(!path.Found)continue;
            Job job=JobMaker.MakeJob(JobDefOf.Goto,target.Cell);job.locomotionUrgency=LocomotionUrgency.Sprint;return job;
        }
        return null;
    }
    public void Rebuild()
    {
        restored.Clear();
        foreach(Thing thing in owner.map.listerThings.AllThings)
        {
            if(thing is Projectile_Explosive p && Landed(p))restored.Add((p,p.DamageDef,p.Launcher?.Faction,p.Launcher));
            else if(thing.TryGetComp<CompExplosive>() is {wickStarted:true} comp)
                restored.Add((thing,comp.Props.explosiveDamageType,null,Instigator(comp)));
        }
    }
    public void Tick()
    {
        if(restored.Count==0)return;
        var pending=restored.ToArray();restored.Clear();
        foreach(var warning in pending)
            if(warning.Source.Spawned)GenExplosion.NotifyNearbyPawnsOfDangerousExplosive(warning.Source,warning.Damage,warning.Faction,warning.Instigator);
    }
}

[HarmonyPatch(typeof(GenExplosion),nameof(GenExplosion.NotifyNearbyPawnsOfDangerousExplosive))]
internal static class VerticalExplosionWarningPatch
{
    static void Postfix(Thing exploder,DamageDef damage,Faction onlyFaction,Thing instigator)=>
        LayerMapComponent.For(exploder.MapHeld)?.Warnings.NotifyOtherFloors(exploder,damage,onlyFaction,instigator);
}
[HarmonyPatch(typeof(JobGiver_FleePotentialExplosion),"TryGiveJob")]
internal static class VerticalExplosionFleePatch
{
    static bool Prefix(Pawn pawn,ref Job? __result)
    {
        var owner=LayerMapComponent.For(pawn.Map);Thing danger=pawn.mindState.knownExploder;
        if(owner==null || danger==null || !danger.Spawned || danger.Map!=pawn.Map ||
            !owner.TryDecode(pawn.Position,out var here) || !owner.TryDecode(danger.Position,out var source) || here.LayerId==source.LayerId)return true;
        __result=owner.Warnings.Flee(pawn,danger);return false;
    }
}

// The native flee giver refuses forced work, but its caller would still run the entire
// think tree and could replace that work with an unrelated idle job. Remember the
// threat now; ordinary native arbitration can reconsider it when the command ends.
[HarmonyPatch(typeof(Pawn_MindState),"Notify_DangerousExploderAboutToExplode")]
internal static class ExplosiveWarningArbitrationPatch
{
    static bool Prefix(Pawn_MindState __instance,Pawn ___pawn,Thing exploder)
    {
        if(LayerMapComponent.For(___pawn.Map)==null || (int)___pawn.RaceProps.intelligence<2 || ___pawn.Drafted ||
            !PawnUtility.PlayerForcedJobNowOrSoon(___pawn))return true;
        __instance.knownExploder=exploder;return false;
    }
}
