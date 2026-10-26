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
[HarmonyPatch(typeof(SelectionDrawer),nameof(SelectionDrawer.DrawSelectionBracketFor), new[] { typeof(object), typeof(Material) })]
internal static class LayerSelectionBracketPatch
{
    private static readonly Vector3[] localBracketLocs = new Vector3[4];
    private static Material? bracketMatCached;
    private static Material BracketMat => bracketMatCached ??= MaterialPool.MatFrom("UI/Overlays/SelectionBracket", ShaderDatabase.MetaOverlay);

    static bool Prefix(object obj, Material overrideMat)
    {
        var owner = LayerView.Current;
        if (owner == null || obj is not Thing thing || thing.MapHeld != owner.map) return true;
        if (owner.IsViewedCell(thing.PositionHeld)) return true;

        if (LowerFloorBackdrop.Exposed(owner, thing.PositionHeld))
        {
            if (!thing.DrawPosHeld.HasValue) return false;
            Vector3 carryDrawPos = LowerFloorBackdrop.Project(owner, thing.DrawPosHeld.Value);
            var bracketMat = overrideMat ?? BracketMat;

            SelectionDrawerUtility.CalculateSelectionBracketPositionsWorld(localBracketLocs, thing, carryDrawPos, thing.RotatedSize.ToVector2(), SelectionDrawer.SelectTimes, Vector2.one, 1f, thing.def.deselectedSelectionBracketFactor);
            float num = thing.MultipleItemsPerCellDrawn() ? 0.8f : 1f;
            float num2 = 1f;
            CameraDriver cameraDriver = Find.CameraDriver;
            float num3 = Mathf.Clamp01(Mathf.InverseLerp(cameraDriver.config.sizeRange.max * 0.85f, cameraDriver.config.sizeRange.max, cameraDriver.ZoomRootSize));
            if (thing is Pawn)
            {
                if (thing.def.Size == IntVec2.One) num *= Mathf.Min(1f + num3 / 2f, 2f);
                else num2 = Mathf.Min(1f + num3 / 2f, 2f);
            }
            int num4 = 0;
            for (int i = 0; i < 4; i++)
            {
                Quaternion q = Quaternion.AngleAxis(num4, Vector3.up);
                Vector3 pos = (localBracketLocs[i] - carryDrawPos) * num + carryDrawPos;
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, q, new Vector3(num, 1f, num) * num2), bracketMat, 0);
                num4 -= 90;
            }
        }
        return false;
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
