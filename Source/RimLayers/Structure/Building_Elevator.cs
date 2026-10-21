using UnityEngine;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
namespace RimLayers.Structure;

internal static class ConnectorDefs
{
    internal static bool IsUp(BuildableDef? d)=>Direction(d)>0;
    internal static bool IsDown(BuildableDef? d)=>Direction(d)<0;
    internal static bool IsAdjacent(BuildableDef? d)=>Direction(d)!=0;
    internal static int Direction(BuildableDef? d)=>d?.defName switch {"RL_Stairs" or "RL_LiftSingle"=>1,"RL_StairsDown" or "RL_LiftSingleDown"=>-1,_=>0};
}
public sealed partial class Building_Elevator:Building_Stairs
{
    public bool StopEnabled=true;
    public override bool ExtendsUp=>Direction>0;
    public override IntVec3 UpperCell=>ConnectsAdjacent?base.UpperCell:IntVec3.Invalid;
    public bool Powered=>this.TryGetComp<CompPowerTrader>()?.PowerOn==true;
    public override void ExposeData(){base.ExposeData();Scribe_Values.Look(ref StopEnabled,"rimLayersStopEnabled",true);ExposeExtensions();}
    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach(var g in base.GetGizmos())yield return g;
        if(!ConnectsAdjacent&&Faction==Faction.OfPlayer)foreach(var g in ExpansionGizmos())yield return g;
        if(!ConnectsAdjacent)yield return new Command_Toggle{defaultLabel="RL_LiftStop".Translate(),defaultDesc="RL_LiftStopHelp".Translate(),icon=ContentFinder<Texture2D>.Get("Things/Building/RimLayers/LiftVertical"),isActive=()=>StopEnabled,toggleAction=()=>{StopEnabled=!StopEnabled;LayerMapComponent.For(Map)?.Connectors.Invalidate();}};
    }
    public override string GetInspectString()=>base.GetInspectString()+"\n"+(Powered?"RL_LiftPowered":"RL_LiftEmergency").Translate().ToString()+
        (ConnectsAdjacent?"":"\n"+(StopEnabled?"RL_LiftStop":"RL_LiftShaftOnly").Translate().ToString()+ExtensionInspection());
}
public sealed class CompConnectorState:ThingComp
{
    public override void ReceiveCompSignal(string signal)
    {base.ReceiveCompSignal(signal);if(signal is "PowerTurnedOn" or "PowerTurnedOff" or "FlickedOn" or "FlickedOff")LayerMapComponent.For(parent.Map)?.Connectors.Invalidate();}
}
public sealed class CompProperties_ConnectorState:CompProperties{public CompProperties_ConnectorState()=>compClass=typeof(CompConnectorState);}

// Native power-net teardown resets flickables; preserve the user's elevator switch across reconnect/transport.
[HarmonyPatch(typeof(CompPowerTrader),nameof(CompPowerTrader.ResetPowerVars))]
internal static class LiftSwitchPreservation
{
    static void Prefix(CompPowerTrader __instance,out bool? __state)
    {__state=__instance.parent is Building_Elevator?__instance.parent.TryGetComp<CompFlickable>()?.SwitchIsOn:null;}
    static void Postfix(CompPowerTrader __instance,bool? __state)
    {if(__state.HasValue&&__instance.parent.TryGetComp<CompFlickable>() is {} flick)flick.SwitchIsOn=__state.Value;}
}
