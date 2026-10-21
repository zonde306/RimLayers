using RimLayers.Combat;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Building_TurretGun combatTurret=null!;
    private static CompPowerBattery turretBattery=null!;
    private static float turretFuelBefore,turretEnergyBefore;
    private static bool turretFlightSeen,turretFlightPicture;
    private static void BeginTurret()
    {
        foreach(Pawn colonist in map.mapPawns.FreeColonistsSpawned.ToList())
        {colonist.jobs.StopAll();colonist.equipment.DestroyAllEquipment(DestroyMode.Vanish);}
        pawn.drafter.Drafted=false;pawn.jobs.StopAll();pawn.apparel.DestroyAll(DestroyMode.Vanish);
        pawn.Position=At(0,85,60);pawn.Notify_Teleported();pawn.SetFaction(Faction.OfAncientsHostile);
        frozenCombatPawn=pawn;shotTarget=pawn;
        for(int x=65;x<=95;x++)for(int z=57;z<=63;z++)
        {
            var cell=At(0,x,z);
            foreach(Thing thing in cell.GetThingList(map).ToList())
                if(thing is not Pawn){if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
            map.roofGrid.SetRoof(cell,null);map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=65&&c.x<=95&&c.z>=57&&c.z<=63);
        combatTurret=(Building_TurretGun)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"),ThingDefOf.Steel);
        GenSpawn.Spawn(combatTurret,At(2,64,60),map);combatTurret.SetFaction(Faction.OfPlayer);
        var battery=UtilityBuilding("Battery",2,61,62);turretBattery=battery.GetComp<CompPowerBattery>();turretBattery.AddEnergy(300);
        for(int x=61;x<=64;x++)UtilityBuilding("PowerConduit",2,x,62);
        FlushUtilityGraph();
        turretFuelBefore=combatTurret.refuelableComp.Fuel;turretEnergyBefore=turretBattery.StoredEnergy;
        foreach(var stair in map.listerThings.ThingsOfDef(RimLayersDefOf.RL_Stairs).OfType<RimLayers.Structure.Building_Stairs>()
            .Where(s=>owner.TryDecode(s.Position,out var c)&&c.LayerId==1).ToList())stair.Destroy(DestroyMode.Vanish);
        Check("real-core-turret-has-native-gun-and-barrel",combatTurret.AttackVerb is Verb_LaunchProjectile&&turretFuelBefore>0);
        Check("turret-can-geometrically-hit-without-a-walking-route",combatTurret.AttackVerb.CanHitTarget(pawn)&&
            !map.reachability.CanReach(combatTurret.Position,pawn,Verse.AI.PathEndMode.Touch,TraverseParms.For(TraverseMode.PassDoors)));
        var acquired=combatTurret.TryFindNewTarget();
        Check("native-turret-query-acquires-real-other-floor-pawn",acquired.Thing==pawn,$"target={acquired}");
        stage=200;ticks=0;
    }
    private static void TurretStep()
    {
        var flight=owner.Combat.Flights.FirstOrDefault(f=>f.Projectile.Launcher==combatTurret&&!f.Projectile.Destroyed);
        if(flight!=null)
        {
            turretFlightSeen=true;
            if(!turretFlightPicture)
            {
                turretFlightPicture=true;var p=flight.DrawPoint;
                LayerView.Switch(owner,Math.Clamp((int)Math.Floor(p.Height/3),0,2));Find.Selector.ClearSelection();
                Find.CameraDriver.SetRootPosAndSize(At(owner.ViewedLayer,(int)p.X,(int)p.Z).ToVector3Shifted(),12);
                pendingScreenshot=Path.Combine(directory,"combat-turret-flight.png");screenshotHold=3;
            }
        }
        if(blastDamageCalls.GetValueOrDefault(pawn.ThingID)>0)
        {
            Check("native-turret-map-ticks-launch-real-physical-flight",turretFlightSeen);
            Check("real-turret-bullet-reaches-native-pawn-damage",InjurySum(pawn)>0,$"damageCalls={blastDamageCalls.GetValueOrDefault(pawn.ThingID)},injuries={InjurySum(pawn)}");
            Check("turret-is-powered-by-real-native-battery",combatTurret.GetComp<CompPowerTrader>().PowerOn&&turretBattery.StoredEnergy<turretEnergyBefore);
            Check("native-turret-consumes-real-barrel-fuel",combatTurret.refuelableComp.Fuel<turretFuelBefore,$"fuel={turretFuelBefore}->{combatTurret.refuelableComp.Fuel}");
            combatTurret.GetComp<CompFlickable>().SwitchIsOn=false;
            LayerView.Switch(owner,0);Find.Selector.ClearSelection();
            Thing visible=pawn.Corpse is {} corpse?corpse:pawn;
            if(visible.Spawned)Find.Selector.Select(visible);
            Find.CameraDriver.SetRootPosAndSize(At(0,85,60).ToVector3Shifted(),12);
            pendingScreenshot=Path.Combine(directory,"combat-turret-hit.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(ticks>1500)
        {Check("native-turret-fires-and-hits",false,$"target={combatTurret.CurrentTarget},active={combatTurret.Active},power={combatTurret.GetComp<CompPowerTrader>().PowerOn},flights={owner.Combat.Flights.Count},commits={owner.Combat.CommittedImpacts}");Finish();}
    }
}
