using System.IO;
using System.Text.Json;
using RimLayers.Combat;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private sealed record SettingsDefBaseline(string Def,bool Terrain,bool Own,int Stuff,float Work,float Power,Dictionary<string,int> Costs);
    private static SettingsDefBaseline[] SettingsDefsSnapshot()=>DefDatabase<ThingDef>.AllDefsListForReading.Cast<BuildableDef>()
        .Concat(DefDatabase<TerrainDef>.AllDefsListForReading)
        .Where(d=>d.modContentPack==RimLayersMod.SettingsContent||d.defName is "Wall" or "Door" or "Bed" or "WoodPlankFloor" or "SolarGenerator")
        .Select(d=>new SettingsDefBaseline(d.defName,d is TerrainDef,d.modContentPack==RimLayersMod.SettingsContent,d.costStuffCount,
            d.statBases?.FirstOrDefault(s=>s.stat==StatDefOf.WorkToBuild)?.value??0,
            d is ThingDef t&&typeof(Building_Elevator).IsAssignableFrom(t.thingClass)?t.GetCompProperties<CompProperties_Power>()?.PowerConsumption??-1:-1,
            d.costList?.ToDictionary(c=>c.thingDef.defName,c=>c.count)??new())).ToArray();
    private static string SettingsValues()=>JsonSerializer.Serialize(typeof(RimLayersSettings).GetFields().ToDictionary(f=>f.Name,f=>f.GetValue(RimLayersMod.Settings)));
    private static void BeginSettingsBehavior()
    {
        var s=RimLayersMod.Settings;
        Check("all-default-settings-match-fresh-instance",SettingsValues()==JsonSerializer.Serialize(typeof(RimLayersSettings).GetFields().ToDictionary(f=>f.Name,f=>f.GetValue(new RimLayersSettings()))));
        File.WriteAllText(Path.Combine(directory,"settings-def-baseline.json"),JsonSerializer.Serialize(SettingsDefsSnapshot()));
        foreach(var f in typeof(RimLayersSettings).GetFields())if(f.FieldType==typeof(bool))f.SetValue(s,false);
        s.UpperLimit=7;s.UndergroundLimit=6;s.GlobalLimit=77;
        s.StairTime=.5f;s.PoweredLiftTime=2;s.EmergencyLiftTime=3;
        s.MaterialCost=2;s.WorkCost=3;s.LiftPower=.5f;s.Minerals=2.5f;s.HeightRange=0;
        s.RecoveryMode=RecoveryPreference.ResetFirst;s.RecoveryExcludedDefs=new(){"Steel","Human"};
        Check("direct-height-zero-restores-native-range",ShotProfiles.Multiplier(9,-9,false)==1&&ShotProfiles.Multiplier(-9,9,false)==1);
        s.HeightRange=2;
        Check("direct-height-strength-shared-and-curved-unchanged",Math.Abs(ShotProfiles.Multiplier(9,-9,false)-1.4f)<.00001f&&Math.Abs(ShotProfiles.Multiplier(-9,9,false)-.6f)<.00001f&&ShotProfiles.Multiplier(9,-9,true)==1);
        Check("definition-changes-await-restart",SettingsDefinitions.RestartNeeded&&SettingsDefinitions.Applied==(1f,1f,1f));
        LoadedModManager.GetMod<RimLayersMod>().WriteSettings();
        File.WriteAllText(Path.Combine(directory,"settings-values.json"),SettingsValues());
        GameDataSaveLoader.SaveGame("RimLayers_SettingsBehavior");Finish();
    }
    private static void RestoreSettingsBehavior()
    {
        Check("all-global-settings-survive-process-restart",SettingsValues()==File.ReadAllText(Path.Combine(directory,"settings-values.json")));
        var old=JsonSerializer.Deserialize<SettingsDefBaseline[]>(File.ReadAllText(Path.Combine(directory,"settings-def-baseline.json")))!;
        var current=SettingsDefsSnapshot();
        foreach(var b in old)
        {
            var a=current.Single(d=>d.Def==b.Def&&d.Terrain==b.Terrain);
            bool costs=a.Costs.Count==b.Costs.Count&&b.Costs.All(c=>a.Costs[c.Key]==(b.Own?SettingsMath.MaterialCount(c.Value,2):c.Value));
            Check("restart-definition-"+b.Def,costs&&a.Stuff==(b.Own?SettingsMath.MaterialCount(b.Stuff,2):b.Stuff)&&a.Work==b.Work*(b.Own?3:1)&&a.Power==(b.Own&&b.Power>=0?b.Power*.5f:b.Power));
        }
        var before=JsonSerializer.Serialize(current);SettingsDefinitions.Apply();
        Check("definition-multipliers-apply-only-once",before==JsonSerializer.Serialize(SettingsDefsSnapshot())&&!SettingsDefinitions.RestartNeeded);
        RimLayersMod.Settings.Reset();SettingsMenu.SettingsChanged();
        Check("restore-defaults-resets-every-public-setting",SettingsValues()==JsonSerializer.Serialize(typeof(RimLayersSettings).GetFields().ToDictionary(f=>f.Name,f=>f.GetValue(new RimLayersSettings()))));
        Check("reset-does-not-rescale-live-definitions",before==JsonSerializer.Serialize(SettingsDefsSnapshot())&&SettingsDefinitions.RestartNeeded);
        LoadedModManager.GetMod<RimLayersMod>().WriteSettings();Finish();
    }
}
