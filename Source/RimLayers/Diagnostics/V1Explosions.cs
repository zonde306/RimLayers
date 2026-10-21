using System.Text.Json;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Explosion testBlast=null!;
    private static Thing nearBlastTarget=null!,upperBlastTarget=null!,coveredBlastTarget=null!,farBlastTarget=null!;
    private static int nearBlastSavedHP,upperBlastHP,coveredBlastHP,farBlastHP;
    private static readonly Dictionary<string,int> blastDamageCalls=new();
    private static void BeginBlastTests()
    {
        PrepareBlastArena();
        // Explicit shapes retain their semantics but cannot smuggle a storage-gap cell into effects.
        var gapProbe=(Explosion)GenSpawn.Spawn(ThingDefOf.Explosion,At(1,84,0),map);
        gapProbe.radius=1;gapProbe.damType=DamageDefOf.Bomb;gapProbe.damAmount=1;gapProbe.propagationSpeed=1;
        gapProbe.doVisualEffects=gapProbe.doSoundEffects=false;gapProbe.applyDamageToExplosionCellsNeighbors=true;
        gapProbe.overrideCells=new(){At(1,84,0),At(1,84,0),new IntVec3(84,0,owner.Layout!.Stride-1)};
        gapProbe.StartExplosion(null,null);
        Check("override-shape-deduplicates-and-rejects-gap",BlastAccess.Cells(gapProbe).All(c=>owner.TryDecode(c,out _)) &&
            BlastAccess.Cells(gapProbe).Distinct().Count()==BlastAccess.Cells(gapProbe).Count);
        gapProbe.Destroy();
        GenExplosion.DoExplosion(At(0,84,84),map,5,DamageDefOf.Bomb,pawn,40,1,damageFalloff:true,propagationSpeed:0.2f,
            postExplosionGasType:GasType.BlindSmoke,postExplosionGasAmount:96,postExplosionGasRadiusOverride:4);
        testBlast=owner.Combat.Blasts.Select(b=>b.Explosion).Single(e=>!e.Destroyed);
        var cells=BlastAccess.Cells(testBlast);
        Check("native-explosion-owns-cross-layer-wave",cells.Contains(upperBlastTarget.Position) && cells.Contains(farBlastTarget.Position));
        Check("slab-blocks-blast-above-source",!cells.Contains(coveredBlastTarget.Position));
        Check("blast-radius-uses-physical-height",!cells.Any(c=>owner.TryDecode(c,out var p)&&p.LayerId==2));
        Check("blast-armor-falloff-uses-3d-distance",Math.Abs(testBlast.GetArmorPenetrationAt(upperBlastTarget.Position)-
            (1-0.8*Math.Sqrt(13)/5))<0.0001);
        stage=40;ticks=0;
    }
    private static void PrepareBlastArena()
    {
        pawn.Position=At(0,40,40);pawn.Notify_Teleported();
        for(int layer=0;layer<3;layer++)
            for(int x=77;x<=92;x++) for(int z=77;z<=92;z++)
            {
                var cell=At(layer,x,z);
                foreach(Thing thing in cell.GetThingList(map).ToList())
                    if(thing is not Pawn) { if(thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
                map.roofGrid.SetRoof(cell,null);
                map.terrainGrid.SetTerrain(cell,layer==0 ? TerrainDefOf.Concrete : RimLayersDefOf.RL_OpenAir);
                map.fogGrid.Unfog(cell);
            }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=77 && c.x<=92 && c.z>=77 && c.z<=92);
        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Column,ThingDefOf.Steel),At(0,86,86),map);
        foreach(int x in new[]{84,86})
        {
            map.roofGrid.SetRoof(At(0,x,84),RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(At(1,x,84),RimLayersDefOf.RL_StructuralFloor);
        }
        nearBlastTarget=BlastWall(0,85,83);farBlastTarget=BlastWall(0,87,84);
        upperBlastTarget=BlastWall(1,86,84);coveredBlastTarget=BlastWall(1,84,84);
        upperBlastHP=upperBlastTarget.HitPoints;coveredBlastHP=coveredBlastTarget.HitPoints;farBlastHP=farBlastTarget.HitPoints;
    }
    private static Thing BlastWall(int layer,int x,int z)
    {
        var wall=ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel);
        GenSpawn.Spawn(wall,At(layer,x,z),map);wall.SetFaction(Faction.OfPlayer);return wall;
    }
    private static void RestoreBlastTests()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"combat-explosion-baseline.json")));
        var b=baseline.RootElement;
        Thing FindThing(string key)=>map.listerThings.AllThings.Single(t=>t.ThingID==b.GetProperty(key).GetString());
        testBlast=(Explosion)FindThing("explosion");nearBlastTarget=FindThing("near");upperBlastTarget=FindThing("upper");
        coveredBlastTarget=FindThing("covered");farBlastTarget=FindThing("far");
        nearBlastSavedHP=b.GetProperty("nearHP").GetInt32();upperBlastHP=b.GetProperty("upperHP").GetInt32();
        coveredBlastHP=b.GetProperty("coveredHP").GetInt32();farBlastHP=b.GetProperty("farHP").GetInt32();
        Check("restart-restores-physical-blast-center",CombatService.Blast(testBlast) is {} state && Math.Abs(state.Origin.y-1)<0.001);
        Check("restart-restores-native-receipts",BlastAccess.Damaged(testBlast).Contains(nearBlastTarget));
        Check("restart-restores-unscribed-native-gas-parameters",testBlast.postExplosionGasAmount==96 && testBlast.postExplosionGasRadiusOverride==4);
        Check("restart-resumes-pending-wave",BlastAccess.Cells(testBlast).Contains(upperBlastTarget.Position) && upperBlastTarget.HitPoints==upperBlastHP);
        stage=41;ticks=0;
    }
    private static void BlastStep()
    {
        if(stage==40 && BlastAccess.Damaged(testBlast).Contains(nearBlastTarget) && upperBlastTarget.HitPoints==upperBlastHP)
        {
            nearBlastSavedHP=nearBlastTarget.HitPoints;
            Check("native-wave-damages-near-before-upper",nearBlastSavedHP<nearBlastTarget.MaxHitPoints && !testBlast.Destroyed);
            IntVec3 moved=At(0,80,84);
            Check("new-cell-still-in-pending-wave",BlastAccess.Cells(testBlast).Contains(moved));
            nearBlastTarget.Position=moved; // The same object must not be damaged again at a later cell.
            GameDataSaveLoader.SaveGame("RimLayers_V1Explosion");
            File.WriteAllText(Path.Combine(directory,"combat-explosion-baseline.json"),JsonSerializer.Serialize(new {
                explosion=testBlast.ThingID,near=nearBlastTarget.ThingID,upper=upperBlastTarget.ThingID,
                covered=coveredBlastTarget.ThingID,far=farBlastTarget.ThingID,nearHP=nearBlastSavedHP,
                upperHP=upperBlastHP,coveredHP=coveredBlastHP,farHP=farBlastHP
            }));
            Check("saved-native-wave-after-first-receipt",true);stage=41;ticks=0;
        }
        else if(stage==41 && testBlast.Destroyed)
        {
            Check("native-wave-damages-exposed-upper-target",upperBlastTarget.HitPoints<upperBlastHP);
            Check("native-wave-damages-logically-near-ground",farBlastTarget.HitPoints<farBlastHP);
            Check("blocked-upper-target-remains-unhurt",coveredBlastTarget.HitPoints==coveredBlastHP);
            Check("moved-object-does-not-get-second-blast-hit",nearBlastTarget.HitPoints==nearBlastSavedHP,
                $"saved={nearBlastSavedHP}, now={nearBlastTarget.HitPoints}");
            Check("one-native-damage-call-for-each-new-target",blastDamageCalls.GetValueOrDefault(upperBlastTarget.ThingID)==1 &&
                blastDamageCalls.GetValueOrDefault(farBlastTarget.ThingID)==1);
            stage=11;ticks=0;
        }
        else if(ticks>500) { Check("native-blast-finishes",false);Finish(); }
    }
}
