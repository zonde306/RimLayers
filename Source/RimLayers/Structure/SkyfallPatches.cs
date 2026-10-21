using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;

namespace RimLayers.Structure;

[HarmonyPatch(typeof(GenSpawn),nameof(GenSpawn.Spawn),new[]{typeof(Thing),typeof(IntVec3),typeof(Map),typeof(Rot4),typeof(WipeMode),typeof(bool),typeof(bool)})]
internal static class SkySpawnPatch
{
    static bool Prefix(Thing newThing,ref IntVec3 loc,Map map,Rot4 rot,bool respawningAfterLoad,ref Thing? __result)
    {
        if(newThing!=SkyPayload.RecoveringPod && !SkyPayload.PrepareSpawn(newThing,loc,rot,map)){__result=null;return false;}
        if(!respawningAfterLoad && newThing is Skyfaller faller && SkyfallService.Incoming(faller) && LayerMapComponent.For(map) is { } owner)
        {
            Rot4 actual=faller.def.rotatable && faller.GetDirectlyHeldThings().Count>0?faller.GetDirectlyHeldThings()[0].Rotation:rot;
            if(owner.Skyfall.Trace(faller,loc,actual,out var hit))loc=hit;
        }
        return true;
    }
    static ProgramState CapacityState(Thing thing)=>thing==SkyPayload.RecoveringPod?ProgramState.MapInitializing:Current.ProgramState;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var state=AccessTools.PropertyGetter(typeof(Current),nameof(Current.ProgramState));int count=0;
        foreach(var code in instructions)
        {
            if(code.Calls(state))
            {
                var arg=new CodeInstruction(OpCodes.Ldarg_0);arg.MoveLabelsFrom(code);yield return arg;
                code.operand=AccessTools.Method(typeof(SkySpawnPatch),nameof(CapacityState));count++;
            }
            yield return code;
        }
        if(count!=1)throw new InvalidOperationException("GenSpawn capacity branch changed.");
    }
}

[HarmonyPatch(typeof(Skyfaller),"Tick")]
internal static class SkyTickPatch
{
    static bool Prefix(Skyfaller __instance)=>!SkyfallService.Incoming(__instance) || LayerMapComponent.For(__instance.Map) is not { } owner || owner.Skyfall.BeforeTick(__instance);
}
[HarmonyPatch(typeof(Skyfaller),"HitRoof")]
internal static class SkyRoofPatch
{
    static void Prefix(Skyfaller __instance)
    {if(SkyfallService.Incoming(__instance))LayerMapComponent.For(__instance.Map)?.Skyfall.PunchUpperRoofs(__instance);}
}
[HarmonyPatch(typeof(DropCellFinder),nameof(DropCellFinder.CanPhysicallyDropInto))]
internal static class SkyDropCellPatch
{
    static void Postfix(IntVec3 c,Map map,bool canRoofPunch,ref bool __result)
    {if(__result && LayerMapComponent.For(map) is { } owner)__result=owner.Skyfall.CanArrive(c,canRoofPunch);}
}

[HarmonyPatch]
internal static class SkyPayloadScopePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Skyfaller),"SpawnThings");
        yield return AccessTools.Method(typeof(DropPodIncoming),"SpawnThings");
        yield return AccessTools.Method(typeof(ActiveTransporter),"PodOpen");
    }
    static void Prefix(Thing __instance,out SkyPayloadContext? __state)
    {
        if(__instance is Skyfaller faller && !SkyfallService.Incoming(faller)){__state=SkyPayload.Current;SkyPayload.Current=null;}
        else __state=SkyPayload.Enter(__instance);
    }
    static Exception? Finalizer(Thing __instance,SkyPayloadContext? __state,Exception? __exception)
    {
        var scope=SkyPayload.Current;SkyPayload.Current=__state;
        if(scope?.Parent==__instance && __instance is ActiveTransporter pod)
        {
            var skyfall=LayerMapComponent.For(scope.Map)!.Skyfall;
            if(pod.Contents.innerContainer.Count>0 && !pod.Destroyed)
            {
                if(!pod.Spawned)
                {
                    var previous=SkyPayload.RecoveringPod;SkyPayload.RecoveringPod=pod;
                    try{GenSpawn.Spawn(pod,scope.Position,scope.Map,pod.Rotation,WipeMode.Vanish,respawningAfterLoad:true);}
                    finally{SkyPayload.RecoveringPod=previous;}
                }
                skyfall.Waiting(pod);
            }
            else if(pod.Contents.innerContainer.Count==0)skyfall.Recovered(pod,scope.Position);
        }
        return __exception;
    }
}

[HarmonyPatch]
internal static class SkyPlacePatch
{
    static MethodBase TargetMethod()=>AccessTools.Method(typeof(GenPlace),nameof(GenPlace.TryPlaceThing),new[]{typeof(Thing),typeof(IntVec3),typeof(Map),typeof(ThingPlaceMode),typeof(Thing).MakeByRefType(),typeof(Action<Thing,int>),typeof(Predicate<IntVec3>),typeof(Rot4?),typeof(int)});
    static bool Prefix(Thing thing,IntVec3 center,Map map,ThingPlaceMode mode,Rot4? rot,ref Predicate<IntVec3>? extraValidator,ref Thing? lastResultingThing,ref bool __result)
    {
        if(SkyPayload.Current is not { } scope || scope.Map!=map)return true;
        Rot4 rotation=rot??thing.def.defaultPlacingRot;var previous=extraValidator;
        extraValidator=c=>SkyPayload.Allowed(thing.def,c,rotation,map) &&
            (mode==ThingPlaceMode.Direct || SkyPayload.SafeNear(thing.def,c,rotation,map)) && (previous==null || previous(c));
        if(mode!=ThingPlaceMode.Direct && thing.def.category!=ThingCategory.Filth || extraValidator(center))return true;
        __result=false;lastResultingThing=null;return false;
    }
}

[HarmonyPatch(typeof(Skyfaller),"Impact")]
internal static class SkyImpactPatch
{
    static void Prefix(Skyfaller __instance)
    {if(SkyfallService.Incoming(__instance) && LayerMapComponent.For(__instance.Map) is { } owner)owner.Skyfall.ImpactCommits++;}
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var clear=AccessTools.Method(typeof(ThingOwner),nameof(ThingOwner.ClearAndDestroyContents));
        var destroy=AccessTools.Method(typeof(Thing),nameof(Thing.Destroy));
        var effect=AccessTools.Method(typeof(GenSpawn),nameof(GenSpawn.TrySpawn),new[]{typeof(ThingDef),typeof(IntVec3),typeof(Map),typeof(Thing).MakeByRefType(),typeof(WipeMode),typeof(bool)});
        int clears=0,finishes=0,effects=0;
        foreach(var code in instructions)
        {
            if(code.Calls(clear)){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(SkyPayload),nameof(SkyPayload.KeepUnplaced));clears++;}
            if(code.Calls(destroy)){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(SkyPayload),nameof(SkyPayload.FinishImpact));finishes++;}
            if(code.Calls(effect)){code.operand=AccessTools.Method(typeof(SkyPayload),nameof(SkyPayload.SpawnImpactEffect));effects++;}
            yield return code;
        }
        if(clears!=1 || finishes!=1 || effects!=1)throw new InvalidOperationException("Skyfaller impact finalization changed.");
    }
}

[HarmonyPatch(typeof(ActiveTransporter),"PodOpen")]
internal static class SkyPodOpenPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
    {
        var codes=instructions.ToList();var clear=AccessTools.Method(typeof(ThingOwner),nameof(ThingOwner.ClearAndDestroyContents));
        var exit=generator.DefineLabel();codes.Last(c=>c.opcode==OpCodes.Ret).labels.Add(exit);int count=0;
        foreach(var code in codes)
        {
            if(code.Calls(clear))
            {
                code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(SkyPayload),nameof(SkyPayload.ClearIfPlaced));
                yield return code;yield return new CodeInstruction(OpCodes.Brfalse,exit);count++;
            }
            else yield return code;
        }
        if(count!=1)throw new InvalidOperationException("ActiveTransporter open finalization changed.");
    }
}

[HarmonyPatch(typeof(ActiveTransporter),nameof(ActiveTransporter.Destroy))]
internal static class SkyPodSelfWipePatch
{
    static bool Prefix(ActiveTransporter __instance)=>SkyPayload.Current?.Parent!=__instance || __instance.Contents.innerContainer.Count==0;
}


[HarmonyPatch(typeof(ActiveTransporter),"Tick")]
internal static class SkyPodRetryPatch
{
    static bool Prefix(ActiveTransporter __instance)=>LayerMapComponent.For(__instance.Map) is not { } owner ||
        !owner.Skyfall.IsWaiting(__instance) || (Find.TickManager.TicksGame+__instance.thingIDNumber)%30==0;
}

[HarmonyPatch(typeof(GenSpawn),nameof(GenSpawn.CheckMoveItemsAside))]
internal static class SkyMoveAsidePatch
{
    static bool Prefix(IntVec3 thingPos,Rot4 thingRot,ThingDef thingDef,Map map)
    {
        if(SkyPayload.Current is not { } scope || scope.Map!=map)return true;
        if(thingDef.surfaceType==SurfaceType.None && thingDef.passability!=Traversability.Standable)
            SkyPayload.MoveItemsSafely(thingPos,thingRot,thingDef,map);
        return false;
    }
}


[HarmonyPatch(typeof(ShuttleIncoming),"SpawnThings")]
internal static class SkyShuttlePlacementPatch
{
    // Blueprint cancellation belongs to a successful placement, not each failed retry.
    static bool Prefix(ShuttleIncoming __instance)=>SkyPayload.ReadyForShuttle(__instance);
}


[HarmonyPatch(typeof(DropCellFinder),nameof(DropCellFinder.RandomDropSpot))]
internal static class SkyRandomDropPatch
{
    static IntVec3 Find(Predicate<IntVec3> validator,Map map,int maxTries)
    {
        var owner=LayerMapComponent.For(map);
        return CellFinderLoose.RandomCellWith(c=>validator(c) && (owner==null || owner.Skyfall.CanArrive(c,true)),map,maxTries);
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original=AccessTools.Method(typeof(CellFinderLoose),nameof(CellFinderLoose.RandomCellWith));int count=0;
        foreach(var code in instructions)
        {
            if(code.Calls(original)){code.operand=AccessTools.Method(typeof(SkyRandomDropPatch),nameof(Find));count++;}
            yield return code;
        }
        if(count!=2)throw new InvalidOperationException("Random drop fallback changed.");
    }
}
