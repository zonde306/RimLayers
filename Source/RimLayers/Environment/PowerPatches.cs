using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Environment;

[HarmonyPatch(typeof(PowerNetMaker),"ContiguousPowerBuildings")]
internal static class LayerPowerGraphPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native=AccessTools.Method(typeof(GenAdj),nameof(GenAdj.CellsAdjacentCardinal),new[]{typeof(Thing)});
        int count=0;
        foreach(var code in instructions)
        {
            if(code.Calls(native)) { code.operand=AccessTools.Method(typeof(PowerLinks),nameof(PowerLinks.Neighbors));count++; }
            yield return code;
        }
        if(count!=1) throw new InvalidOperationException("Unexpected native power adjacency entry count.");
    }
}
[HarmonyPatch(typeof(PowerNetManager),nameof(PowerNetManager.UpdatePowerNetsAndConnections_First))]
internal static class LayerPowerUpdatePatch
{
    static void Prefix(PowerNetManager __instance)=>LayerMapComponent.For(__instance.map)?.Power.Flush();
    static void Postfix(PowerNetManager __instance)=>LayerMapComponent.For(__instance.map)?.Power.AfterUpdate();
}
[HarmonyPatch(typeof(PowerNetManager),nameof(PowerNetManager.Notfiy_TransmitterTransmitsPowerNowChanged))]
internal static class LayerPowerChangedPatch
{
    static void Postfix(PowerNetManager __instance,CompPower transmitter)
    {
        if(transmitter.parent.TryGetComp<CompPowerRiser>()!=null)
            LayerMapComponent.For(__instance.map)?.Power.Mark(transmitter.parent.Position);
    }
}
[HarmonyPatch]
internal static class LayerPowerSurfacePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(Type type in typeof(Building).Assembly.GetTypes())
            if(typeof(Building).IsAssignableFrom(type) && type.GetMethod("get_TransmitsPowerNow",
                BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly) is {} method)
                yield return method;
    }
    static void Postfix(Building __instance,ref bool __result)
    { if(__result && !PowerLinks.HasSurface(__instance)) __result=false; }
}
[HarmonyPatch(typeof(PowerConnectionMaker),nameof(PowerConnectionMaker.BestTransmitterForConnector))]
internal static class LayerPowerConnectorQueryPatch
{
    static bool Prefix(IntVec3 connectorPos,Map map,List<PowerNet> disallowedNets,ref CompPower? __result)
    {
        var owner=LayerMapComponent.For(map);
        if(owner==null) return true;
        __result=owner.Power.BestTransmitter(connectorPos,disallowedNets);return false;
    }
}
[HarmonyPatch(typeof(PowerConnectionMaker),"PotentialConnectorsForTransmitter")]
internal static class LayerPowerConnectorCandidatesPatch
{
    static void Postfix(CompPower b,ref IEnumerable<CompPower> __result)
    {
        var owner=LayerMapComponent.For(b.parent.Map);
        if(owner!=null) __result=__result.Where(c=>c.parent.Map==owner.map && PowerLinks.SameLayer(owner.map,b.parent.Position,c.parent.Position));
    }
}
[HarmonyPatch(typeof(CompPower),nameof(CompPower.ConnectToTransmitter))]
internal static class LayerPowerParentGuardPatch
{
    static bool Prefix(CompPower __instance,CompPower transmitter,bool reconnectingAfterLoading)
    {
        var map=__instance.parent.Map;
        if(LayerMapComponent.For(map)==null) return true;
        if(PowerLinks.SameLayer(map,__instance.parent.Position,transmitter.parent.Position) && transmitter.parent.Map==map) return true;
        if(reconnectingAfterLoading && __instance.connectParent==transmitter)
        { __instance.connectParent=null;map.powerNetManager.Notify_ConnectorWantsConnect(__instance); }
        return false;
    }
}
