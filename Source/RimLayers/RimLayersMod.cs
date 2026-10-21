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
        int patchClasses=0;
        foreach (var type in typeof(RimLayersMod).Assembly.GetTypes())
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0)
            {
                harmony.CreateClassProcessor(type).Patch();
                patchClasses++;
            }
        Compatibility.VanillaGravshipCompatibility.Install(harmony);
        M1Integration.Configure();
        V1Integration.Configure();
        Log.Message($"[RimLayers] 0.1.5 loaded ({patchClasses} patch classes). Global settings enabled; connector expansion preserves existing bands and coordinates.");
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
