using RimLayers.Core;
using RimLayers.Mountains;
using UnityEngine;
using Verse;

namespace RimLayers;

internal sealed class SettingsMenu
{
    private int page;
    private Vector2 scroll;
    private readonly float[] heights=new float[5];
    private string exclusions="";
    private bool editingExclusions;
    private static readonly string[] Pages={"Display","Floors","Gameplay","Underground","Recovery"};
    internal void Draw(Rect rect)
    {
        var settings=RimLayersMod.Settings;
        var times=(settings.StairTime,settings.PoweredLiftTime,settings.EmergencyLiftTime);
        float tabWidth=(rect.width-16)/5;
        for(int i=0;i<Pages.Length;i++)
            if(Widgets.ButtonText(new Rect(rect.x+i*(tabWidth+4),rect.y,tabWidth,32),TextFor(Pages[i]),active:i!=page))
            {page=i;scroll=Vector2.zero;editingExclusions=false;}
        Widgets.DrawBoxSolid(new Rect(rect.x+page*(tabWidth+4),rect.y+29,tabWidth,3),new Color(.85f,.72f,.43f));
        var outer=new Rect(rect.x,rect.y+42,rect.width,Math.Max(60,rect.height-88));
        var inner=new Rect(0,0,outer.width-20,Math.Max(outer.height,heights[page]));
        Widgets.BeginScrollView(outer,ref scroll,inner);
        var body=new SettingsBody(inner.width);
        try
        {
            switch(page)
            {
                case 0:
                    body.Note("Immediate");
                    body.Check("LayerTransitions",ref settings.LayerTransitions);
                    body.Check("FloorPanel",ref settings.FloorPanel);
                    body.Check("KeyHints",ref settings.KeyHints);
                    body.Check("LandingMarkers",ref settings.LandingMarkers);
                    body.Check("PlanningBounds",ref settings.PlanningBounds);
                    body.Check("LowerBackground",ref settings.LowerBackground);
                    body.Check("Mist",ref settings.Mist);
                    body.Check("CombatInfo",ref settings.CombatInfo);
                    body.Check("Trajectory",ref settings.Trajectory);
                    body.Note("HiddenPanelHelp");
                    break;
                case 1:
                    body.Note("NewFloors");
                    body.Integer("UpperLimit",ref settings.UpperLimit,0,9);
                    body.Integer("UndergroundLimit",ref settings.UndergroundLimit,0,9);
                    body.Integer("GlobalLimit",ref settings.GlobalLimit,0,100);
                    body.Check("SpaceExpansion",ref settings.SpaceExpansion);
                    body.Note("QuotaHelp");
                    DrawUsage(body);
                    body.Note("SaveCompatibility");
                    break;
                case 2:
                    body.Note("Immediate");
                    body.Slider("StairTime",ref settings.StairTime,.25f,4);
                    body.Slider("PoweredLiftTime",ref settings.PoweredLiftTime,.25f,4);
                    body.Slider("EmergencyLiftTime",ref settings.EmergencyLiftTime,.25f,4);
                    body.Slider("HeightRange",ref settings.HeightRange,0,2);
                    body.Note("Restart");
                    body.Slider("MaterialCost",ref settings.MaterialCost,.25f,4);
                    body.Slider("WorkCost",ref settings.WorkCost,.25f,4);
                    body.Slider("LiftPower",ref settings.LiftPower,.25f,4);
                    body.Note("CurrentDefinitions");
                    break;
                case 3:
                    body.Note("NewUnderground");
                    body.Slider("Minerals",ref settings.Minerals,0,3);
                    body.Check("Caves",ref settings.Caves);
                    body.Check("CaveHives",ref settings.CaveHives);
                    body.Note("UndergroundHelp");
                    break;
                case 4:
                    body.Check("AutoRecovery",ref settings.AutoRecovery);
                    body.Label(TextFor("RecoveryMode"));
                    if(body.Button(settings.RecoveryMode==RecoveryPreference.FallThenReset?"FallThenReset":"ResetFirst"))
                        settings.RecoveryMode=settings.RecoveryMode==RecoveryPreference.FallThenReset?RecoveryPreference.ResetFirst:RecoveryPreference.FallThenReset;
                    body.Note("RecoveryHelp");
                    if(Current.Game!=null&&body.Button("OpenRecoveryList"))Find.WindowStack.Add(new RimLayers.Recovery.Dialog_EntityRecovery());
                    body.Label(TextFor("Exclusions"));
                    if(!editingExclusions){exclusions=string.Join(", ",settings.RecoveryExcludedDefs);editingExclusions=true;}
                    exclusions=Widgets.TextField(body.Row(34),exclusions);
                    settings.RecoveryExcludedDefs=exclusions.Split(new[]{',',';','\n','\r'},StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
                    body.Note("ExclusionsHelp");
                    var owner=LayerMapComponent.For(Find.CurrentMap);
                    if(owner!=null && body.Button("MountainMigration"))Find.WindowStack.Add(new Dialog_MountainMigration(owner));
                    break;
            }
            heights[page]=body.Height+8;
        }
        finally{Widgets.EndScrollView();}
        if(Widgets.ButtonText(new Rect(rect.x,rect.yMax-34,180,30),TextFor("Reset")))
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(TextFor("ResetConfirm"),()=>{settings.Reset();SettingsChanged();editingExclusions=false;}));
        if(SettingsDefinitions.RestartNeeded)Widgets.Label(new Rect(rect.x+194,rect.yMax-34,rect.width-194,32),TextFor("RestartPending"));
        if(times!=(settings.StairTime,settings.PoweredLiftTime,settings.EmergencyLiftTime))SettingsChanged();
    }
    internal static void SettingsChanged()
    {
        RimLayersMod.Settings.Normalize();
        if(Current.Game!=null)foreach(var map in Find.Maps)LayerMapComponent.For(map)?.Connectors.Invalidate();
    }
    private static void DrawUsage(SettingsBody body)
    {
        if(Current.Game==null){body.Note("LoadGameForUsage");return;}
        var maps=Find.Maps.Select(LayerMapComponent.For).OfType<LayerMapComponent>().ToArray();
        int generated=maps.Sum(m=>m.Layout!.LayerCount-1);
        body.Label("RL_SettingsGenerated".Translate(generated,RimLayersMod.Settings.GlobalLimit));
        body.Label("RL_SettingsReserved".Translate(LayerExpansionQuota.Current?.Reserved??0));
        foreach(var owner in maps)
        {
            var l=owner.Layout!;
            long cells=(long)l.Width*(l.StorageHeight-l.Height);
            body.Label("RL_SettingsMapUsage".Translate(owner.map.Parent.Label,owner.map.uniqueID,l.LayerCount-1,cells.ToString("N0")));
        }
    }
    internal static string TextFor(string key)=> ("RL_Settings"+key).Translate();
}

internal sealed class SettingsBody
{
    private readonly float width;
    public float Height { get; private set; }
    internal SettingsBody(float width)=>this.width=width;
    internal Rect Row(float height){var row=new Rect(0,Height,width,height);Height+=height+8;return row;}
    internal void Label(string text)=>Widgets.Label(Row(Math.Max(26,Text.CalcHeight(text,width))),text);
    internal void Note(string key)=>Label(SettingsMenu.TextFor(key));
    internal bool Button(string key)=>Widgets.ButtonText(Row(30),SettingsMenu.TextFor(key));
    internal void Check(string key,ref bool value)
    {
        string label=SettingsMenu.TextFor(key);
        Widgets.CheckboxLabeled(Row(Math.Max(28,Text.CalcHeight(label,width-32))),label,ref value);
    }
    internal void Slider(string key,ref float value,float min,float max)
    {
        Label(SettingsMenu.TextFor(key)+"  "+value.ToString("0.00")+"×");
        Widgets.HorizontalSlider(Row(24),ref value,new FloatRange(min,max),roundTo:.05f);
    }
    internal void Integer(string key,ref int value,int min,int max)
    {
        Label(SettingsMenu.TextFor(key)+"  "+value);
        float number=value;Widgets.HorizontalSlider(Row(24),ref number,new FloatRange(min,max),roundTo:1);value=(int)Math.Round(number);
    }
}
