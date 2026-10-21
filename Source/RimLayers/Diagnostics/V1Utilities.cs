using System.Text.Json;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly List<Building> risers=new();
    private static Building powerGenerator=null!,powerStove=null!,powerBatteryBuilding=null!;
    private static CompPowerBattery powerBattery=null!;
    private static float energyBefore,energyRate;
    private static Building UtilityBuilding(string def,int layer,int x,int z)
    {
        var thing=(Building)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(def));
        GenSpawn.Spawn(thing,At(layer,x,z),map);thing.SetFaction(Faction.OfPlayer);return thing;
    }
    private static void BeginUtilities()
    {
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;
        pawn.Position=At(0,48,48);pawn.Notify_Teleported();
        powerGenerator=UtilityBuilding("WoodFiredGenerator",0,54,56);
        powerGenerator.GetComp<CompRefuelable>().Refuel(50);
        powerBatteryBuilding=UtilityBuilding("Battery",1,58,56);
        powerBattery=powerBatteryBuilding.GetComp<CompPowerBattery>();powerBattery.AddEnergy(300);
        powerStove=UtilityBuilding("ElectricStove",2,58,56);
        risers.Clear();
        for(int layer=0;layer<3;layer++)
        {
            risers.Add(UtilityBuilding("RL_PowerRiser",layer,54,54));
            risers.Add(UtilityBuilding("RL_PowerRiser",layer,62,54));
            for(int x=55;x<62;x++) UtilityBuilding("PowerConduit",layer,x,54);
        }
        UtilityBuilding("PowerConduit",0,54,55);UtilityBuilding("PowerConduit",1,58,55);
        Check("riser-inherits-native-power-comp-and-switch",risers.All(b=>b.GetComp<CompPower>()!=null && b.GetComp<CompFlickable>()!=null));
        Check("riser-keeps-normal-structural-placement-rule",owner.Structure.Validate(RimLayersDefOf.RL_PowerRiser,At(1,54,54),Rot4.North).Accepted);
        CheckFacilities();
        stage=60;ticks=0;
    }
    private static bool SameUtilityNet()=>powerGenerator.PowerComp.PowerNet!=null &&
        powerGenerator.PowerComp.PowerNet==powerStove.PowerComp.PowerNet && powerBattery.PowerNet==powerStove.PowerComp.PowerNet;
    private static bool UniqueUtilityParts()
    {
        var net=powerStove.PowerComp.PowerNet;
        return net!=null && net.transmitters.Distinct().Count()==net.transmitters.Count &&
            net.powerComps.Distinct().Count()==net.powerComps.Count && net.batteryComps.Count(b=>b==powerBattery)==1 &&
            map.powerNetManager.AllNetsListForReading.SelectMany(n=>n.batteryComps).Count(b=>b==powerBattery)==1;
    }
    private static void FlushUtilityGraph()=>map.powerNetManager.UpdatePowerNetsAndConnections_First();
    private static void CheckFacilities()
    {
        var bench=UtilityBuilding("CraftingSpot",2,58,60);
        var affected=bench.GetComp<CompAffectedByFacilities>();
        var below=UtilityBuilding("ToolCabinet",1,54,60);
        var local=new[]{UtilityBuilding("ToolCabinet",2,54,60),UtilityBuilding("ToolCabinet",2,58,63),UtilityBuilding("ToolCabinet",2,61,59)};
        affected.Notify_ThingChanged();
        Check("facility-preview-rejects-different-logical-floor",!CompAffectedByFacilities.CanPotentiallyLinkTo_Static(
            below,bench.def,bench.Position,bench.Rotation,map));
        Check("native-facility-cap-remains-two",affected.LinkedFacilitiesListForReading.Count==2 &&
            !affected.LinkedFacilitiesListForReading.Contains(below),string.Join(",",affected.LinkedFacilitiesListForReading.Select(t=>t.ThingID)));
        Check("native-facility-offset-is-not-multiplied-by-layers",Math.Abs(affected.GetStatOffset(StatDefOf.WorkTableWorkSpeedFactor)-0.12f)<0.0001);
        foreach(var cabinet in local) cabinet.Destroy(DestroyMode.Vanish);
        affected.Notify_ThingChanged();
        Check("other-floor-cabinet-does-not-replace-destroyed-local-facilities",affected.GetStatOffset(StatDefOf.WorkTableWorkSpeedFactor)==0);
    }
    private static void UtilitiesStep()
    {
        if(stage==60 && ticks>250)
        {
            Check("two-vertical-links-form-one-native-three-floor-net",SameUtilityNet());
            Check("native-generator-powers-top-floor-stove",powerStove.GetComp<CompPowerTrader>().PowerOn && powerGenerator.GetComp<CompPowerPlant>().PowerOutput>0,
                $"source={powerGenerator.GetComp<CompPowerPlant>().PowerOutput}, stoveOn={powerStove.GetComp<CompPowerTrader>().PowerOn}");
            Check("loop-does-not-duplicate-energy-components",UniqueUtilityParts());
            float stored=powerBattery.StoredEnergy;
            risers[1].GetComp<CompFlickable>().SwitchIsOn=false;FlushUtilityGraph();
            Check("one-open-riser-keeps-alternate-route",SameUtilityNet());
            risers[0].GetComp<CompFlickable>().SwitchIsOn=false;FlushUtilityGraph();
            Check("both-ground-risers-off-split-old-network",powerGenerator.PowerComp.PowerNet!=powerStove.PowerComp.PowerNet);
            risers[0].GetComp<CompFlickable>().SwitchIsOn=true;FlushUtilityGraph();
            Check("native-switch-rejoins-one-network",SameUtilityNet() && UniqueUtilityParts());
            risers[2].Destroy(DestroyMode.Vanish);FlushUtilityGraph();
            Check("despawn-invalidates-remote-endpoint-network",powerGenerator.PowerComp.PowerNet!=powerStove.PowerComp.PowerNet);
            risers[2]=UtilityBuilding("RL_PowerRiser",1,54,54);FlushUtilityGraph();
            Check("replacement-terminal-rejoins-full-network",SameUtilityNet() && UniqueUtilityParts());
            map.roofGrid.SetRoof(At(0,54,54),null);
            Check("lost-support-immediately-disables-upper-transmitters",!risers[2].TransmitsPowerNow && !risers[4].TransmitsPowerNow);
            FlushUtilityGraph();
            Check("lost-support-breaks-vertical-power-path",powerGenerator.PowerComp.PowerNet!=powerStove.PowerComp.PowerNet);
            map.roofGrid.SetRoof(At(0,54,54),RoofDefOf.RoofConstructed);FlushUtilityGraph();
            Check("restored-support-rejoins-network",SameUtilityNet());
            Check("network-rebuild-does-not-create-or-consume-energy",Math.Abs(powerBattery.StoredEnergy-stored)<0.0001);
            powerGenerator.GetComp<CompFlickable>().SwitchIsOn=false;
            stage=61;ticks=0;
        }
        else if(stage==61 && ticks>30 && powerStove.GetComp<CompPowerTrader>().PowerOn && powerGenerator.GetComp<CompPowerPlant>().PowerOutput==0)
        {
            energyBefore=powerBattery.StoredEnergy;energyRate=powerStove.PowerComp.PowerNet.CurrentEnergyGainRate();
            Check("battery-supplies-real-load-with-generator-off",powerGenerator.GetComp<CompPowerPlant>().PowerOutput==0 &&
                powerStove.GetComp<CompPowerTrader>().PowerOn && energyRate<0,energyRate.ToString());
            stage=62;ticks=0;
        }
        else if(stage==62 && ticks==100)
        {
            float expected=-energyRate*100+5*CompPower.WattsToWattDaysPerTick*100;
            float loss=energyBefore-powerBattery.StoredEnergy;
            Check("battery-loss-equals-one-native-load-and-self-discharge",Math.Abs(loss-expected)<0.003,
                $"loss={loss}, expected={expected}, interval=100 ticks");
            map.roofGrid.SetRoof(At(0,54,56),null);FlushUtilityGraph();
            Check("unrelated-roof-change-keeps-ground-generator-off",!powerGenerator.GetComp<CompFlickable>().SwitchIsOn);
            map.roofGrid.SetRoof(At(0,54,56),RoofDefOf.RoofConstructed);FlushUtilityGraph();
            var upperSource=UtilityBuilding("WoodFiredGenerator",2,54,58);
            upperSource.GetComp<CompFlickable>().SwitchIsOn=false;FlushUtilityGraph();
            map.roofGrid.SetRoof(At(1,54,58),null);FlushUtilityGraph();
            Check("support-loss-does-not-enable-off-upper-generator",!upperSource.GetComp<CompFlickable>().SwitchIsOn && !upperSource.TransmitsPowerNow);
            map.roofGrid.SetRoof(At(1,54,58),RoofDefOf.RoofConstructed);FlushUtilityGraph();
            Check("support-recovery-preserves-user-power-switch",!upperSource.GetComp<CompFlickable>().SwitchIsOn && upperSource.TransmitsPowerNow);
            GameDataSaveLoader.SaveGame("RimLayers_V1Utilities");
            File.WriteAllText(Path.Combine(directory,"utilities-baseline.json"),JsonSerializer.Serialize(new {
                source=powerGenerator.ThingID,stove=powerStove.ThingID,battery=powerBatteryBuilding.ThingID,
                ports=risers.Select(p=>p.ThingID).ToArray(),energy=powerBattery.StoredEnergy,stoveOn=powerStove.GetComp<CompPowerTrader>().PowerOn
            }));
            Check("saved-network-facts-not-network-instances",true);UtilitiesScreenshot();stage=11;ticks=0;
        }
        else if(stage==64 && ticks>60)
        {
            Check("reloaded-network-remains-unique",SameUtilityNet() && UniqueUtilityParts());
            Check("reloaded-battery-supplies-top-floor-load",powerBattery.StoredEnergy<energyBefore && powerStove.GetComp<CompPowerTrader>().PowerOn);
            UtilitiesScreenshot();stage=11;ticks=0;
        }
        else if(ticks>1200) { Check("utilities-scenario-completes",false);Finish(); }
    }
    private static void RestoreUtilities()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"utilities-baseline.json")));
        var b=baseline.RootElement;
        Building FindBuilding(string id)=>(Building)map.listerThings.AllThings.Single(t=>t.ThingID==id);
        powerGenerator=FindBuilding(b.GetProperty("source").GetString()!);powerStove=FindBuilding(b.GetProperty("stove").GetString()!);
        powerBatteryBuilding=FindBuilding(b.GetProperty("battery").GetString()!);powerBattery=powerBatteryBuilding.GetComp<CompPowerBattery>();
        risers.Clear();foreach(var id in b.GetProperty("ports").EnumerateArray()) risers.Add(FindBuilding(id.GetString()!));
        energyBefore=b.GetProperty("energy").GetSingle();
        Check("restart-preserves-battery-energy-before-first-tick",Math.Abs(powerBattery.StoredEnergy-energyBefore)<0.0001);
        Check("restart-preserves-terminal-native-switch-state",!risers[1].GetComp<CompFlickable>().SwitchIsOn && risers[0].GetComp<CompFlickable>().SwitchIsOn);
        Check("restart-keeps-user-generators-off",!powerGenerator.GetComp<CompFlickable>().SwitchIsOn &&
            !At(2,54,58).GetEdifice(map).GetComp<CompFlickable>().SwitchIsOn);
        FlushUtilityGraph();
        Check("restart-rebuilds-native-network-from-terminal-facts",SameUtilityNet() && UniqueUtilityParts());
        Check("restart-does-not-reset-saved-powered-consumer",powerStove.GetComp<CompPowerTrader>().PowerOn==b.GetProperty("stoveOn").GetBoolean());
        stage=64;ticks=0;
    }
    private static void UtilitiesScreenshot()
    {
        LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.Selector.Select(risers[4]);
        Find.CameraDriver.SetRootPosAndSize(At(2,58,56).ToVector3Shifted(),12);
        pendingScreenshot=Path.Combine(directory,mode+"-utilities.png");screenshotHold=3;
    }
}
