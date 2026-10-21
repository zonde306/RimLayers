using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Presentation;

// Native terrains order by renderQueue (2000 + precedence). Moving a mesh lower
// does not place every terrain/material behind every other terrain. Reserve a
// separate queue band per background floor, without mutating shared materials.
internal static class BackgroundPass
{
    private static readonly Dictionary<(Material,int),Material> materials=new();
    [ThreadStatic] private static int depth;
    [ThreadStatic] private static Matrix4x4 transform;
    internal static bool Active=>depth>0;
    internal static int QueueBase(int distance)=>SettingsMath.BackgroundQueueBase(distance);
    internal static Material MaterialFor(Material source,int distance)
    {
        if(!materials.TryGetValue((source,distance),out var material))
        {
            material=new Material(source){name=source.name+" (RimLayers background)",renderQueue=QueueBase(distance)+Math.Clamp((source.renderQueue-2000)/30,0,70)};
            materials.Add((source,distance),material);
        }
        return material;
    }
    internal static void Begin(LayerMapComponent owner,int floor)
    {
        depth=owner.Elevation(owner.ViewedLayer)-owner.Elevation(floor);
        transform=Matrix4x4.TRS(new Vector3(0,-depth*3,(owner.ViewedLayer-floor)*owner.Layout!.Stride),Quaternion.identity,new Vector3(1,.001f,1));
    }
    internal static void End()=>depth=0;
    internal static void Project(ref Matrix4x4 matrix,ref Material mat)
    {if(Active){matrix=transform*matrix;mat=MaterialFor(mat,depth);}}
}

// The two managed submission endpoints are disjoint: MaterialPropertyBlock
// overloads funnel into DrawMeshCore; DrawPropertyBlock has its own final overload.
// This also catches equipment/custom PostDraw calls that bypass GenDraw entirely.
// Immediate DrawMeshNow (portrait/atlas rendering) is deliberately not patched.
[HarmonyPatch]
internal static class BackgroundMeshSubmissionPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Graphics),"DrawMeshCore");
        yield return AccessTools.Method(typeof(Graphics),nameof(Graphics.DrawMesh),new[]{typeof(Mesh),typeof(Matrix4x4),typeof(Material),typeof(int),typeof(Camera),typeof(int),typeof(Waylith.Rendering.DrawPropertyBlock),typeof(UnityEngine.Rendering.ShadowCastingMode),typeof(bool),typeof(int)});
    }
    [HarmonyPriority(Priority.First)]
    static void Prefix(ref Matrix4x4 matrix,ref Material material,ref int renderQueue)
    {
        if(RimLayers.Ships.ShipCutsceneSurface.Offset!=0)
            matrix=Matrix4x4.Translate(new Vector3(0,0,RimLayers.Ships.ShipCutsceneSurface.Offset))*matrix;
        if(!BackgroundPass.Active)return;
        BackgroundPass.Project(ref matrix,ref material);
        renderQueue=material.renderQueue;
    }
}
