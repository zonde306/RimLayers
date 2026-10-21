using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Mountains;

public sealed class Dialog_MountainMigration:Window
{
    private readonly LayerMapComponent owner;
    private readonly MountainService.Preview preview;
    private static Dialog_MountainMigration? active;
    public override Vector2 InitialSize=>new(510,290);
    public Dialog_MountainMigration(LayerMapComponent owner)
    {
        this.owner=owner;preview=owner.Mountains.PreviewMigration(owner.ViewedLayer);
        forcePause=true;doCloseX=true;closeOnClickedOutside=true;absorbInputAroundWindow=true;active=this;
    }
    public override void PostClose(){base.PostClose();if(active==this)active=null;}
    public override void DoWindowContents(Rect rect)
    {
        Text.Font=GameFont.Medium;Widgets.Label(new Rect(0,0,rect.width,35),"RL_MountainMigration".Translate());Text.Font=GameFont.Small;
        Widgets.Label(new Rect(0,45,rect.width,140),"RL_MountainMigrationHelp".Translate(preview.Layer+1,preview.Eligible.Count,preview.Skipped.Count));
        if(Widgets.ButtonText(new Rect(0,rect.height-38,140,32),"CancelButton".Translate()))Close();
        GUI.enabled=preview.Eligible.Count>0;
        if(Widgets.ButtonText(new Rect(rect.width-180,rect.height-38,180,32),"RL_MountainMigrationApply".Translate()))
        {
            Close();
            LongEventHandler.QueueLongEvent(()=>
            {
                if(owner.map.Disposed||!Find.Maps.Contains(owner.map))return;
                try{int n=owner.Mountains.CommitMigration(preview);Messages.Message("RL_MountainMigrated".Translate(n),RimWorld.MessageTypeDefOf.TaskCompletion,false);}
                catch(Exception e){Log.Error("[RimLayers] Mountain migration rolled back: "+e);Messages.Message("RL_MountainMigrationFailed".Translate(),RimWorld.MessageTypeDefOf.RejectInput,false);}
            },"RL_MountainMigration",false,null);
        }
        GUI.enabled=true;
    }
    internal static void Draw(LayerMapComponent o)
    {
        if(active?.owner!=o||active.preview.Layer!=o.ViewedLayer)return;
        GenDraw.DrawFieldEdges(active.preview.Eligible,Color.green);
        GenDraw.DrawFieldEdges(active.preview.Skipped,Color.yellow);
    }
}
