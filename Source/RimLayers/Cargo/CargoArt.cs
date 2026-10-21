using UnityEngine;
using Verse;
namespace RimLayers.Cargo;

/// <summary>Code-native, outlined hoist artwork shared by the menu, ghost and in-world frame.</summary>
[StaticConstructorOnStartup]
internal static class CargoArt
{
    private static readonly Texture2D manual=Create(false),electric=Create(true);
    internal static Texture2D Texture(bool powered)=>powered?electric:manual;
    internal static Material Material(bool powered)=>MaterialPool.MatFrom(Texture(powered),ShaderDatabase.Cutout,Color.white);
    private static Texture2D Create(bool powered)
    {
        const int size=96;
        var pixels=new Color[size*size];
        Color ink=new Color(.10f,.12f,.12f),shade=new Color(.20f,.23f,.22f);
        var frame=powered?new Color(.48f,.57f,.59f):new Color(.55f,.38f,.22f);
        var light=powered?new Color(.69f,.74f,.73f):new Color(.72f,.54f,.33f);
        var wood=new Color(.63f,.46f,.27f);var rope=new Color(.85f,.76f,.53f);
        void Box(int x,int y,int w,int h,Color c){for(int j=y;j<y+h;j++)for(int i=x;i<x+w;i++)if(i>=0&&i<size&&j>=0&&j<size)pixels[j*size+i]=c;}
        void Panel(int x,int y,int w,int h,Color c){Box(x,y,w,h,ink);Box(x+2,y+2,w-4,h-4,c);}
        void Disc(int x,int y,int radius,Color c){for(int j=y-radius;j<=y+radius;j++)for(int i=x-radius;i<=x+radius;i++)if((i-x)*(i-x)+(j-y)*(j-y)<=radius*radius&&i>=0&&i<size&&j>=0&&j<size)pixels[j*size+i]=c;}
        Panel(7,7,82,81,shade);
        Panel(7,7,11,81,frame);Box(10,11,2,73,light);
        Panel(78,7,11,81,frame);Box(81,11,2,73,light);
        Panel(7,76,82,12,frame);Box(11,83,72,2,light);
        Panel(21,13,54,53,wood);
        for(int y=20;y<62;y+=9){Box(24,y,48,2,new Color(.39f,.27f,.16f));Box(24,y+2,48,1,new Color(.77f,.59f,.35f));}
        Panel(20,12,56,8,light);Panel(20,60,56,8,light);
        Box(28,65,3,14,rope);Box(65,65,3,14,rope);
        if(powered)
        {
            Panel(57,71,24,20,frame);
            for(int x=61;x<77;x+=4)Box(x,76,2,10,shade);
            Panel(23,73,16,14,shade);Box(27,77,7,5,new Color(.31f,.73f,.81f));
        }
        else
        {
            Disc(48,80,10,ink);Disc(48,80,7,rope);Disc(48,80,3,shade);
            Box(48,79,20,3,ink);Panel(64,74,6,11,light);
        }
        foreach(int x in new[]{12,83})foreach(int y in new[]{13,80}){Disc(x,y,2,ink);Box(x,y,1,1,light);}
        var texture=new Texture2D(size,size,TextureFormat.RGBA32,false){name=powered?"RimLayersElectricHoist":"RimLayersManualHoist",filterMode=FilterMode.Bilinear};
        texture.SetPixels(pixels);texture.Apply();return texture;
    }
}
public sealed class Graphic_Cargo:Graphic_Single
{
    public override Graphic GetColoredVersion(Shader shader,Color tint,Color secondary)=>GraphicDatabase.Get<Graphic_Cargo>(path,shader,drawSize,tint,secondary,data);
    public override void Init(GraphicRequest req)
    {
        data=req.graphicData;path=req.path;color=req.color;colorTwo=req.colorTwo;drawSize=req.drawSize;
        mat=MaterialPool.MatFrom(CargoArt.Texture(req.path.Contains("Electric")),req.shader,req.color);
    }
}
