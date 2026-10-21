using System.Text.Json;
using HarmonyLib;
using RimLayers.Structure;
using RimLayers.Presentation;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static int airTopBefore;
    private static int Rot(IntVec3 c)=>map.gasGrid.DensityAt(c,GasType.RotStink);
    private static int ColumnRot(int x,int z)=>Enumerable.Range(0,3).Sum(layer=>Rot(At(layer,x,z)));
    private static void BeginAirTests()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(0,48,48);pawn.Notify_Teleported();
        for(int layer=0;layer<3;layer++)
        {
            var cell=At(layer,70,70);
            foreach(Thing thing in cell.GetThingList(map).ToList())
                if(thing is not Pawn) { if(thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
            map.roofGrid.SetRoof(cell,null);
            map.terrainGrid.SetTerrain(cell,layer==0 ? TerrainDefOf.Concrete : RimLayersDefOf.RL_OpenAir);
            map.gasGrid.SetDirect(cell,0u);
            foreach(var offset in GenAdj.CardinalDirections)
            {
                IntVec3 wallCell=At(layer,70+offset.x,70+offset.z);
                foreach(Thing thing in wallCell.GetThingList(map).ToList())
                    if(thing is not Pawn) { if(thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn(); }
                if(layer>0)
                {
                    map.roofGrid.SetRoof(At(layer-1,70+offset.x,70+offset.z),RoofDefOf.RoofConstructed);
                    map.terrainGrid.SetTerrain(wallCell,RimLayersDefOf.RL_StructuralFloor);
                }
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),wallCell,map);
            }
        }
        map.roofCollapseBuffer.CellsMarkedToCollapse.RemoveAll(c=>c.x>=69 && c.x<=71 && c.z>=69 && c.z<=71);
        Check("upper-open-air-is-volume-not-walkable-floor",!owner.Structure.HasSurface(At(1,70,70)) && map.gasGrid.GasCanMoveTo(At(1,70,70)));
        Check("open-shaft-connects-adjacent-air-volumes",owner.Air.IsOpen(At(0,70,70),At(1,70,70)) && owner.Air.IsOpen(At(1,70,70),At(2,70,70)));
        map.gasGrid.SetDirect(At(0,70,70),30,100,255,99);
        Check("native-dlc-gas-masks-retained",map.gasGrid.DensityAt(At(0,70,70),GasType.ToxGas)==(ModsConfig.BiotechActive?100:0) &&
            map.gasGrid.DensityAt(At(0,70,70),GasType.DeadlifeDust)==(ModsConfig.AnomalyActive?99:0));
        owner.Air.Exchange();owner.Air.Exchange();
        Check("open-shaft-transfers-through-middle-layer",Rot(At(1,70,70))>0 && Rot(At(2,70,70))>0);
        Check("vertical-exchange-conserves-concentration",ColumnRot(70,70)==255);
        Check("blind-smoke-keeps-native-no-diffusion-policy",map.gasGrid.DensityAt(At(0,70,70),GasType.BlindSmoke)==30 &&
            map.gasGrid.DensityAt(At(1,70,70),GasType.BlindSmoke)==0);
        map.gasGrid.SetDirect(At(0,58,58),255u<<16);map.gasGrid.SetDirect(At(1,58,58),0u);
        owner.Air.Exchange();
        Check("solid-slab-and-roof-stop-vertical-gas",!owner.Air.IsOpen(At(0,58,58),At(1,58,58)) && Rot(At(1,58,58))==0);
        var door=(Building_Door)ThingMaker.MakeThing(ThingDefOf.Door,ThingDefOf.Steel);
        GenSpawn.Spawn(door,At(1,52,52),map);door.SetFaction(Faction.OfPlayer);
        map.gasGrid.SetDirect(At(0,52,52),200u<<16);map.gasGrid.SetDirect(At(1,52,52),0u);
        owner.Air.Exchange();
        Check("closed-landing-door-seals-stair-air-channel",!door.Open && !owner.Air.IsOpen(At(0,52,52),At(1,52,52)) && Rot(At(1,52,52))==0);
        AccessTools.Method(typeof(Building_Door),"DoorOpen").Invoke(door,new object[]{100});
        var stair=At(0,52,52).GetThingList(map).OfType<Building_Stairs>().Single();stair.SetForbidden(true);
        owner.Air.Exchange();
        Check("explicit-stair-air-permission-is-not-pawn-forbiddance",door.Open && Rot(At(1,52,52))>0 && ColumnRot(52,52)==200);
        AccessTools.Method(typeof(Building_Door),"DoorTryClose").Invoke(door,null);
        int upperBefore=Rot(At(1,52,52));owner.Air.Exchange();
        Check("closing-door-stops-next-transfer",!door.Open && Rot(At(1,52,52))==upperBefore);stair.SetForbidden(false);
        IntVec3 gap=new(60,0,owner.Layout!.Height);
        Check("storage-gap-does-not-conduct-gas",!map.gasGrid.GasCanMoveTo(gap));
        map.gasGrid.SetDirect(gap,uint.MaxValue);Check("packed-write-cannot-fill-isolation",map.gasGrid.GetDirect(gap)==0);
        map.gasGrid.SetDirect(gap,255,255,255,255);Check("channel-write-cannot-fill-isolation",map.gasGrid.GetDirect(gap)==0);
        map.gasGrid.SetDirect(At(0,60,119),200u<<16);
        AccessTools.Method(typeof(GasGrid),"TryDiffuseGases").Invoke(map.gasGrid,new object[]{At(0,60,119)});
        Check("native-horizontal-diffusion-stays-out-of-gap",map.gasGrid.GetDirect(gap)==0);
        // A known mid-volume source lets native scheduled exchange be observed despite
        // native dissipation. The enclosing walls prevent unrelated horizontal spread.
        map.gasGrid.SetDirect(At(0,70,70),30u);map.gasGrid.SetDirect(At(1,70,70),255u<<16);map.gasGrid.SetDirect(At(2,70,70),0u);
        GameDataSaveLoader.SaveGame("RimLayers_V1Air");
        File.WriteAllText(Path.Combine(directory,"air-baseline.json"),JsonSerializer.Serialize(new {
            shaft=Enumerable.Range(0,3).Select(layer=>map.gasGrid.GetDirect(At(layer,70,70))).ToArray()
        }));
        Check("saved-native-gas-grid-without-second-density-owner",true);
        airTopBefore=0;stage=70;ticks=0;
    }
    private static void RestoreAir()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"air-baseline.json")));
        uint[] stored=baseline.RootElement.GetProperty("shaft").EnumerateArray().Select(v=>v.GetUInt32()).ToArray();
        Check("restart-preserves-native-gas-packing-before-tick",stored.SequenceEqual(Enumerable.Range(0,3).Select(layer=>map.gasGrid.GetDirect(At(layer,70,70)))));
        Check("restart-rebuilds-active-column-index",owner.Air.ActiveColumns>0);
        int total=ColumnRot(70,70);owner.Air.Exchange();owner.Air.Exchange();
        Check("restart-exchange-preserves-mass-and-connectivity",ColumnRot(70,70)==total && Rot(At(0,70,70))>0 && Rot(At(2,70,70))>0);
        airTopBefore=-1;stage=70;ticks=0;
    }
    private static void AirStep()
    {
        if(ticks>50)
        {
            Check("native-map-ticks-run-vertical-air-exchange",Rot(At(2,70,70))>airTopBefore,
                $"top={Rot(At(2,70,70))}, before={airTopBefore}");
            LayerView.Switch(owner,1);Find.Selector.ClearSelection();
            Find.CameraDriver.SetRootPosAndSize(At(1,70,70).ToVector3Shifted(),6);
            pendingScreenshot=Path.Combine(directory,mode+"-air.png");screenshotHold=3;
            stage=11;ticks=0;
        }
    }
}
