using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Cargo;

[HarmonyPatch(typeof(StoreUtility),nameof(StoreUtility.IsGoodStoreCell))]
internal static class CargoStoreCellOrigin
{
    public static Thing? ParentAtAccess(Thing item,Pawn carrier)
    {
        if(CargoNetwork.OwnerOf(item) is not {} main||carrier==null)return item.SpawnedParentOrMe;
        return CargoNetwork.For(carrier.Map).BestPort(main,carrier,carrier.Position,out _);
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter=AccessTools.PropertyGetter(typeof(Thing),nameof(Thing.SpawnedParentOrMe));
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(getter))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_3).MoveLabelsFrom(instruction);
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(CargoStoreCellOrigin),nameof(ParentAtAccess)));
            }
            else yield return instruction;
        }
    }
}
