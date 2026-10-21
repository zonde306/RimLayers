using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Combat;

public sealed partial class CombatService
{
    private sealed record Collision(double Time,IntVec3 Cell,Thing? Thing=null,
        CompProjectileInterceptor? Shield=null,bool Exit=false,ShotObstruction? Solid=null);
    internal bool Sweep(LayerFlight flight)
    {
        if(flight.ImpactCommitted) return true;
        if(flight.ShieldCommitted)
        {
            PlaceImpact(flight,new ShotPoint(flight.ImpactPoint.x,flight.ImpactPoint.y,flight.ImpactPoint.z),flight.ImpactLayer);
            ProjectileAccess.Impact(flight.Projectile,null,true);
            return true;
        }
        double from=flight.LastProgress,to=flight.Progress;
        if(to<=from) return false;
        // Quadratic overhead arcs are chord-approximated with at most .01 tile height error.
        int pieces=Math.Max(1,(int)Math.Ceiling((to-from)*Math.Sqrt(flight.ArcHeight/0.01)));
        for(int i=0;i<pieces;i++)
        {
            double low=from+(to-from)*i/pieces,high=from+(to-from)*(i+1)/pieces;
            ShotPoint a=flight.Point(low),b=flight.Point(high);
            var events=new List<Collision>();
            var scene=new LayerShotScene(owner,flight.Emitter??flight.Projectile.Launcher,includeCover:true);
            var solid=ShotGeometry.First(a,b,scene);
            if(solid is {} wall)
                events.Add(new(wall.Time,owner.AtElevation(wall.Layer,wall.X,wall.Z),Solid:wall));
            var seen=new HashSet<Thing>();
            foreach(var span in ShotGeometry.Cells(a,b,3))
            {
                if(span.X<0 || span.X>=owner.Layout!.Width || span.Z<0 || span.Z>=owner.Layout.Height)
                { events.Add(new(span.Start,IntVec3.Invalid,Exit:true)); continue; }
                if(!owner.Layout.TryGetLayerAtElevation(span.Layer,out int layerId)) continue; // Actual sky, not a storage band.
                IntVec3 cell=owner.ToStorage(new LayerCell(layerId,span.X,span.Z));
                foreach(Thing thing in cell.GetThingList(owner.map).ToList())
                {
                    if(!seen.Add(thing) || flight.CheckedThings.Contains(thing) || thing==flight.Projectile ||
                        thing==(flight.Emitter??flight.Projectile.Launcher) || thing.def.Fillage==FillCategory.Full ||
                        thing is not Pawn) continue;
                    double entry=span.Start,exit=span.End,dy=b.Height-a.Height;
                    double bottom=span.Layer*3,top=bottom+TopHeight(thing);
                    if(Math.Abs(dy)<1e-9) { if(a.Height<bottom || a.Height>top) continue; }
                    else
                    {
                        double h0=(bottom-a.Height)/dy,h1=(top-a.Height)/dy;
                        entry=Math.Max(entry,Math.Min(h0,h1));exit=Math.Min(exit,Math.Max(h0,h1));
                    }
                    if(entry<=exit+1e-9) events.Add(new(entry,cell,thing));
                }
            }
            foreach(Thing shieldThing in owner.map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor))
            {
                if(!owner.TryDecode(shieldThing.Position,out var location)) continue;
                var shield=shieldThing.TryGetComp<CompProjectileInterceptor>();
                if(shield==null || !shield.Active) continue;
                double bottom=owner.Elevation(location.LayerId)*3,top=bottom+3,radius=shield.Props.radius;
                double x=location.X+0.5,z=location.Z+0.5;
                bool Inside(ShotPoint p)=>(p.X-x)*(p.X-x)+(p.Z-z)*(p.Z-z)<=radius*radius &&
                    p.Height>=bottom && p.Height<=top;
                double? time;
                if(Inside(a))
                {
                    if(!shield.Props.interceptOutgoingProjectiles || Inside(b)) continue;
                    double? reverse=ShotGeometry.CylinderEntry(b,a,x,z,radius,bottom,top);
                    time=reverse.HasValue ? 1-reverse.Value : null;
                }
                else time=ShotGeometry.CylinderEntry(a,b,x,z,radius,bottom,top);
                if(time.HasValue) events.Add(new(time.Value,shieldThing.Position,Shield:shield));
            }
            foreach(var hit in events.OrderBy(e=>e.Time).ThenBy(e=>e.Solid==null?1:0))
            {
                if(flight.Projectile.Destroyed) return true;
                // Only consumed space may survive a callback save; later contacts stay pending.
                flight.LastProgress=(float)(low+(high-low)*Math.Clamp(hit.Time,0,1));
                ShotPoint point=a.At(b,Math.Clamp(hit.Time,0,1));
                if(hit.Exit) { flight.Projectile.Destroy(); return true; }
                if(hit.Shield!=null)
                {
                    if(!ShieldIntercept(flight,hit.Shield,a,b,point)) continue;
                    PlaceImpact(flight,point,flight.ImpactLayer);
                    ProjectileAccess.Impact(flight.Projectile,null,true);
                    return true;
                }
                if(hit.Solid is {} obstruction)
                {
                    Thing? obstacle=obstruction.ThingKey==0 ? null :
                        hit.Cell.GetThingList(owner.map).FirstOrDefault(t=>t.thingIDNumber==obstruction.ThingKey);
                    // Obstruction.Layer identifies the solid, not the incoming side of its face.
                    // Persist the contact just outside that face, and project only its storage layer.
                    ShotPoint approach=ShotGeometry.Approach(a,b,Math.Clamp(hit.Time,0,1));
                    PlaceImpact(flight,approach);
                    ProjectileAccess.Impact(flight.Projectile,obstacle,false);
                    return true;
                }
                Thing thing=hit.Thing!;
                flight.ActivePoint=point;
                bool allowed;
                try { allowed=ProjectileAccess.CanHit(flight.Projectile,thing); }
                finally { flight.ActivePoint=null; }
                if(!allowed) continue;
                flight.CheckedThings.Add(thing); // One chance per actual object, also across a save/restart.
                float chance;
                if(thing is Pawn pawn)
                {
                    chance=0.4f*Mathf.Clamp(pawn.BodySize,0.1f,2);
                    if(pawn.GetPosture()!=PawnPosture.Standing) chance*=0.1f;
                    Thing? launcher=flight.Projectile.Launcher;
                    if(launcher?.Faction!=null && pawn.Faction!=null && !pawn.Faction.HostileTo(launcher.Faction))
                        chance*=ProjectileAccess.PreventFriendly(flight.Projectile) ? 0 : Find.Storyteller.difficulty.friendlyFireChanceFactor;
                }
                else chance=thing.def.fillPercent*
                    (ProjectileAccess.Destination(flight.Projectile).ToIntVec3().AdjacentTo8Way(
                        Project(point,flight.StorageLayer,0).ToIntVec3()) ? 1 : 0.15f);
                chance*=VerbUtility.InterceptChanceFactorFromDistance(ProjectileAccess.Origin(flight.Projectile),
                    Project(point,flight.StorageLayer,0).ToIntVec3());
                if(!Rand.Chance(chance)) continue;
                PlaceImpact(flight,point);
                ProjectileAccess.Impact(flight.Projectile,thing,false);
                return true;
            }
            flight.LastProgress=(float)high;
        }
        return false;
    }

    private bool ShieldIntercept(LayerFlight flight,CompProjectileInterceptor shield,ShotPoint a,ShotPoint b,ShotPoint contact)
    {
        owner.TryDecode(shield.parent.Position,out var location);
        Vector3 last=Project(a,location.LayerId,0),next=Project(b,location.LayerId,0);
        Vector3 center=shield.parent.Position.ToVector3Shifted();
        float radius=shield.Props.radius;
        bool InsideXY(Vector3 p)=>(p.x-center.x)*(p.x-center.x)+(p.z-center.z)*(p.z-center.z)<=radius*radius;
        // A cap entry can be inside the 2D footprint at both ends. Give vanilla the corresponding
        // inward radial crossing so it still owns faction, charge, EMP and hitpoint decisions.
        if(InsideXY(last) && InsideXY(next))
        {
            Vector3 radial=Project(contact,location.LayerId,0)-center;
            if(radial.sqrMagnitude<0.0001f) radial=Vector3.right;
            last=center+radial.normalized*(radius+0.2f);
        }
        // Clip the test to the contact so native distance rejection cannot skip a fast swept hit.
        Vector3 contact2D=Project(contact,location.LayerId,0);
        if(!InsideXY(last)) next=Vector3.Lerp(contact2D,center,0.0001f);
        else next=contact2D+(contact2D-center).normalized*0.001f;
        flight.ImpactPoint=Vector(contact);flight.ImpactLayer=location.LayerId;
        return ShieldDecisionReceiptPatch.Check(flight,shield,last,next);
    }

    internal bool GeometricHitAllowed(LayerFlight flight,Thing thing)
    {
        if(!thing.Spawned || thing.Map!=owner.map || !owner.TryDecode(thing.Position,out var logical)) return false;
        ShotPoint p=flight.ActivePoint??flight.Point(flight.Progress);
        double bottom=owner.Elevation(logical.LayerId)*3,top=bottom+TopHeight(thing);
        if(p.Height<bottom-0.01 || p.Height>top+0.01) return false;
        // Vanilla usedTarget follows a Thing reference; do not hit a target that moved out of the endpoint.
        if(flight.ActivePoint==null)
        {
            CellRect rect=thing.OccupiedRect();
            return p.X>=rect.minX && p.X<=rect.maxX+1 && p.Z>=rect.minZ-logical.LayerId*owner.Layout!.Stride &&
                p.Z<=rect.maxZ+1-logical.LayerId*owner.Layout.Stride;
        }
        return true;
    }
}
