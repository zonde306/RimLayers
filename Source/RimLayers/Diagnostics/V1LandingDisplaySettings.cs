using System.IO;
using Verse;

namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void CheckLandingDisplayPersistence()
    {
        RimLayersSettings Load(string path)
        {
            RimLayersSettings loaded=null!;
            Scribe.loader.InitLoading(path);
            try {Scribe_Deep.Look(ref loaded,"settings");Scribe.loader.FinalizeLoading();return loaded;}
            finally {if(Scribe.mode!=LoadSaveMode.Inactive)Scribe.loader.ForceStop();}
        }
        foreach(bool markers in new[]{false,true})
        {
            string path=Path.Combine(directory,"legacy-markers-"+markers+".xml");
            File.WriteAllText(path,"<SettingsProbe><settings><landingMarkers>"+markers+"</landingMarkers></settings></SettingsProbe>");
            var legacy=Load(path);
            Check("legacy-combined-landing-option-migrates-"+markers,legacy.LandingMarkers==markers&&legacy.ReturnButtons==markers);
            foreach(bool buttons in new[]{false,true})
            {
                var settings=new RimLayersSettings{LandingMarkers=markers,ReturnButtons=buttons};
                path=Path.Combine(directory,"landing-controls-"+markers+"-"+buttons+".xml");
                Scribe.saver.InitSaving(path,"SettingsProbe");
                try {Scribe_Deep.Look(ref settings,"settings");Scribe.saver.FinalizeSaving();}
                finally {if(Scribe.mode!=LoadSaveMode.Inactive)Scribe.saver.ForceStop();}
                var loaded=Load(path);
                Check("independent-landing-controls-roundtrip-"+markers+"-"+buttons,loaded.LandingMarkers==markers&&loaded.ReturnButtons==buttons);
            }
        }
    }
}
