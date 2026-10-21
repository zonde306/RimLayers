using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;
namespace RimLayers.Presentation;

/// <summary>Opaque, cached roof surfaces belong to presentation, not walkability or RoofGrid.</summary>
internal static class RoofSurfaces
{
    private sealed class State
    {
        internal readonly Dictionary<(int Floor,int X,int Z),Mesh[]> Meshes=new();
        internal State(LayerMapComponent owner)=>owner.Changed+=(c,k)=>{if((k&LayerChangeKind.Structure)!=0)Invalidate(owner,owner.ToStorage(c));};
    }
    private static readonly ConditionalWeakTable<LayerMapComponent,State> states=new();
    private static readonly Dictionary<(int Depth,int Type),Material> materials=new();
    internal static int LastTiles;
    internal static void Clear(LayerMapComponent owner)
    {if(states.TryGetValue(owner,out var s)){foreach(var meshes in s.Meshes.Values)foreach(var mesh in meshes)UnityEngine.Object.Destroy(mesh);s.Meshes.Clear();}}
    internal static void Invalidate(LayerMapComponent owner,IntVec3 cell)
    {
        if(!states.TryGetValue(owner,out var s)||!owner.TryDecode(cell,out var c))return;
        // A ship ownership change can alter the roof finish in this local column too.
        foreach(var key in s.Meshes.Keys.Where(k=>k.X==c.X/17&&k.Z==c.Z/17).ToArray())
        {foreach(var mesh in s.Meshes[key])UnityEngine.Object.Destroy(mesh);s.Meshes.Remove(key);}
    }
    private static Mesh Build(LayerMapComponent o,int floor,int x,int z,int type)
    {
        var vertices=new List<Vector3>();var uv=new List<Vector2>();var triangles=new List<int>();
        for(int dx=x*17;dx<Math.Min((x+1)*17,o.Layout!.Width);dx++)
        for(int dz=z*17;dz<Math.Min((z+1)*17,o.Layout.Height);dz++)
        {
            var c=o.ToStorage(new(floor,dx,dz));
            if(o.map.roofGrid.RoofAt(c)==null||c.Fogged(o.map)||(o.map.roofGrid.RoofAt(c).isNatural?2:o.Ships.OwnerAt(c)!=null?1:0)!=type)continue;
            int i=vertices.Count;float sz=c.z;
            vertices.AddRange(new[]{new Vector3(dx,.6f,sz),new Vector3(dx,.6f,sz+1),new Vector3(dx+1,.6f,sz+1),new Vector3(dx+1,.6f,sz)});
            uv.AddRange(new[]{new Vector2(0,0),new Vector2(0,1),new Vector2(1,1),new Vector2(1,0)});
            triangles.AddRange(new[]{i,i+1,i+2,i,i+2,i+3});
        }
        var result=new Mesh{name="RimLayers roof surface"};result.vertices=vertices.ToArray();result.uv=uv.ToArray();result.triangles=triangles.ToArray();result.RecalculateBounds();return result;
    }
    internal static void Draw(LayerMapComponent o,int floor)
    {
        var s=states.GetValue(o,k=>new State(k));var l=o.Layout!;int depth=o.Elevation(o.ViewedLayer)-o.Elevation(floor);
        var view=Find.CameraDriver.CurrentViewRect;int offset=o.ViewedLayer*l.Stride;
        var transform=Matrix4x4.TRS(new Vector3(0,-depth*3+.6f,(o.ViewedLayer-floor)*l.Stride),Quaternion.identity,new Vector3(1,.001f,1));
        for(int x=Math.Max(0,view.minX/17);x<=Math.Min((l.Width-1)/17,view.maxX/17);x++)
        for(int z=Math.Max(0,(view.minZ-offset)/17);z<=Math.Min((l.Height-1)/17,(view.maxZ-offset)/17);z++)
        {
            var key=(floor,x,z);if(!s.Meshes.TryGetValue(key,out var meshes))s.Meshes[key]=meshes=new[]{Build(o,floor,x,z,0),Build(o,floor,x,z,1),Build(o,floor,x,z,2)};
            for(int type=0;type<3;type++)
            {
                var mesh=meshes[type];if(mesh.vertexCount==0)continue;
                if(!materials.TryGetValue((depth,type),out var mat))
                {mat=new Material(type==2?MaterialPool.MatFrom(DefDatabase<ThingDef>.GetNamed("Granite").building.naturalTerrain.texturePath,ShaderDatabase.Cutout,DefDatabase<ThingDef>.GetNamed("Granite").building.naturalTerrain.color):MaterialPool.MatFrom(type==0?"Roof/RoofSurface":"Roof/ShipRoofSurface",ShaderDatabase.Cutout)){renderQueue=BackgroundPass.QueueBase(depth)+75};materials.Add((depth,type),mat);}
                Graphics.DrawMesh(mesh,transform,mat,0);LastTiles+=mesh.vertexCount/4;
            }
        }
    }
}
[HarmonyPatch(typeof(FogGrid),"UnfogWorker")]
internal static class RoofSurfaceUnfog{static void Postfix(IntVec3 c,Map ___map){if(LayerMapComponent.For(___map) is {} o)RoofSurfaces.Invalidate(o,c);}}
[HarmonyPatch(typeof(FogGrid),nameof(FogGrid.Refog))]
internal static class RoofSurfaceRefog{static void Postfix(CellRect rect,Map ___map){if(LayerMapComponent.For(___map) is {} o)foreach(var c in rect)RoofSurfaces.Invalidate(o,c);}}

[HarmonyPatch(typeof(Map),nameof(Map.Dispose))]
internal static class RoofSurfaceMapDisposal
{static void Prefix(Map __instance){if(LayerMapComponent.For(__instance) is {} o)RoofSurfaces.Clear(o);}}
