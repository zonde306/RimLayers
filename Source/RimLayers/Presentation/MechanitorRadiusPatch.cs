using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Presentation;

[HarmonyPatch(typeof(Pawn_MechanitorTracker),nameof(Pawn_MechanitorTracker.DrawCommandRadius))]
internal static class MechanitorRadiusPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var position=AccessTools.PropertyGetter(typeof(Thing),nameof(Thing.Position));int count=0;
        foreach(var code in source)
        {
            if(code.Calls(position))
            {code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(MechanitorRadiusPatch),nameof(ShownCenter));count++;}
            yield return code;
        }
        if(count!=1)throw new InvalidOperationException($"RimLayers mechanitor radius anchor changed: {count}");
    }
    private static IntVec3 ShownCenter(Thing thing)
    {
        var owner=LayerMapComponent.For(thing.Map);
        return owner!=null&&owner==LayerView.Current&&owner.TryDecode(thing.Position,out var cell) ?
            owner.ToStorage(new LayerCell(owner.ViewedLayer,cell.X,cell.Z)) : thing.Position;
    }
}


// Pawn.DrawAt is culled on other floors. Submit the remote overlay from the native
// MapComponentDraw overlay stage instead, without rendering the hidden controller.
internal static class RemoteMechanitorRadius
{
    internal static int DrawCalls,VisibleCalls;
    internal static void Draw(LayerMapComponent owner)
    {
        DrawCalls++;
        if(!ModsConfig.BiotechActive||owner!=LayerView.Current)return;
        // SelectedPawns is a reused native scratch list; DrawCommandRadius queries it again.
        foreach(Pawn controller in Find.Selector.SelectedPawns.Where(p=>p.Drafted)
            .Select(p=>p.GetOverseer()).OfType<Pawn>().Distinct().ToArray())
            if(controller.Spawned&&controller.Map==owner.map&&owner.TryDecode(controller.Position,out var cell)&&cell.LayerId!=owner.ViewedLayer)
            {
                VisibleCalls++;
                controller.mechanitor?.DrawCommandRadius();
            }
    }
}
