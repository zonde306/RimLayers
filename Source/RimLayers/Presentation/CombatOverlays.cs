using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Presentation;

[HarmonyPatch]
internal static class LayerLineOverlayPatch
{
    static IEnumerable<MethodBase> TargetMethods()=>typeof(GenDraw).GetMethods(BindingFlags.Public|BindingFlags.Static)
        .Where(m=>m.Name==nameof(GenDraw.DrawLineBetween) && m.GetParameters()[0].ParameterType==typeof(Vector3));
    static bool Prefix(ref Vector3 __0,ref Vector3 __1)
    {
        var owner=LayerView.Current;
        if(owner==null)return true;
        bool visible=owner.IsViewedCell(__0.ToIntVec3())||owner.IsViewedCell(__1.ToIntVec3())||
            LowerFloorBackdrop.Exposed(owner,__0.ToIntVec3())||LowerFloorBackdrop.Exposed(owner,__1.ToIntVec3());
        if(!visible)return false;
        __0=LowerFloorBackdrop.Project(owner,__0);__1=LowerFloorBackdrop.Project(owner,__1);return true;
    }
}
[HarmonyPatch]
internal static class LayerRangeOverlayPatch
{
    static IEnumerable<MethodBase> TargetMethods()=>typeof(GenDraw).GetMethods(BindingFlags.Public|BindingFlags.Static)
        .Where(m=>m.Name==nameof(GenDraw.DrawRadiusRing) && m.GetParameters()[0].ParameterType==typeof(IntVec3));
    static bool Prefix(IntVec3 __0)=>LayerView.Current is not {} owner || owner.IsViewedCell(__0);
}
[HarmonyPatch(typeof(SelectionDrawer),nameof(SelectionDrawer.DrawSelectionBracketFor))]
internal static class LayerSelectionBracketPatch
{
    static bool Prefix(object obj)
    {
        var owner=LayerView.Current;
        return owner==null || obj is not Thing thing || thing.MapHeld!=owner.map || owner.IsViewedCell(thing.PositionHeld);
    }
}

// The highlight is a presentation-only value; the targeting source keeps the real Thing.
[HarmonyPatch(typeof(GenDraw),nameof(GenDraw.DrawTargetHighlight))]
internal static class BackgroundTargetHighlightPatch
{
    static void Prefix(ref LocalTargetInfo targ)
    {
        var owner=LayerView.Current;
        if(owner!=null&&targ.Thing is {} thing&&thing.MapHeld==owner.map&&LowerFloorBackdrop.Exposed(owner,thing.PositionHeld))
            targ=new LocalTargetInfo(LowerFloorBackdrop.Project(owner,thing.DrawPos).ToIntVec3());
    }
}
