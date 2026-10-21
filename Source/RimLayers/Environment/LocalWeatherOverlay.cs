using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Environment;

/// <summary>Weather meshes are masked by the same local concentrations used for health and shooting.</summary>
internal static class LocalWeatherOverlay
{
    private sealed class Geometry
    {
        internal long Revision=-1;
        internal int Layer=-1,Tick=-1,MinX,MaxX,MinZ,MaxZ;
        internal Mesh?[] Meshes=new Mesh?[16];
    }
    private sealed class State
    {
        internal readonly Dictionary<int,Geometry> Geometry=new();
        internal readonly Dictionary<(Material,int),Material> Materials=new();
        internal readonly Dictionary<int,Material> Residuals=new();
    }
    private static readonly ConditionalWeakTable<LayerMapComponent,State> states=new();
    private static readonly Dictionary<string,int> modes=new(StringComparer.Ordinal)
    {
        ["WeatherOverlay_Fog"]=0,["WeatherOverlay_FogBlind"]=0,
        ["WeatherOverlay_Fallout"]=1,["WeatherOverlay_NoxiousHaze"]=2,
        ["VolcanicAshOverlay"]=3,["WeatherOverlay_GrayPallFog"]=4,["WeatherOverlay_GrayPallAsh"]=4,
        ["WeatherOverlay_DeathpallFog"]=5,["WeatherOverlay_DeathpallAshes"]=5,
        ["WeatherOverlay_Rain"]=6,["WeatherOverlay_TorrentialRain"]=6,["WeatherOverlay_ToxRain"]=6,
        ["WeatherOverlay_SnowGentle"]=6,["WeatherOverlay_SnowHard"]=6,["WeatherOverlay_SnowBlizzard"]=6,
        ["WeatherOverlay_BloodRain"]=6,["WeatherOverlay_BloodFog"]=6,["WeatherOverlay_DeepBloodFog"]=6,
        ["GlowSporeOverlay"]=6,["WeatherOverlay_SandHard"]=7
    };
    internal static bool TryMode(SkyOverlay overlay,out int mode)=>modes.TryGetValue(overlay.GetType().Name,out mode)&&
        overlay.GetType().Assembly==typeof(SkyOverlay).Assembly;
    internal static void Clear(LayerMapComponent owner)
    {
        if(!states.TryGetValue(owner,out var state))return;
        foreach(var g in state.Geometry.Values)foreach(var mesh in g.Meshes)if(mesh!=null)UnityEngine.Object.Destroy(mesh);
        foreach(var material in state.Materials.Values)UnityEngine.Object.Destroy(material);
        foreach(var material in state.Residuals.Values)UnityEngine.Object.Destroy(material);
        states.Remove(owner);
    }
    internal static void DrawResiduals(LayerMapComponent owner)
    {
        if(!owner.Environment.Enabled)return;
        for(int mode=0;mode<6;mode++)
        {
            if(!owner.Environment.HasResidual((Atmosphere)mode))continue;
            var state=states.GetOrCreateValue(owner);
            if(!state.Residuals.TryGetValue(mode,out var material))
            {
                material=new Material(new RimWorld.WeatherOverlay_Fog().worldOverlayMat);
                var tint=mode switch
                {
                    1=>new Color(.6f,.7f,.25f,.65f),2=>new Color(.7f,.65f,.3f,.65f),
                    3=>new Color(.55f,.5f,.45f,.65f),4=>new Color(.45f,.45f,.48f,.65f),
                    5=>new Color(.35f,.45f,.38f,.65f),_=>new Color(.8f,.8f,.8f,.65f)
                };
                material.color=tint;state.Residuals[mode]=material;
            }
            Draw(owner,mode,material);
        }
    }
    internal static void Draw(LayerMapComponent owner,int mode,Material material)
    {
        if(material==null)return;
        var state=states.GetOrCreateValue(owner);
        if(!state.Geometry.TryGetValue(mode,out var g))state.Geometry[mode]=g=new();
        var view=Find.CameraDriver.CurrentViewRect;int offset=owner.ViewedLayer*owner.Layout!.Stride;
        int minX=Math.Max(0,view.minX),maxX=Math.Min(owner.Layout.Width-1,view.maxX);
        int minZ=Math.Max(0,view.minZ-offset),maxZ=Math.Min(owner.Layout.Height-1,view.maxZ-offset);
        int tick=Find.TickManager.TicksGame/120;
        if(g.Revision!=owner.Revision||g.Layer!=owner.ViewedLayer||g.Tick!=tick||g.MinX!=minX||g.MaxX!=maxX||g.MinZ!=minZ||g.MaxZ!=maxZ)
        {
            foreach(var mesh in g.Meshes)if(mesh!=null)UnityEngine.Object.Destroy(mesh);
            g.Meshes=new Mesh?[16];
            var vertices=Enumerable.Range(0,16).Select(_=>new List<Vector3>()).ToArray();
            var uv=Enumerable.Range(0,16).Select(_=>new List<Vector2>()).ToArray();
            var indices=Enumerable.Range(0,16).Select(_=>new List<int>()).ToArray();
            int Level(int x,int z)
            {
                var cell=owner.ToStorage(new(owner.ViewedLayer,x,z));if(cell.Fogged(owner.map))return 0;
                float exposure=mode<6?owner.Environment.Concentration(cell,(Atmosphere)mode):
                    mode==6?(owner.Environment.Sky(cell)?1:0):(owner.Environment.WindExposed(cell)?1:0);
                return Math.Clamp((int)Math.Round(exposure*15),0,15);
            }
            for(int z=minZ;z<=maxZ;z++)
            {
                int start=minX;
                while(start<=maxX)
                {
                    int level=Level(start,z),end=start+1;
                    while(end<=maxX&&Level(end,z)==level)end++;
                    if(level>0)
                    {
                        int i=vertices[level].Count;float sz=z+offset;
                        vertices[level].AddRange(new[]{new Vector3(start,0,sz),new Vector3(start,0,sz+1),new Vector3(end,0,sz+1),new Vector3(end,0,sz)});
                        uv[level].AddRange(new[]{new Vector2(start*.1f,z*.1f),new Vector2(start*.1f,(z+1)*.1f),new Vector2(end*.1f,(z+1)*.1f),new Vector2(end*.1f,z*.1f)});
                        indices[level].AddRange(new[]{i,i+1,i+2,i,i+2,i+3});
                    }
                    start=end;
                }
            }
            for(int level=1;level<16;level++)if(vertices[level].Count>0)
            {
                var mesh=new Mesh{name="RimLayers local weather"};mesh.vertices=vertices[level].ToArray();mesh.uv=uv[level].ToArray();mesh.triangles=indices[level].ToArray();mesh.RecalculateBounds();g.Meshes[level]=mesh;
            }
            g.Revision=owner.Revision;g.Layer=owner.ViewedLayer;g.Tick=tick;g.MinX=minX;g.MaxX=maxX;g.MinZ=minZ;g.MaxZ=maxZ;
        }
        for(int level=1;level<16;level++)if(g.Meshes[level] is {} mesh)
        {
            var key=(material,level);
            if(!state.Materials.TryGetValue(key,out var mat))state.Materials[key]=mat=new Material(material);
            mat.CopyPropertiesFromMaterial(material);var color=material.color;color.a*=level/15f;mat.color=color;
            Graphics.DrawMesh(mesh,new Vector3(0,AltitudeLayer.Weather.AltitudeFor(),0),Quaternion.identity,mat,LayerMask.NameToLayer("GravshipExclude"));
        }
    }
}
[HarmonyPatch]
internal static class LocalWeatherOverlayPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(WeatherOverlayDualPanner),nameof(WeatherOverlayDualPanner.DrawOverlay));
        yield return AccessTools.Method(AccessTools.Inner(typeof(RimWorld.GameCondition_VolcanicAsh),"VolcanicAshOverlay"),"DrawOverlay");
        yield return AccessTools.Method(AccessTools.Inner(typeof(RimWorld.GameCondition_BioluminescentSpores),"GlowSporeOverlay"),"DrawOverlay");
    }
    static bool Prefix(SkyOverlay __instance,Map map)
    {
        var owner=LayerMapComponent.For(map);
        if(owner?.Environment.Enabled!=true||!LocalWeatherOverlay.TryMode(__instance,out int mode))return true;
        if(__instance.GetType().Name=="VolcanicAshOverlay")
        {
            foreach(string field in new[]{"CloudLayer","ParticleLayer"})LocalWeatherOverlay.Draw(owner,mode,(Material)AccessTools.Field(__instance.GetType(),field).GetValue(null)!);
        }
        else if(__instance.GetType().Name=="GlowSporeOverlay")
            LocalWeatherOverlay.Draw(owner,mode,(Material)AccessTools.Field(__instance.GetType(),"GlowSporeOverlayWorld").GetValue(null)!);
        else if(__instance is WeatherOverlayDualPanner panner)
        {
            if(panner.worldOverlayMat!=null)LocalWeatherOverlay.Draw(owner,mode,panner.worldOverlayMat);
            if(panner.screenOverlayMat!=null)LocalWeatherOverlay.Draw(owner,mode,panner.screenOverlayMat);
        }
        return false;
    }
}
[HarmonyPatch(typeof(Map),nameof(Map.Dispose))]
internal static class LocalWeatherOverlayDisposal
{
    static void Prefix(Map __instance){if(LayerMapComponent.For(__instance) is {} owner)LocalWeatherOverlay.Clear(owner);}
}
