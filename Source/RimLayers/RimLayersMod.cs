using HarmonyLib;
using RimLayers.Diagnostics;
using Verse;
using RimWorld;

namespace RimLayers;

public sealed class RimLayersMod : Mod
{
    public static RimLayersSettings Settings { get; private set; } = new();
    internal static ModContentPack SettingsContent = null!;
    private readonly SettingsMenu menu = new();
    public override string SettingsCategory() => "RimLayers";
    public override void DoSettingsWindowContents(UnityEngine.Rect inRect) => menu.Draw(inRect);
    public override void WriteSettings() { Settings.Normalize(); base.WriteSettings(); }
    public const string HarmonyId = "rimlayers.core";
    public RimLayersMod(ModContentPack content) : base(content)
    {
        SettingsContent=content;
        Settings=GetSettings<RimLayersSettings>();
        LongEventHandler.ExecuteWhenFinished(SettingsDefinitions.Apply);
        var harmony = new Harmony(HarmonyId);
        // Optional-adapter registration must run before the patch loop: their Prepare() gates
        // depend on the detected state this sets up.
        Compatibility.VVRaceCompatibility.Install();
        // Same ordering rule as VVRace: an optional adapter that answers Prepare() from its own
        // binding state must resolve before the class-processor loop reaches its patch classes.
        Compatibility.AerocraftCompatibility.Install();
        // Third-party pipe/water/skylight adapters. Installed here (not only from their static
        // constructor) so their Harmony patches are in place before the class-processor loop, which
        // is the only point where a failure is still reported per patch class.
        Compatibility.DubsCompatibility.Install(harmony);
        Compatibility.VEPipeSystemCompatibility.Install(harmony);
        int patchClasses=0;
        var failed=new List<string>();
        foreach (var type in typeof(RimLayersMod).Assembly.GetTypes())
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0)
            {
                // Harmony's class processor rethrows any transpiler/patch failure, and a throw here
                // aborts the mod constructor: every later patch class (and the optional VGE adapter)
                // silently disappears and the game looks inert. Isolate per class so one third-party
                // conflict costs exactly one patch instead of the whole mod.
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    patchClasses++;
                }
                catch (Exception exception)
                {
                    failed.Add(type.Name);
                    Log.Error($"[RimLayers] Patch class {type.Name} failed and was skipped; the rest of the mod keeps working.\n{exception}");
                }
            }
        Compatibility.VanillaGravshipCompatibility.Install(harmony);
        M1Integration.Configure();
        V1Integration.Configure();
        Log.Message($"[RimLayers] 0.1.5 loaded ({patchClasses} patch classes{(failed.Count==0?"":$", {failed.Count} skipped: {string.Join(", ", failed)}")}). Global settings enabled; connector expansion preserves existing bands and coordinates.");
    }
}

[DefOf]
public static class RimLayersDefOf
{
    public static KeyBindingDef RL_LayerUp = null!;
    public static KeyBindingDef RL_LayerDown = null!;
    public static TerrainDef RL_OpenAir = null!;
    public static TerrainDef RL_Isolation = null!;
    public static TerrainDef RL_StructuralFloor = null!;
    public static ThingDef RL_Stairs = null!;
    public static ThingDef RL_PowerRiser = null!;
    static RimLayersDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RimLayersDefOf));
}
