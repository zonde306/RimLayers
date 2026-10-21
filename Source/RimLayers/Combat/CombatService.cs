using RimLayers.Core;
using RimLayers.Layout;
using Verse;

namespace RimLayers.Combat;

public readonly record struct FireSolution(bool InRange, bool Clear, LayerCell AimCell, ShotObstruction? Obstruction)
{
    public bool GeometricallyPossible => InRange && Clear;
    public float EffectiveRange { get; init; }
    public float Exposure { get; init; }
    public bool Curved { get; init; }
    public bool LaunchBlocked { get; init; }
    public ShotPoint Start { get; init; }
    public ShotPoint Aim { get; init; }
    public float ArcHeight { get; init; }
    public ExposureSample[]? Samples { get; init; }
}

/// <summary>Shared geometry. Native verbs retain authority, accuracy, ammo and threat policy.</summary>
public sealed partial class CombatService
{
    private readonly LayerMapComponent owner;
    public CombatService(LayerMapComponent owner) => this.owner=owner;

    public float EffectiveRange(Verb verb,IntVec3 source,IntVec3 target)
    {
        if(!owner.TryDecode(source,out var a)||!owner.TryDecode(target,out var b))return verb.EffectiveRange;
        return verb.EffectiveRange*ShotProfiles.Multiplier(owner.Elevation(a.LayerId),owner.Elevation(b.LayerId),ShotProfiles.Curved(verb));
    }
    public float MaximumRange(Verb verb) => verb.EffectiveRange*ShotProfiles.Multiplier(owner.Layout!.HighestElevation,owner.Layout.LowestElevation,ShotProfiles.Curved(verb));

    public FireSolution SolveWeapon(Thing shooter,IntVec3 source,LocalTargetInfo target,Verb verb,bool ignoreRange=false)
    {
        float range=ignoreRange?float.PositiveInfinity:EffectiveRange(verb,source,target.Cell);
        float minimum=ignoreRange?0:verb.verbProps.EffectiveMinRange(target,shooter);
        return Solve(shooter,source,target,range,minimum,true,ShotProfiles.Projectile(verb),ShotProfiles.Overhead(verb));
    }

    public bool OpenLaunchColumn(IntVec3 source)
    {
        if(!owner.TryDecode(source,out var c))return false;
        foreach(int layer in new[]{c.LayerId}.Concat(owner.Layout!.Above(c.LayerId)))
        {
            IntVec3 at=owner.ToStorage(new LayerCell(layer,c.X,c.Z));
            if(owner.map.roofGrid.RoofAt(at)!=null || owner.Elevation(layer)>owner.Elevation(c.LayerId)&&owner.Structure.HasSurface(at))return false;
        }
        return true;
    }

    public FireSolution Solve(Thing shooter, IntVec3 source, LocalTargetInfo target,
        float range,float minimumRange=0,bool requireSight=true,ThingDef? projectile=null,bool overhead=false,Thing? silhouette=null)
    {
        if(shooter.Map!=owner.map||!owner.TryDecode(source,out var root)||!target.IsValid||
            target.HasThing&&(target.Thing.Map!=owner.map||!target.Thing.Spawned||target.Thing.Destroyed))return default;
        var scene=new LayerShotScene(owner,shooter,includeCover:true);
        ShotPoint start=scene.Point(source,SourceHeight(shooter));
        FireSolution best=default;
        IEnumerable<IntVec3> cells=target.HasThing?target.Thing.OccupiedRect().Cells:new[]{target.Cell};
        foreach(var cell in cells)
        {
            if(!owner.TryDecode(cell,out var aim))continue;
            double dx=aim.X-root.X,dz=aim.Z-root.Z,distance=Math.Sqrt(dx*dx+dz*dz);
            Thing? bodyShape=silhouette??target.Thing;
            var center=scene.Point(cell,AimHeight(bodyShape));
            bool curved=ShotProfiles.Curved(projectile)||overhead;
            float arc=ShotProfiles.Arc(projectile,distance,overhead);
            bool launchBlocked=overhead&&!OpenLaunchColumn(source);
            bool inRange=distance<=range+0.00001&&distance>=minimumRange;
            ShotPoint[] points=bodyShape is Pawn body && !curved
                ? ExposureGeometry.Body(start,center,owner.Elevation(aim.LayerId)*3,TopHeight(body),Math.Clamp(0.22*Math.Sqrt(body.BodySize),0.1,0.4))
                : new[]{center};
            var samples=new ExposureSample[points.Length];int clearCount=0;
            ShotObstruction? first=null;ShotPoint clearAim=center;
            for(int i=0;i<points.Length;i++)
            {
                var obstruction=requireSight?ExposureGeometry.First(start,points[i],arc,scene):null;
                if(obstruction is {} hit&&target.HasThing&&hit.ThingKey==target.Thing.thingIDNumber)obstruction=null;
                samples[i]=new(points[i],obstruction);
                if(obstruction==null){clearCount++;clearAim=points[i];}
                else if(first==null||obstruction.Value.Time<first.Value.Time)first=obstruction;
            }
            // Ground bombardment may intentionally meet a roof. Ordinary direct fire may not.
            bool clear=clearCount>0 || overhead&&!target.HasThing;
            var solution=new FireSolution(inRange,clear&&!launchBlocked,aim,first) {
                EffectiveRange=range,Exposure=(float)clearCount/points.Length,Curved=curved,
                LaunchBlocked=launchBlocked,Start=start,Aim=clearCount>0?clearAim:center,ArcHeight=arc,Samples=samples
            };
            if(solution.GeometricallyPossible)return solution;
            if(best.Samples==null||inRange&&!best.InRange||solution.Exposure>best.Exposure)best=solution;
        }
        return best;
    }
}
