using HarmonyLib;
using RimLayers.Core;
using RimLayers.Recovery;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static readonly List<string> recoveryWarningMessages=new();
    private static void CaptureRecoveryMessage(string text)=>recoveryWarningMessages.Add(text);
    private static void CheckRecoveryConstructionWarnings()
    {
        var service=RecoveryService;
        var warnings=(Dictionary<int,int>)AccessTools.Field(typeof(EntityRecoveryService),"warnings").GetValue(service)!;
        var queued=(Dictionary<Thing,string>)AccessTools.Field(typeof(EntityRecoveryService),"pendingWarnings").GetValue(service)!;
        var flush=AccessTools.Method(typeof(EntityRecoveryService),"FlushWarnings");
        var next=AccessTools.Field(typeof(EntityRecoveryService),"nextWarning");int prior=(int)next.GetValue(service)!;
        var hook=new Harmony("rimlayers.diagnostics.recovery-warnings");
        hook.Patch(AccessTools.Method(typeof(Messages),nameof(Messages.Message),new[]{typeof(string),typeof(MessageTypeDef),typeof(bool)}),
            prefix:new HarmonyMethod(typeof(V1Integration),nameof(CaptureRecoveryMessage)));
        void Flush(){recoveryWarningMessages.Clear();flush.Invoke(service,new object[]{Find.TickManager.TicksGame});}
        try
        {
            // The preceding unknown-building probe was removed; it must not leave a stale popup.
            Flush();Check("removed-building-warning-does-not-send-empty-recovery-list",recoveryWarningMessages.Count==0);
            var source=At(1,90,76);var lower=At(0,90,76);
            map.roofGrid.SetRoof(lower,RoofDefOf.RoofConstructed);
            var floor=RimLayersDefOf.RL_StructuralFloor;
            Check("floor-warning-fixture-is-valid-plan-over-air",!owner.Structure.HasSurface(source)&&owner.Structure.Validate(floor,source,Rot4.North,true).Accepted);
            new Designator_Build(floor).DesignateMultiCell(new[]{source});
            var blueprint=source.GetThingList(map).Single(t=>t is Blueprint&&t.def.entityDefToBuild==floor);
            for(int i=0;i<3;i++)service.Notice(blueprint,map);
            Check("native-floor-blueprint-is-not-an-airspace-recovery-issue",!warnings.ContainsKey(blueprint.thingIDNumber)&&!queued.ContainsKey(blueprint)&&!service.Records.Any(r=>r.Thing==blueprint));
            blueprint.Destroy(DestroyMode.Vanish);
            var frame=FrameFor(floor,source,4);
            for(int i=0;i<3;i++)service.Notice(frame,map);
            Check("floor-frame-with-materials-is-not-an-airspace-recovery-issue",frame.Spawned&&frame.resourceContainer.TotalStackCount==4&&!warnings.ContainsKey(frame.thingIDNumber)&&!service.Records.Any(r=>r.Thing==frame));
            Flush();Check("normal-floor-construction-emits-no-recovery-message",recoveryWarningMessages.Count==0);
            map.roofGrid.SetRoof(lower,null);service.Notice(frame,map);
            Check("floor-plan-losing-roof-is-still-inspected",warnings.ContainsKey(frame.thingIDNumber)&&queued.ContainsKey(frame)&&!service.Records.Any(r=>r.Thing==frame));
            map.roofGrid.SetRoof(lower,RoofDefOf.RoofConstructed);
            Flush();Check("repaired-floor-support-retracts-delayed-warning",recoveryWarningMessages.Count==0);
            frame.Destroy(DestroyMode.Vanish);map.roofGrid.SetRoof(lower,null);
            var wall=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel),source,map);
            service.Notice(wall,map);Flush();
            Check("unknown-building-message-does-not-claim-a-recovery-queue",recoveryWarningMessages.SequenceEqual(new[]{"RL_RecoveryBuildingWarning".Translate(1).ToString()})&&!service.Records.Any(r=>r.Thing==wall));
            wall.Destroy(DestroyMode.Vanish);
            var wallPlan=GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall.blueprintDef),source,map);
            service.Notice(wallPlan,map);Flush();
            Check("ordinary-unsupported-blueprints-are-not-blanket-exempt",warnings.ContainsKey(wallPlan.thingIDNumber)&&recoveryWarningMessages.SequenceEqual(new[]{"RL_RecoveryBuildingWarning".Translate(1).ToString()}));
            wallPlan.Destroy(DestroyMode.Vanish);
            Thing StoreItem()
            {
                var item=ThingMaker.MakeThing(ThingDefOf.Silver);item.stackCount=7;
                if(!service.CaptureInvalidSpawn(item,map,new IntVec3(-2,0,-2),Rot4.North))throw new InvalidOperationException("Warning storage fixture failed");
                return item;
            }
            var stored=StoreItem();var record=service.Records.Single(r=>r.Thing==stored);
            Flush();Check("real-stored-entity-still-emits-pending-recovery-message",recoveryWarningMessages.SequenceEqual(new[]{"RL_RecoveryWarning".Translate(service.Records.Count).ToString()})&&service.GetDirectlyHeldThings().Contains(stored));
            service.TryRelease(record,map,At(0,90,77));stored.Destroy(DestroyMode.Vanish);
            var resolved=StoreItem();var resolvedRecord=service.Records.Single(r=>r.Thing==resolved);
            bool released=service.TryRelease(resolvedRecord,map,At(0,91,77));Flush();
            Check("resolved-before-notification-emits-no-stale-recovery-message",released&&resolved.Spawned&&resolved.stackCount==7&&!queued.ContainsKey(resolved)&&recoveryWarningMessages.Count==0);
            resolved.Destroy(DestroyMode.Vanish);
        }
        finally {hook.UnpatchAll(hook.Id);next.SetValue(service,prior);recoveryWarningMessages.Clear();}
    }
}
