using HarmonyLib;
using RimLayers.Environment;
using RimLayers.Underground;
using RimWorld;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void EnvironmentGeologySettingsChecks(int layer)
    {
        var settings=RimLayersMod.Settings.Environment;
        var cells=owner.LayerRect(layer).Cells.ToList();
        using var caves=new MapGenFloatGrid(map);
        foreach(var c in cells)if(c.x%27<3)caves[c]=1;
        ThingDef Host(IntVec3 c)=>DefDatabase<ThingDef>.GetNamed("Granite");
        UndergroundGeology Plan(int depth=9,float maxValue=float.MaxValue)
        {
            Rand.PushState(51291);
            try{return UndergroundGeology.Build(owner,layer,depth,cells,caves,Host,10,maxValue);}
            finally{Rand.PopState();}
        }
        var neutral=new ThingDef{defName="RL_DiagnosticNaturalOre",thingClass=typeof(Mineable),building=new BuildingProperties{
            mineableThing=ThingDefOf.Steel,mineableScatterCommonality=1,mineableYield=40}};
        Check("unknown-mod-ore-has-neutral-depth-profile",UndergroundGeology.DepthWeight(neutral,9,settings)==1);
        Check("unknown-legal-natural-mod-ore-enters-eligibility-pool",UndergroundGeology.Eligible(neutral,float.MaxValue));
        neutral.building.mineableScatterCommonality=0;settings.MineralWeights[neutral.defName]=5;
        Check("weight-cannot-enable-disabled-mod-ore",!UndergroundGeology.Eligible(neutral,float.MaxValue));
        settings.MineralWeights.Clear();
        Check("native-value-ceiling-remains-effective",Plan(maxValue:2).Ore.Values.All(d=>d.building.mineableThing.BaseMarketValue<=2));
        settings.MineralDepth=0;
        Check("zero-depth-difference-restores-neutral-profiles",UndergroundGeology.LegalPool(float.MaxValue).All(d=>UndergroundGeology.DepthWeight(d,9,settings)==1));
        settings.MineralDepth=1;
        foreach(var d in UndergroundGeology.ConfigurablePool())settings.MineralWeights[d.defName]=0;
        Check("all-zero-per-ore-weights-exclude-all-minerals",Plan().Ore.Count==0);
        settings.MineralWeights.Clear();settings.LavaEnabled=false;
        Check("disabled-magma-generates-no-core-or-shore",Plan().Lava.Count==0&&Plan().Shore.Count==0);
        settings.LavaEnabled=true;settings.LavaCoverage=0;
        Check("zero-magma-coverage-generates-no-core",Plan().Lava.Count==0);
        settings.LavaCoverage=.005f;settings.GeothermalEnrichment=0;
        int budget=Plan().Record.Budget;
        settings.GeothermalEnrichment=3;
        Check("enrichment-never-adds-extra-cluster-budget",Plan().Record.Budget==budget);
        settings.GeothermalEnrichment=1;settings.LumpSize=.5f;var small=Plan();
        settings.LumpSize=2;var large=Plan();
        Check("lump-size-changes-cells-not-cluster-budget-or-native-yield",small.Record.Budget==large.Record.Budget&&small.Ore.Count<large.Ore.Count&&
            large.Record.ExpectedYield.All(p=>p.Value==large.Record.Cells[p.Key]*DefDatabase<ThingDef>.GetNamed(p.Key).building.mineableYield));
        settings.LumpSize=1;settings.LavaCoverage=.05f;settings.LavaCap=.1f;settings.LavaMinSize=8;settings.LavaMaxSize=256;
        var stairs=GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("RL_StairsDown"),EnvAt(-2,20,20),map);
        var protectedPlan=Plan();
        Check("magma-and-shore-protect-eight-cell-entrance-work-radius",protectedPlan.Lava.Concat(protectedPlan.Shore).All(c=>owner.TryDecode(c,out var q)&&
            (q.X-20)*(q.X-20)+(q.Z-20)*(q.Z-20)>64));
        stairs.Destroy(DestroyMode.Vanish);
        settings.ResetGroup("Lava");
        // A real Core temporary terrain uses the native cooling queue and keeps the permanent core separate.
        var at=EnvAt(-2,45,45);EnvClear(at,DefDatabase<TerrainDef>.GetNamed("RL_CooledLava"),RoofDefOf.RoofRockThick);
        map.terrainGrid.SetTempTerrain(at,DefDatabase<TerrainDef>.GetNamed("RL_LavaShallow"));
        map.tempTerrain.QueueRemoveTerrain(at,Find.TickManager.TicksGame);
        map.tempTerrain.Tick();
        Check("shallow-magma-cools-through-native-queue-even-without-odyssey",at.GetTerrain(map).defName=="RL_CooledLava");
        Check("deep-magma-remains-permanent",!DefDatabase<TerrainDef>.GetNamed("RL_LavaDeep").temporary);
    }
}
