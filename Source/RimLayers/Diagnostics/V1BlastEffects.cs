using HarmonyLib;
using RimLayers.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static void BeginBlastEffects()
    {
        PrepareBlastArena();
        int upper=upperBlastTarget.HitPoints,far=farBlastTarget.HitPoints;
        farBlastTarget.Position=At(0,88,84);
        // Detonate near the ceiling: the next storey is physically within Vaporize's 2.9 cutoff.
        var previous=BlastAccess.Impact;
        BlastAccess.Impact=(map,At(0,85,84),new Vector3(85.5f,2.9f,84.5f));
        try { GenExplosion.DoExplosion(At(0,85,84),map,4,DefDatabase<DamageDef>.GetNamed("Vaporize"),pawn,1,1,
            doVisualEffects:false,doSoundEffects:false,propagationSpeed:100); }
        finally { BlastAccess.Impact=previous; }
        var blast=owner.Combat.Blasts.Select(b=>b.Explosion).Single(e=>!e.Destroyed);
        Check("vaporize-candidate-crosses-layer-through-open-edge",BlastAccess.Cells(blast).Contains(upperBlastTarget.Position));
        AccessTools.Method(typeof(Explosion),"Tick").Invoke(blast,null);
        Check("native-vaporize-cutoff-uses-physical-distance",upperBlastTarget.HitPoints<upper && farBlastTarget.HitPoints==far);
        GenExplosion.DoExplosion(At(0,84,84),map,5,DefDatabase<DamageDef>.GetNamed("Smoke"),pawn,0,0,
            postExplosionGasType:GasType.BlindSmoke,postExplosionGasAmount:96,postExplosionGasRadiusOverride:4,
            propagationSpeed:100,excludeRadius:2,doVisualEffects:false,doSoundEffects:false);
        blast=owner.Combat.Blasts.Select(b=>b.Explosion).Single(e=>!e.Destroyed);
        Check("gas-probe-includes-upper-air-and-outer-ground",BlastAccess.Cells(blast).Contains(At(1,86,85)) &&
            BlastAccess.Cells(blast).Contains(At(0,84,89)));
        AccessTools.Method(typeof(Explosion),"Tick").Invoke(blast,null);
        Check("cross-layer-gas-emission-uses-physical-radius",At(1,86,85).AnyGas(map,GasType.BlindSmoke));
        Check("gas-radius-override-excludes-outer-covered-cell",!At(0,84,89).AnyGas(map,GasType.BlindSmoke));
        Check("exclude-radius-skips-center-effects",!At(0,84,84).AnyGas(map,GasType.BlindSmoke));
        Check("slab-excludes-upper-gas",!At(1,84,84).AnyGas(map,GasType.BlindSmoke));
        Check("native-effect-waves-finish",owner.Combat.Blasts.All(b=>b.Explosion.Destroyed));
        stage=11;ticks=0;
    }
}
