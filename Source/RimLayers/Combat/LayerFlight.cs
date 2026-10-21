using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Combat;

/// <summary>Only immutable launch facts and the committed phase are saved; the Projectile remains vanilla-owned.</summary>
public sealed class LayerFlight : IExposable
{
    public Projectile Projectile = null!;
    public Thing? Emitter;
    public Vector3 Start, End;
    public float ArcHeight, LastProgress;
    public int TotalTicks, StorageLayer, Version=1;
    public bool ImpactCommitted,ShieldCommitted;
    public int ImpactLayer;
    internal ShotPoint? PendingImpact;
    public Vector3 ImpactPoint;
    public List<Thing> CheckedThings=new();
    internal ShotPoint? ActivePoint;
    public ShotPoint Point(double t)
    {
        if(ImpactCommitted || ShieldCommitted) return new(ImpactPoint.x,ImpactPoint.y,ImpactPoint.z);
        return new(Start.x+(End.x-Start.x)*t,
            Start.y+(End.y-Start.y)*t+4*ArcHeight*t*(1-t), Start.z+(End.z-Start.z)*t);
    }
    public double Progress => TotalTicks<=0 ? 1 : Math.Clamp(1d-(double)ProjectileAccess.Ticks(Projectile)/TotalTicks,0,1);
    // Draw the last consumed contact when a save interrupted a large-delta sweep.
    public ShotPoint DrawPoint => Point(Math.Min(LastProgress,Progress));
    public void ExposeData()
    {
        if(Scribe.mode==LoadSaveMode.Saving) CheckedThings.RemoveAll(t=>t==null || t.Destroyed);
        Scribe_Values.Look(ref Version,"version",1);
        Scribe_References.Look(ref Projectile,"projectile");
        Scribe_References.Look(ref Emitter,"emitter");
        Scribe_Values.Look(ref Start,"start");
        Scribe_Values.Look(ref End,"end");
        Scribe_Values.Look(ref ArcHeight,"arcHeight");
        Scribe_Values.Look(ref TotalTicks,"totalTicks");
        Scribe_Values.Look(ref StorageLayer,"storageLayer");
        Scribe_Values.Look(ref LastProgress,"lastProgress");
        Scribe_Values.Look(ref ImpactCommitted,"impactCommitted");
        Scribe_Values.Look(ref ShieldCommitted,"shieldCommitted");
        Scribe_Values.Look(ref ImpactLayer,"impactLayer");
        Scribe_Values.Look(ref ImpactPoint,"impactPoint");
        Scribe_Collections.Look(ref CheckedThings,"checkedThings",LookMode.Reference);
        if(Scribe.mode==LoadSaveMode.PostLoadInit) { CheckedThings??=new(); CheckedThings.RemoveAll(t=>t==null); }
        if(Scribe.mode==LoadSaveMode.PostLoadInit && Version!=1)
            throw new InvalidOperationException("Unsupported RimLayers flight version.");
    }
}

internal static class ProjectileAccess
{
    internal static readonly AccessTools.FieldRef<Projectile,Vector3> Origin=AccessTools.FieldRefAccess<Projectile,Vector3>("origin");
    internal static readonly AccessTools.FieldRef<Projectile,Vector3> Destination=AccessTools.FieldRefAccess<Projectile,Vector3>("destination");
    internal static readonly AccessTools.FieldRef<Projectile,int> Ticks=AccessTools.FieldRefAccess<Projectile,int>("ticksToImpact");
    internal static readonly AccessTools.FieldRef<Projectile,int> Lifetime=AccessTools.FieldRefAccess<Projectile,int>("lifetime");
    internal static readonly AccessTools.FieldRef<Projectile,bool> PreventFriendly=AccessTools.FieldRefAccess<Projectile,bool>("preventFriendlyFire");
    internal static readonly Func<Projectile,Thing,bool> CanHit=AccessTools.MethodDelegate<Func<Projectile,Thing,bool>>(
        AccessTools.Method(typeof(Projectile),"CanHit"));
    internal static readonly Action<Projectile,Thing?,bool> Impact=AccessTools.MethodDelegate<Action<Projectile,Thing?,bool>>(
        AccessTools.Method(typeof(Projectile),"Impact"));
}

public sealed partial class CombatService
{
    private List<LayerFlight> flights=new();
    private static readonly ConditionalWeakTable<Projectile,LayerFlight> flightIndex=new();
    public int CommittedImpacts { get; internal set; }
    public IReadOnlyList<LayerFlight> Flights=>flights;
    public static LayerFlight? Flight(Projectile projectile) =>
        flightIndex.TryGetValue(projectile,out var value) ? value : null;

    public void ExposeData()
    {
        if(Scribe.mode==LoadSaveMode.Saving) Tick();
        Scribe_Collections.Look(ref flights,"rimLayersFlights",LookMode.Deep);
        Scribe_Collections.Look(ref blasts,"rimLayersBlasts",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit) blasts??=new();
        if(Scribe.mode==LoadSaveMode.PostLoadInit) flights??=new();
    }
    public void RebuildFlights()
    {
        foreach(var blast in blasts)
            if(blast.Explosion!=null && !blast.Explosion.Destroyed)
            {
                blastIndex.AddOrUpdate(blast.Explosion,blast);
                blast.Explosion.postExplosionGasRadiusOverride=blast.GasRadius;
                blast.Explosion.postExplosionGasAmount=blast.GasAmount;
            }
        foreach(var flight in flights)
            if(flight.Projectile!=null && !flight.Projectile.Destroyed)
                flightIndex.AddOrUpdate(flight.Projectile,flight);
    }
    public void Tick()
    {
        for(int i=blasts.Count-1;i>=0;i--)
            if(blasts[i].Explosion==null || blasts[i].Explosion.Destroyed)
            {
                if(blasts[i].Explosion!=null) blastIndex.Remove(blasts[i].Explosion);
                blasts.RemoveAt(i);
            }
        for(int i=flights.Count-1;i>=0;i--)
            if(flights[i].Projectile==null || flights[i].Projectile.Destroyed)
            {
                if(flights[i].Projectile!=null) flightIndex.Remove(flights[i].Projectile);
                flights.RemoveAt(i);
            }
    }

    internal void Launched(Projectile projectile,Vector3 originalOrigin)
    {
        Vector3 destination=ProjectileAccess.Destination(projectile);
        if(GenCommandLine.CommandLineArgPassed("rimlayersv1test")) Log.Message($"[RimLayers.Flight] Launch {originalOrigin}->{destination}, type={projectile.GetType().Name}");
        if(!owner.TryDecode(originalOrigin.ToIntVec3(),out var source) ||
            !owner.TryDecode(destination.ToIntVec3(),out var target)) return;
        int landing=target.LayerId;
        if(!projectile.usedTarget.HasThing)
            while(!owner.Structure.HasSurface(owner.ToStorage(new LayerCell(landing,target.X,target.Z)))&&owner.Layout!.TryGetAdjacentLayer(landing,-1,out int below))landing=below;
        // A same-floor shot can later fall off an edge; track it too when its endpoint descends.
        if(source.LayerId==landing && !ShotProfiles.Curved(projectile.def)) return;
        Thing? actor=CombatCoordinates.CastingThing??projectile.Launcher;
        ShotPoint? selectedAim=null;
        if(projectile.usedTarget.HasThing && projectile.usedTarget.Thing==projectile.intendedTarget.Thing &&
            actor?.Map==owner.map && !ShotProfiles.Curved(projectile.def))
        {
            var solution=Solve(actor,originalOrigin.ToIntVec3(),projectile.usedTarget,float.PositiveInfinity);
            if(solution.Samples is {Length:>0} samples)
            {
                // One uniform silhouette sample is the entire cover roll. Obstructed samples
                // remain real trajectories and hit their first obstacle in Sweep.
                selectedAim=samples[Rand.Range(0,samples.Length)].Aim;
                destination=owner.ToStorage(solution.AimCell).ToVector3Shifted();
                owner.TryDecode(destination.ToIntVec3(),out target);landing=target.LayerId;
            }
        }
        Vector3 start=new(originalOrigin.x,owner.Elevation(source.LayerId)*3+SourceHeight(actor),originalOrigin.z-source.LayerId*owner.Layout!.Stride);
        Vector3 end=selectedAim is {} sample ? Vector(sample) :
            new(destination.x,owner.Elevation(landing)*3+AimHeight(projectile.usedTarget.Thing),destination.z-target.LayerId*owner.Layout.Stride);
        Vector3 nativeOrigin=originalOrigin;
        nativeOrigin.z+=(landing-source.LayerId)*owner.Layout.Stride;
        destination.z+=(landing-target.LayerId)*owner.Layout.Stride;
        ProjectileAccess.Origin(projectile)=nativeOrigin;
        ProjectileAccess.Destination(projectile)=destination;
        if(!projectile.usedTarget.HasThing)
            projectile.usedTarget=new IntVec3((int)Math.Floor(end.x),0,(int)Math.Floor(end.z)+landing*owner.Layout.Stride);
        int ticks=Math.Max(1,Mathf.CeilToInt(new Vector2(end.x-start.x,end.z-start.z).magnitude/
            Math.Max(projectile.def.projectile.SpeedTilesPerTick,0.001f)));
        ProjectileAccess.Ticks(projectile)=ProjectileAccess.Lifetime(projectile)=ticks;
        var flight=new LayerFlight {
            Projectile=projectile,Emitter=actor,Start=start,End=end,StorageLayer=landing,TotalTicks=ticks,
            ArcHeight=ShotProfiles.Arc(projectile.def,new Vector2(end.x-start.x,end.z-start.z).magnitude)
        };
        flights.Add(flight);flightIndex.AddOrUpdate(projectile,flight);
        if(GenCommandLine.CommandLineArgPassed("rimlayersv1test")) Log.Message($"[RimLayers.Flight] Snapshot ticks={ticks}, start={start}, end={end}");
        var position=originalOrigin.ToIntVec3();
        if(position.InBounds(owner.map)) projectile.Position=position;
    }

    internal static float AimHeight(Thing? thing) => thing is Pawn pawn ?
        pawn.GetPosture()==PawnPosture.Standing ? TopHeight(pawn)*0.75f : 0.3f :
        thing==null ? 0.1f : thing.def.Fillage==FillCategory.Full ? 1.35f : Math.Clamp(thing.def.fillPercent*1.5f,0.1f,1.35f);
    internal static float SourceHeight(Thing? thing)=>thing is Pawn ? AimHeight(thing) : 1.35f;
    internal static float TopHeight(Thing thing) => thing is Pawn pawn ?
        pawn.GetPosture()==PawnPosture.Standing ? Math.Clamp(1.8f*pawn.BodySize,0.6f,3f) : 0.45f :
        ShotProfiles.CoverHeight(thing);
    internal static Vector3 Vector(ShotPoint p)=>new((float)p.X,(float)p.Height,(float)p.Z);
    internal Vector3 Project(ShotPoint point,int layer,float altitude) =>
        new((float)point.X,altitude,(float)point.Z+layer*owner.Layout!.Stride);

    internal void PlaceImpact(LayerFlight flight,ShotPoint point,int? specificLayer=null)
    {
        int layer=specificLayer??owner.Layout!.ProjectionLayer(point.Height);
        int delta=(layer-flight.StorageLayer)*owner.Layout!.Stride;
        Vector3 origin=ProjectileAccess.Origin(flight.Projectile); origin.z+=delta;
        ProjectileAccess.Origin(flight.Projectile)=origin;
        ProjectileAccess.Destination(flight.Projectile)=Project(point,layer,0);
        flight.StorageLayer=layer;flight.ImpactPoint=Vector(point);
        flight.ImpactLayer=layer;flight.PendingImpact=point;
        IntVec3 cell=Project(point,layer,0).ToIntVec3();
        if(cell.InBounds(owner.map)) flight.Projectile.Position=cell;
    }
}
