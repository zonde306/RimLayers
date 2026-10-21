using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Presentation;

/// <summary>Project real lower-floor meshes and actors for presentation only.
/// Native fog remains intact; no entity position, map state, AI or collision is changed.</summary>
internal static class LowerFloorBackdrop
{
    private static readonly AccessTools.FieldRef<Section,List<SectionLayer>> Layers=AccessTools.FieldRefAccess<Section,List<SectionLayer>>("layers");
    private static readonly AccessTools.FieldRef<Section,bool> Dirty=AccessTools.FieldRefAccess<Section,bool>("anyLayerDirty");
    private static readonly Action<Section> Regenerate=AccessTools.MethodDelegate<Action<Section>>(AccessTools.Method(typeof(Section),"RegenerateDirtyLayers"));
    [ThreadStatic] internal static bool Picking;
    internal static int LastMeshes,LastActors,LastCorpses,LastDynamic;
    internal static bool Exposed(LayerMapComponent owner,IntVec3 actual)
    {
        if(!owner.TryDecode(actual,out var target)||owner.Elevation(target.LayerId)>=owner.Elevation(owner.ViewedLayer))return false;
        foreach(int layer in new[]{target.LayerId}.Concat(owner.Layout!.Above(target.LayerId)).Where(l=>owner.Elevation(l)<owner.Elevation(owner.ViewedLayer)))
            if(owner.map.roofGrid.RoofAt(owner.ToStorage(new LayerCell(layer,target.X,target.Z)))!=null)return false;
        foreach(int layer in owner.Layout!.Above(target.LayerId).Where(l=>owner.Elevation(l)<=owner.Elevation(owner.ViewedLayer)))
            if(owner.Structure.HasSurface(owner.ToStorage(new LayerCell(layer,target.X,target.Z))))return false;
        return true;
    }
    internal static Vector3 Project(LayerMapComponent owner,Vector3 actual)
    {
        if(owner.TryDecode(actual.ToIntVec3(),out var logical))actual.z+=(owner.ViewedLayer-logical.LayerId)*owner.Layout!.Stride;
        return actual;
    }
    internal static void Draw(LayerMapComponent owner)
    {
        LastMeshes=LastActors=LastCorpses=LastDynamic=0;RoofSurfaces.LastTiles=0;
        if(!RimLayersMod.Settings.LowerBackground || !owner.Layout!.Below(owner.ViewedLayer).Any())return;
        var layout=owner.Layout!;var view=Find.CameraDriver.CurrentViewRect;
        int localMin=view.minZ-owner.ViewedLayer*layout.Stride;
        int localMax=view.maxZ-owner.ViewedLayer*layout.Stride;
        foreach(int floor in layout.Below(owner.ViewedLayer).Reverse())
        {
            int depth=owner.Elevation(owner.ViewedLayer)-owner.Elevation(floor);
            float offset=(owner.ViewedLayer-floor)*layout.Stride;
            var transform=Matrix4x4.TRS(new Vector3(0,-depth*3,offset),Quaternion.identity,new Vector3(1,.001f,1));
            for(int x=Math.Max(0,view.minX/17);x<=Math.Min((layout.Width-1)/17,view.maxX/17);x++)
                for(int z=Math.Max(0,localMin/17);z<=Math.Min((layout.Height-1)/17,localMax/17);z++)
                {
                    var section=owner.map.mapDrawer.SectionAt(new IntVec3(x*17,0,floor*layout.Stride+z*17));
                    if(section==null)continue;
                    if(Dirty(section))Regenerate(section);
                    foreach(var layer in Layers(section))
                    {
                        if(!layer.Visible||layer is SectionLayer_Dynamic)continue;
                        string name=layer.GetType().Name;
                        if(name is not ("SectionLayer_Terrain" or "SectionLayer_ThingsGeneral" or "SectionLayer_FogOfWar" or "SectionLayer_Snow" or "SectionLayer_Pollution"))continue;
                        foreach(var mesh in layer.subMeshes)
                            if(mesh.finalized&&!mesh.disabled)
                            {Graphics.DrawMesh(mesh.mesh,transform,BackgroundPass.MaterialFor(mesh.material,depth),mesh.renderLayer);LastMeshes++;}
                    }
                }
            BackgroundPass.Begin(owner,floor);
            try
            {
            foreach(var thing in owner.map.dynamicDrawManager.DrawThings)
            {
                if(thing is Projectile or Skyfaller or PawnFlyer||!owner.TryDecode(thing.Position,out var c)||c.LayerId!=floor||!Exposed(owner,thing.Position)||thing.Position.Fogged(owner.map))continue;
                if(thing is Pawn pawn&&pawn.IsHiddenFromPlayer())continue;
                if(!view.Contains(Project(owner,thing.DrawPos).ToIntVec3()))continue;
                // Keep native draw inputs in real coordinates. The scoped draw bridge
                // projects final meshes, including lying bodies and carried equipment.
                thing.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized,thing.DrawPos);
                thing.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw,thing.DrawPos);
                thing.DrawNowAt(thing.DrawPos);
                LastDynamic++;
                if(thing is Pawn)LastActors++;
                if(thing is Corpse)LastCorpses++;
            }
            }
            finally{BackgroundPass.End();}
            RoofSurfaces.Draw(owner,floor);
            DriftingMist.Draw(owner,depth,Time.realtimeSinceStartup);
        }
    }
    internal static IEnumerable<Thing> Pick(LayerMapComponent owner,Vector3 click,float radius,TargetingParameters parameters,ITargetingSource source)
    {
        if(!RimLayersMod.Settings.LowerBackground || !owner.TryDecode(click.ToIntVec3(),out var here))return Array.Empty<Thing>();
        var result=new List<Thing>();
        Picking=true;
        try
        {
            foreach(int floor in owner.Layout!.Below(owner.ViewedLayer))
            {
                var cell=owner.ToStorage(new LayerCell(floor,here.X,here.Z));
                if(!Exposed(owner,cell))continue;
                var local=click;local.z-=(owner.ViewedLayer-floor)*owner.Layout!.Stride;
                result.AddRange(GenUI.ThingsUnderMouse(local,radius,parameters,source)
                    .Where(t=>owner.TryDecode(t.PositionHeld,out var c)&&c.LayerId==floor&&!t.PositionHeld.Fogged(owner.map)&&Exposed(owner,t.PositionHeld)));
            }
        }
        finally{Picking=false;}
        return result.Distinct();
    }
}

[HarmonyPatch(typeof(MapDrawer),nameof(MapDrawer.DrawMapMesh))]
internal static class LowerFloorBackdropPatch
{
    static void Prefix(Map ___map)
    {
        var owner=LayerView.Current;
        if(owner!=null&&owner.map==___map&&!WorldComponent_GravshipController.GravshipRenderInProgess)LowerFloorBackdrop.Draw(owner);
    }
}
