using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

public static class SolarShield
{
    public static bool Protected(PowerNet net)
    {
        var owner=LayerMapComponent.For(net.Map);
        return owner?.Environment.Enabled==true&&LayerEnvironment.Settings.SolarShield&&
            net.transmitters.Concat(net.connectors).Concat(net.powerComps).Concat(net.batteryComps).Distinct()
                .All(c=>c.parent.OccupiedRect().Cells.All(owner.Environment.RockShielded));
    }
    // Only this electrical net query ignores SolarFlare. EMP, breakdowns, communication and launch checks remain native.
    internal static bool Disabled(GameConditionManager manager,Map map,PowerNet net)
    {
        if(!manager.ElectricityDisabled(map))return false;
        if(!Protected(net))return true;
        for(var current=manager;current!=null;current=current.Parent)
            foreach(var condition in current.ActiveConditions)
                if(condition.ElectricityDisabled&&condition.CanApplyOnMap(map)&&!condition.HiddenByOtherCondition(map)&&
                    condition.def.defName!="SolarFlare")return true;
        return false;
    }
}
[HarmonyPatch(typeof(PowerNet),nameof(PowerNet.PowerNetTick))]
internal static class SolarNetPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native=AccessTools.Method(typeof(GameConditionManager),nameof(GameConditionManager.ElectricityDisabled));int count=0;
        foreach(var code in instructions)
        {
            if(code.Calls(native))
            {
                var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);code.labels.Clear();yield return load;
                code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(SolarShield),nameof(SolarShield.Disabled));count++;
            }
            yield return code;
        }
        if(count!=1)throw new InvalidOperationException("RimLayers solar net query changed.");
    }
}
