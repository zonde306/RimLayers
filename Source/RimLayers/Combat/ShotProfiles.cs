using HarmonyLib;
using Verse;

namespace RimLayers.Combat;

/// <summary>Logical height in tile units; independent of native probabilistic fillPercent.</summary>
public sealed class CoverHeightExtension : DefModExtension
{
    public float height;
}

public static class ShotProfiles
{
    public const float LayerHeight = 3;
    private static readonly AccessTools.FieldRef<VerbProperties,bool> Mortar=AccessTools.FieldRefAccess<VerbProperties,bool>("isMortar");
    public static ThingDef? Projectile(Verb? verb) => verb!=null && ReferenceEquals(verb,CombatCoordinates.CastingVerb)
        ? CombatCoordinates.CastingProjectile : verb is Verb_LaunchProjectile launch
        ? launch.Projectile ?? verb.verbProps.defaultProjectile : null;
    public static bool Curved(ThingDef? projectile) => projectile?.projectile is { } p &&
        (p.flyOverhead || p.arcHeightFactor > 0);
    public static bool Overhead(Verb? verb) => verb!=null && (Mortar(verb.verbProps)||Projectile(verb)?.projectile?.flyOverhead==true);
    public static bool Curved(Verb? verb) => Overhead(verb) || Curved(Projectile(verb));
    public static float Multiplier(int sourceLayer,int targetLayer,bool curved) =>
        SettingsMath.RangeMultiplier(sourceLayer-targetLayer,curved,RimLayersMod.Settings.HeightRange);
    public static float CoverHeight(Thing thing) =>
        thing.def.GetModExtension<CoverHeightExtension>() is { } profile
            ? Math.Clamp(profile.height,0,LayerHeight)
            : thing.def.Fillage==FillCategory.Full ? LayerHeight : Math.Clamp(thing.def.fillPercent*3,0.1f,3);
    public static float Arc(ThingDef? projectile,double distance,bool overhead=false) =>
        overhead||projectile?.projectile?.flyOverhead==true ? (float)Math.Max(3,distance/4) :
        projectile?.projectile is {} p ? (float)Math.Min(p.arcHeightFactor,distance*0.2) : 0;
}
