using System.Globalization;
using RimLayers.Environment;
using RimLayers.Underground;
using UnityEngine;
using Verse;

namespace RimLayers;

internal sealed class EnvironmentSettingsMenu
{
    private readonly HashSet<string> open=new(){"Temperature"};
    private readonly HashSet<string> advanced=new();
    private readonly Dictionary<string,string> buffers=new();
    private static string T(string key)=>("RL_Environment"+key).Translate();
    internal void Draw(SettingsBody body)
    {
        var settings=RimLayersMod.Settings.Environment;
        body.Label(T("GlobalHelp"));
        var game=Current.Game?.GetComponent<LayerEnvironmentGame>();
        if(game!=null)
        {
            bool enabled=game.Enabled;Widgets.CheckboxLabeled(body.Row(30),T("EnabledForSave"),ref enabled);
            if(enabled!=game.Enabled){game.Enabled=enabled;foreach(var map in Find.Maps)map.GetComponent<Core.LayerMapComponent>().Environment.Invalidate();}
        }
        else body.Label(T("LoadSave"));
        body.Label(T("TimingHelp"));
        foreach(string group in new[]{"Temperature","Disasters","Minerals","Lava","Insects"})
        {
            if(Widgets.ButtonText(body.Row(34),(open.Contains(group)?"− ":"+ ")+T(group)))
            {if(!open.Add(group))open.Remove(group);}
            if(!open.Contains(group))continue;
            if(group=="Disasters")
            {
                Toggle(body,"SolarShield",ref settings.SolarShield);
                Toggle(body,"FloodDown",ref settings.FloodDown);
                Toggle(body,"LavaDown",ref settings.LavaDown);
            }
            if(group=="Minerals")
            {
                float quantity=RimLayersMod.Settings.Minerals;
                Number(body,"MineralQuantity",T("MineralQuantity"),ref quantity,0,3,"×",1,true);
                RimLayersMod.Settings.Minerals=quantity;
                body.Label("B1 / B5 / B9: "+string.Join(" / ",new[]{1,5,9}.Select(d=>EnvironmentMath.MineralBudgetFactor(d,settings.MineralGrowth,settings.MineralCap).ToString("0.##")+"×"))+" · "+T("ClusterBudgetOnly"));
            }
            if(group=="Lava")Toggle(body,"LavaEnabled",ref settings.LavaEnabled);
            if(group=="Insects")Toggle(body,"InitialHiveEnabled",ref RimLayersMod.Settings.CaveHives);
            foreach(var p in EnvironmentSettings.Parameters.Where(p=>p.Group==group&&!p.Advanced))Parameter(body,p,settings);
            if(Widgets.ButtonText(body.Row(28),(advanced.Contains(group)?"− ":"+ ")+T("Advanced")))
            {if(!advanced.Add(group))advanced.Remove(group);}
            if(advanced.Contains(group))
            {
                foreach(var p in EnvironmentSettings.Parameters.Where(p=>p.Group==group&&p.Advanced))Parameter(body,p,settings);
                if(group=="Minerals")
                    foreach(var def in UndergroundGeology.ConfigurablePool())
                    {
                        float weight=settings.Weight(def.defName);
                        Number(body,"ore:"+def.defName,def.LabelCap,ref weight,0,5,"×",1,true);
                        if(weight==1)settings.MineralWeights.Remove(def.defName);else settings.MineralWeights[def.defName]=weight;
                    }
            }
            if(Widgets.ButtonText(body.Row(28),T("ResetGroup")))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("ResetConfirm"),()=>{settings.ResetGroup(group);if(group=="Minerals")RimLayersMod.Settings.Minerals=1;buffers.Clear();}));
        }
        settings.Normalize();
    }
    private void Parameter(SettingsBody body,EnvironmentSettings.Parameter p,EnvironmentSettings settings)
    {
        float value=p.Get(settings);
        Number(body,p.Name,T(p.Name),ref value,p.Min,p.Max,p.Unit,p.Default,p.Generation,p.Integer);
        p.Set(settings,value);
    }
    private static void Toggle(SettingsBody body,string key,ref bool value)=>Widgets.CheckboxLabeled(body.Row(30),T(key),ref value);
    private void Number(SettingsBody body,string id,string label,ref float value,float min,float max,string unit,float initial,bool generation,bool integer=false)
    {
        float scale=unit is "fraction" or "%/B"?100:1;
        string suffix=unit=="fraction"?"%":unit;
        body.Label(label+"  · "+T("Default")+" "+(initial*scale).ToString("0.###",CultureInfo.InvariantCulture)+" "+suffix+" · "+T(generation?"NewLayers":"NextUpdate"));
        var row=body.Row(28);var entry=new Rect(row.xMax-132,row.y,90,28);
        float before=value;
        var slider=new Rect(row.x,row.y,Math.Max(60,row.width-150),24);
        Widgets.HorizontalSlider(slider,ref value,new FloatRange(min,max),roundTo:integer?1:-1);
        string control="RL_Environment_"+id;
        if(!buffers.TryGetValue(id,out var text)||value!=before||GUI.GetNameOfFocusedControl()!=control)
            text=(value*scale).ToString(integer?"0":"0.###",CultureInfo.InvariantCulture);
        GUI.SetNextControlName(control);
        text=Widgets.TextField(entry,text);buffers[id]=text;
        Widgets.Label(new Rect(entry.xMax+4,row.y,38,28),suffix);
        if(float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out float parsed)&&float.IsFinite(parsed))
        {parsed/=scale;if(parsed>=min&&parsed<=max)value=integer?MathF.Round(parsed):parsed;}
    }
}
