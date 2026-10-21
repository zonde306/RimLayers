using RimLayers.Core;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Recovery;

public sealed class Dialog_EntityRecovery:Window
{
    private Vector2 scroll;
    private RecoveryRecord? selected;
    private Map? targetMap;
    private int targetLayer;
    private string x="0",z="0";
    private float listHeight;
    public override Vector2 InitialSize=>new(820,Math.Min(700,UI.screenHeight-50));
    public Dialog_EntityRecovery(){forcePause=true;doCloseX=true;doCloseButton=true;absorbInputAroundWindow=true;}
    private static string T(string key)=>("RL_Recovery"+key).Translate();
    public override void DoWindowContents(Rect rect)
    {
        var service=EntityRecoveryService.Current;
        if(service==null){Widgets.Label(rect,T("NoGame"));return;}
        var font=Text.Font;Text.Font=GameFont.Medium;Widgets.Label(new Rect(0,0,rect.width,34),T("Title"));Text.Font=font;
        Widgets.Label(new Rect(0,38,rect.width,42),"RL_RecoveryCounts".Translate(service.Records.Count,service.StoredCount));
        var outer=new Rect(0,82,rect.width,Math.Max(70,rect.height-300));
        var view=new Rect(0,0,outer.width-20,Math.Max(outer.height,listHeight));
        Widgets.BeginScrollView(outer,ref scroll,view);
        float y=0;
        try
        {
            foreach(var record in service.Records.ToArray())
            {
                string label=record.Thing?.LabelCap.ToString()??T("Missing");
                string details="RL_RecoverySource".Translate(record.MapName,record.MapId,record.Origin)+"\n"+
                    T("Reason"+record.Reason)+" · "+T("Stage"+record.Stage)+(record.ManualOnly?" · "+T("ManualOnly"):"");
                float height=Math.Max(72,Text.CalcHeight(details,view.width-210)+36);
                var row=new Rect(0,y,view.width,height);if(record==selected)Widgets.DrawHighlightSelected(row);
                if(Widgets.ButtonText(new Rect(6,y+4,view.width-210,28),label))Select(record,service);
                Widgets.Label(new Rect(8,y+36,view.width-210,height-36),details);
                if(Widgets.ButtonText(new Rect(view.width-192,y+6,88,28),T("Locate")))Locate(record,service);
                if(Widgets.ButtonText(new Rect(view.width-98,y+6,90,28),T("Retry")))service.Retry(record);
                y+=height+8;
            }
            if(service.Records.Count==0)Widgets.Label(new Rect(8,8,view.width-16,48),T("Empty"));
            listHeight=y;
        }
        finally{Widgets.EndScrollView();}
        float bottom=outer.yMax+10;
        if(selected==null||!service.Records.Contains(selected))
        {selected=null;Widgets.Label(new Rect(0,bottom,rect.width,100),T("SelectHelp"));return;}
        Widgets.Label(new Rect(0,bottom,rect.width,26),T("ChooseLanding"));bottom+=30;
        if(Widgets.ButtonText(new Rect(0,bottom,rect.width*.6f-8,30),targetMap?.Parent.Label??T("ChooseMap")))
        {
            var original=service.OriginalMap(selected);
            var maps=original!=null?new[]{original}:Find.Maps.Where(m=>!m.Disposed).ToArray();
            Find.WindowStack.Add(new FloatMenu(maps.Select(m=>new FloatMenuOption(m.Parent.Label,()=>{targetMap=m;targetLayer=0;})).ToList()));
        }
        var owner=LayerMapComponent.For(targetMap);
        if(owner!=null)
        {
            if(targetLayer>=owner.Layout!.LayerCount)targetLayer=0;
            if(Widgets.ButtonText(new Rect(rect.width*.6f,bottom,rect.width*.4f,30),owner.LevelName(targetLayer)))
                Find.WindowStack.Add(new FloatMenu(owner.Layout.OrderedLayers.Select(id=>new FloatMenuOption(owner.LevelName(id),()=>targetLayer=id)).ToList()));
        }
        else if(targetMap!=null)Widgets.Label(new Rect(rect.width*.6f,bottom,rect.width*.4f,30),"RL_SurfaceLevel".Translate(1));
        bottom+=38;
        Widgets.Label(new Rect(0,bottom,24,28),"X");x=Widgets.TextField(new Rect(28,bottom,100,28),x);
        Widgets.Label(new Rect(144,bottom,24,28),"Z");z=Widgets.TextField(new Rect(172,bottom,100,28),z);
        if(Widgets.ButtonText(new Rect(286,bottom,rect.width-286,28),T("Release")))
        {
            if(targetMap==null||!int.TryParse(x,out int px)||!int.TryParse(z,out int pz)||
                (owner!=null?!owner.Layout!.Contains(new(targetLayer,px,pz)):!new IntVec3(px,0,pz).InBounds(targetMap)))
            {Messages.Message(T("IllegalLanding"),MessageTypeDefOf.RejectInput,false);return;}
            var at=owner!=null?owner.ToStorage(new(targetLayer,px,pz)):new IntVec3(px,0,pz);var record=selected;var map=targetMap;
            if(record.MapId!=map.uniqueID)
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RL_RecoveryCrossMapQuestion".Translate(record.Thing?.LabelCap,map.Parent.Label),()=>service.TryRelease(record,map,at,true)));
            else service.TryRelease(record,map,at);
        }
        Widgets.Label(new Rect(0,bottom+36,rect.width,52),T("LandingHelp"));
    }
    private void Select(RecoveryRecord record,EntityRecoveryService service)
    {
        selected=record;targetMap=service.OriginalMap(record);targetLayer=0;
        if(LayerMapComponent.For(targetMap) is {} owner&&owner.TryDecode(record.Origin,out var at))
        {targetLayer=at.LayerId;x=at.X.ToString();z=at.Z.ToString();}
        else {x="0";z="0";}
    }
    private static void Locate(RecoveryRecord record,EntityRecoveryService service)
    {
        var map=service.OriginalMap(record);
        if(LayerMapComponent.For(map) is not {} owner||!owner.TryDecode(record.Origin,out _))
        {Messages.Message(T("CannotLocate"),MessageTypeDefOf.RejectInput,false);return;}
        LayerView.ShowTarget(record.Origin,map!);CameraJumper.TryJump(record.Origin,map,CameraJumper.MovementMode.Cut);
    }
}
