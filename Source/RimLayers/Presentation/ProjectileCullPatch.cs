using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Combat;
using RimLayers.Core;
using Verse;

namespace RimLayers.Presentation;

// Vanilla culls non-Pawns by Position before consulting DrawPos. A saved partial sweep
// still owns its original storage position, while its visible contact is on another floor.
[HarmonyPatch(typeof(DynamicDrawManager),"ComputeCulledThings")]
internal static class ProjectileCullPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var position=AccessTools.PropertyGetter(typeof(Thing),nameof(Thing.Position));
        var bounds=AccessTools.Method(typeof(GenAdj),nameof(GenAdj.OccupiedDrawRect));
        int cells=0,rects=0;
        foreach(var code in source)
        {
            if(code.Calls(position))
            {code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(ProjectileCullPatch),nameof(DrawCell));cells++;}
            else if(code.Calls(bounds))
            {code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(ProjectileCullPatch),nameof(DrawBounds));rects++;}
            yield return code;
        }
        if(cells!=1||rects!=1)throw new InvalidOperationException($"RimLayers projectile cull anchors changed: {cells}/{rects}");
    }
    private static IntVec3 DrawCell(Thing thing)=>thing is Projectile projectile &&
        CombatService.Flight(projectile)!=null && LayerMapComponent.For(thing.Map)!=null ? thing.DrawPos.ToIntVec3() : thing.Position;
    private static CellRect DrawBounds(Thing thing)
    {
        CellRect bounds=thing.OccupiedDrawRect();
        IntVec3 offset=DrawCell(thing)-thing.Position;
        return new CellRect(bounds.minX+offset.x,bounds.minZ+offset.z,bounds.Width,bounds.Height);
    }
}
