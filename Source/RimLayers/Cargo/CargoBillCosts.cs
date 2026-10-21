using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimLayers.Cargo;

/// <summary>Only the distance tie-breakers change; native recipe value, filters and amount selection remain intact.</summary>
[HarmonyPatch]
internal static class CargoBillCosts
{
    [ThreadStatic] private static Pawn? worker;
    [ThreadStatic] private static Dictionary<Thing,int>? costs;
    internal static (Pawn?,Dictionary<Thing,int>?) Begin(Pawn pawn)
    {
        var old=(worker,costs);
        worker=CargoNetwork.For(pawn.Map).Hoists.Any()?pawn:null;
        costs=worker==null?null:new();
        return old;
    }
    internal static void End((Pawn?,Dictionary<Thing,int>?) old)=>(worker,costs)=old;
    private static int Cost(Thing item)
    {
        if(costs!.TryGetValue(item,out int value))return value;
        float total;
        if(CargoNetwork.OwnerOf(item) is {} main)
            CargoNetwork.For(worker!.Map).BestPort(main,worker,worker.Position,out total);
        else
        {
            using var path=worker!.Map.pathFinder.FindPathNow(worker.Position,item.SpawnedParentOrMe,TraverseParms.For(worker),null,PathEndMode.ClosestTouch);
            total=path.Found?path.TotalCost:float.PositiveInfinity;
        }
        value=float.IsFinite(total)?(int)Math.Min(int.MaxValue-1,total):int.MaxValue;
        costs[item]=value;return value;
    }
    static IEnumerable<MethodBase> TargetMethods()
    {
        var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        foreach(var type in typeof(WorkGiver_DoBill).GetNestedTypes(flags))
            foreach(var method in type.GetMethods(flags))
                if(method.ReturnType==typeof(int)&&
                    (method.Name.StartsWith("<TryFindBestIngredientsInSet_NoMixHelper>")&&method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Thing),typeof(Thing)})||
                     method.Name.StartsWith("<TryFindBestBillIngredientsInSet_AllowMix>")&&method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Thing)})))
                    yield return method;
    }
    static bool Prefix(object[] __args,ref int __result)
    {
        if(worker==null)return true;
        __result=__args.Length==1?Cost((Thing)__args[0]):Cost((Thing)__args[0]).CompareTo(Cost((Thing)__args[1]));
        return false;
    }
}
