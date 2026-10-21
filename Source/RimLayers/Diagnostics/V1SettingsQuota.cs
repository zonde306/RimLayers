using RimLayers.Core;
using RimLayers.Generation;
using Verse;
using RimWorld;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void BeginSettingsQuota()
    {
        var quota=LayerExpansionQuota.Current!;var settings=RimLayersMod.Settings;int start=quota.Generated;
        var oldSize=map.Size;var oldGround=At(0,35,35);var oldUpper=At(2,35,35);
        Check("quota-derives-existing-extra-floors",start==owner.Layout!.LayerCount-1&&start==2);
        settings.GlobalLimit=start;settings.UpperLimit=9;
        Check("full-global-limit-denies-new-floor-read-only",!quota.Check(map,3).Accepted&&map.Size==oldSize);
        settings.UpperLimit=0;settings.UndergroundLimit=0;
        Check("lowered-settings-preserve-existing-floor-use",quota.Check(map,2).Accepted&&LiveMapExpansion.EnsureElevation(map,2).After==oldSize);
        bool rejected=false;try{LiveMapExpansion.EnsureElevation(map,3);}catch(InvalidOperationException){rejected=true;}
        Check("commit-denial-leaves-layout-unchanged",rejected&&map.Size==oldSize&&owner.Layout.LayerCount==3);
        settings.UpperLimit=5;settings.UndergroundLimit=5;settings.GlobalLimit=20;settings.Minerals=0;settings.Caves=false;settings.CaveHives=false;
        foreach(int elevation in new[]{3,-2,5,-5})LiveMapExpansion.EnsureElevation(map,elevation);
        owner=LayerMapComponent.For(map)!;
        Check("eleven-floors-survive-mixed-batch-add-order",owner.Layout!.LayerCount==11&&owner.Layout.LowestElevation==-5&&owner.Layout.HighestElevation==5);
        Check("mixed-additions-preserve-original-storage-bands",At(0,35,35)==oldGround&&At(2,35,35)==oldUpper);
        Check("empty-floors-still-consume-global-quota",quota.Generated==10);
        Check("disabled-caves-and-minerals-fill-new-underground-with-natural-rock",owner.Layout.OrderedLayers.Where(l=>owner.Elevation(l)<0).SelectMany(l=>owner.LayerRect(l)).All(c=>c.GetEdifice(map) is Mineable rock&&!rock.def.building.isResourceRock));
        Check("disabled-initial-hives-spawn-none",!map.listerThings.ThingsOfDef(ThingDefOf.Hive).Any(t=>owner.TryDecode(t.Position,out var q)&&owner.Elevation(q.LayerId)<0));
        int before=quota.Generated;settings.GlobalLimit=9;
        Check("over-limit-existing-layout-remains-accessible",quota.Check(map,5).Accepted&&quota.Check(map,-5).Accepted&&quota.Generated==before);
        Check("over-limit-save-rejects-only-new-floors",!quota.Check(map,6).Accepted&&!quota.Check(map,-6).Accepted);
        settings.GlobalLimit=20;settings.UpperLimit=9;settings.UndergroundLimit=9;
        var target=Find.WorldGrid.Orbit.Tiles.First(t=>Find.WorldObjects.MapParentAt(t.tile)==null).tile;
        Check("ship-preview-does-not-reserve-or-generate",quota.Preview(target,3,111).Accepted&&quota.Reserved==0&&quota.Generated==10);
        Check("ship-reservation-charges-complete-batch",quota.Reserve(111,target,3).Accepted&&quota.Reserved==3&&quota.Generated==10);
        Check("repeat-reservation-is-idempotent",quota.Reserve(111,target,3).Accepted&&quota.Reserved==3&&quota.Reservations.Count==1);
        Check("same-destination-promises-count-only-maximum-unallocated-span",quota.Reserve(112,target,4).Accepted&&quota.Reserved==4);
        var otherTarget=Find.WorldGrid.Orbit.Tiles.First(t=>t.tile!=target&&Find.WorldObjects.MapParentAt(t.tile)==null).tile;
        Check("distinct-destination-promises-add-to-global-usage",quota.Reserve(113,otherTarget,2).Accepted&&quota.Reserved==6);
        quota.Release(112);quota.Release(113);
        Check("canceling-overlapping-promises-keeps-original-commitment",quota.Reserved==3&&quota.Reservations.Single().EngineId==111);
        settings.SpaceExpansion=false;settings.GlobalLimit=0;settings.UpperLimit=0;
        Check("valid-inflight-commitment-survives-lowered-limits",quota.Preview(target,3,111).Accepted);
        Check("higher-landing-cannot-borrow-old-commitment",!quota.Preview(target,4,111).Accepted);
        var main=map;var space=MakeUnmanagedMap(target,120,120);Current.Game.CurrentMap=main;
        Check("quota-does-not-block-native-base-map-creation",LayerMapComponent.For(space)==null&&space.Size==new IntVec3(120,1,120));
        Check("space-switch-rejects-uncommitted-expansion",!quota.Check(space,1).Accepted);
        LayerExpansionQuota.ActiveCommitment=111;
        try{LiveMapExpansion.EnsureElevation(space,3);}finally{LayerExpansionQuota.ActiveCommitment=0;}
        Check("allocation-converts-reservation-to-real-occupancy",quota.Generated==13&&quota.Reserved==0&&quota.Reservations.Count==0);
        Check("disabled-space-switch-keeps-existing-floors",quota.Check(space,3).Accepted);
        settings.SpaceExpansion=true;settings.GlobalLimit=100;settings.UndergroundLimit=9;
        Check("space-expansion-toggle-never-allows-underground",!quota.Check(space,-1).Accepted);
        Current.Game.DeinitAndRemoveMap(space,false);
        Check("only-map-removal-releases-generated-capacity",quota.Generated==10);
        settings.UpperLimit=9;
        Check("canceled-reservation-releases-capacity",quota.Reserve(222,target,2).Accepted&&quota.Reserved==2);
        quota.Release(222);quota.Release(222);
        Check("repeated-release-is-idempotent",quota.Reserved==0&&quota.Generated==10);
        int existingThings=map.listerThings.AllThings.Count;
        settings.Minerals=3;settings.Caves=true;settings.CaveHives=false;
        Check("changed-geology-settings-never-regenerate-developed-layer",LiveMapExpansion.EnsureElevation(map,-5).Milliseconds==0&&map.listerThings.AllThings.Count==existingThings);
        LiveMapExpansion.EnsureElevation(map,-6);int newLayer=owner.Layout!.LayerAt(-6);
        Check("new-layer-uses-enabled-caves-and-positive-mineral-multiplier",owner.LayerRect(newLayer).Any(c=>c.GetEdifice(map)==null)&&owner.LayerRect(newLayer).Any(c=>c.GetEdifice(map) is Mineable rock&&rock.def.building.isResourceRock));
        Check("hive-switch-off-keeps-new-caves-without-hives",!map.listerThings.ThingsOfDef(ThingDefOf.Hive).Any(t=>owner.TryDecode(t.Position,out var q)&&q.LayerId==newLayer));
        Check("old-geology-stays-solid-after-new-settings",owner.LayerRect(owner.Layout.LayerAt(-1)).All(c=>c.GetEdifice(map) is Mineable rock&&!rock.def.building.isResourceRock));
        settings.Reset();Finish();
    }
}
