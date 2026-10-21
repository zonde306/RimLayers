using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Presentation;

internal static class DriftingMist
{
    private static readonly Dictionary<int,Material> materials=new();
    internal static int LastPatches;
    internal static Vector3 Center(int x,int z,int depth,float time)=>new(x*32f+time*.16f+Mathf.Sin(x*12.7f+z*8.3f+depth)*5f,
        -depth*3+.5f,z*28f+time*.045f+Mathf.Cos(x*5.1f-z*11.3f+depth)*4f);
    internal static void Draw(LayerMapComponent owner,int depth,float time)
    {
        LastPatches=0;
        if(!RimLayersMod.Settings.Mist || owner.map.Tile.Layer.Def.isSpace)return; // Vacuum uses the native orbital backdrop, not atmospheric mist.
        if(!materials.TryGetValue(depth,out var material))
        {
            material=new Material(MaterialPool.MatFrom("Atmosphere/ThinMist",ShaderDatabase.Transparent,new Color(.92f,.96f,1f,.26f)))
                {renderQueue=BackgroundPass.QueueBase(depth)+80};
            materials.Add(depth,material);
        }
        var view=Find.CameraDriver.CurrentViewRect;
        int offset=owner.ViewedLayer*owner.Layout!.Stride;
        int minX=Mathf.FloorToInt((view.minX-time*.16f-40)/32),maxX=Mathf.CeilToInt((view.maxX-time*.16f+40)/32);
        int minZ=Mathf.FloorToInt((view.minZ-offset-time*.045f-32)/28),maxZ=Mathf.CeilToInt((view.maxZ-offset-time*.045f+32)/28);
        LastPatches=0;
        for(int x=minX;x<=maxX;x++)for(int z=minZ;z<=maxZ;z++)
        {
            var center=Center(x,z,depth,time);center.z+=offset;
            float angle=Mathf.Sin(x*7.1f+z*9.7f+depth)*22;
            Graphics.DrawMesh(MeshPool.plane10,Matrix4x4.TRS(center,Quaternion.Euler(0,angle,0),new Vector3(54,1,38)),material,0);
            LastPatches++;
        }
    }
}
