using HarmonyLib;
using RimLayers.Structure;
using RimWorld;
using Verse;

namespace RimLayers;

internal static class SettingsDefinitions
{
    private static bool applied;
    internal static (float Material,float Work,float Power) Applied { get; private set; } = (1,1,1);
    internal static bool RestartNeeded => Applied != (RimLayersMod.Settings.MaterialCost,RimLayersMod.Settings.WorkCost,RimLayersMod.Settings.LiftPower);
    internal static void Apply()
    {
        if(applied)return;
        applied=true;
        var settings=RimLayersMod.Settings;
        Applied=(settings.MaterialCost,settings.WorkCost,settings.LiftPower);
        var definitions=DefDatabase<ThingDef>.AllDefsListForReading.Cast<BuildableDef>().Concat(DefDatabase<TerrainDef>.AllDefsListForReading)
            .Where(d=>d.modContentPack==RimLayersMod.SettingsContent).ToArray();
        foreach(var def in definitions)
        {
            if(def is ThingDef connector&&ConnectorDefs.IsAdjacent(connector)&&connector.GetModExtension<RimLayers.Navigation.ConnectorTransmission>() is {} transmission)
                transmission.liquid=true;
            // Clone inherited lists before editing: foreign definitions retain their own values.
            if(def.costList!=null)def.costList=def.costList.Select(c=>new ThingDefCountClass(c.thingDef,SettingsMath.MaterialCount(c.count,settings.MaterialCost))).ToList();
            def.costStuffCount=SettingsMath.MaterialCount(def.costStuffCount,settings.MaterialCost);
            if(def.statBases!=null)def.statBases=def.statBases.Select(s=>new StatModifier{stat=s.stat,value=s.value*(s.stat==StatDefOf.WorkToBuild?settings.WorkCost:1)}).ToList();
            if(def is ThingDef thing && typeof(Building_Elevator).IsAssignableFrom(thing.thingClass) && thing.comps!=null)
            {
                thing.comps=thing.comps.ToList();
                for(int i=0;i<thing.comps.Count;i++)if(thing.comps[i] is CompProperties_Power power)
                {
                    var copy=(CompProperties_Power)AccessTools.Method(typeof(object),"MemberwiseClone").Invoke(power,null)!;
                    var field=AccessTools.FieldRefAccess<CompProperties_Power,float>("basePowerConsumption");
                    field(copy)*=settings.LiftPower;
                    thing.comps[i]=copy;
                }
            }
        }
    }
}
