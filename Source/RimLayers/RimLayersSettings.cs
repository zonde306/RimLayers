using Verse;

namespace RimLayers;

public enum RecoveryPreference { FallThenReset, ResetFirst }

public sealed class RimLayersSettings : ModSettings
{
    public bool FloorPanel=true, KeyHints=true, LandingMarkers=true, ReturnButtons=true, PlanningBounds=true;
    public bool LayerTransitions=true;
    public bool LowerBackground=true, Mist=true, CombatInfo=true, Trajectory=true;
    public int UpperLimit=2, UndergroundLimit=2, GlobalLimit=20;
    public bool SpaceExpansion=true;
    public float StairTime=1, PoweredLiftTime=1, EmergencyLiftTime=1;
    public float MaterialCost=1, WorkCost=1, LiftPower=1, HeightRange=1, Minerals=1;
    public bool Caves=true, CaveHives=true, AutoRecovery=true;
    public bool CrossLayerInteraction=true;
    public RecoveryPreference RecoveryMode;
    public RimLayers.Environment.EnvironmentSettings Environment=new();
    public List<string> RecoveryExcludedDefs=new();

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref LayerTransitions,"layerTransitions",true);
        Scribe_Values.Look(ref FloorPanel,"floorPanel",true);
        Scribe_Values.Look(ref KeyHints,"keyHints",true);
        Scribe_Values.Look(ref LandingMarkers,"landingMarkers",true);
        // Old settings used one switch for both. Always write the new value so a
        // later marker change cannot reinterpret an omitted default on reload.
        Scribe_Values.Look(ref ReturnButtons,"returnButtons",LandingMarkers,forceSave:true);
        Scribe_Values.Look(ref PlanningBounds,"planningBounds",true);
        Scribe_Values.Look(ref LowerBackground,"lowerBackground",true);
        Scribe_Values.Look(ref Mist,"mist",true);
        Scribe_Values.Look(ref CombatInfo,"combatInfo",true);
        Scribe_Values.Look(ref Trajectory,"trajectory",true);
        Scribe_Values.Look(ref UpperLimit,"upperLimit",2);
        Scribe_Values.Look(ref UndergroundLimit,"undergroundLimit",2);
        Scribe_Values.Look(ref GlobalLimit,"globalLimit",20);
        Scribe_Values.Look(ref SpaceExpansion,"spaceExpansion",true);
        Scribe_Values.Look(ref StairTime,"stairTime",1f);
        Scribe_Values.Look(ref PoweredLiftTime,"poweredLiftTime",1f);
        Scribe_Values.Look(ref EmergencyLiftTime,"emergencyLiftTime",1f);
        Scribe_Values.Look(ref MaterialCost,"materialCost",1f);
        Scribe_Values.Look(ref WorkCost,"workCost",1f);
        Scribe_Values.Look(ref LiftPower,"liftPower",1f);
        Scribe_Values.Look(ref HeightRange,"heightRange",1f);
        Scribe_Values.Look(ref Minerals,"minerals",1f);
        Scribe_Values.Look(ref Caves,"caves",true);
        Scribe_Values.Look(ref CaveHives,"caveHives",true);
        Scribe_Values.Look(ref AutoRecovery,"autoRecovery",true);
        Scribe_Values.Look(ref CrossLayerInteraction,"crossLayerInteraction",true);
        Scribe_Values.Look(ref RecoveryMode,"recoveryMode",RecoveryPreference.FallThenReset);
        Scribe_Deep.Look(ref Environment,"environment");
        Environment??=new();
        Scribe_Collections.Look(ref RecoveryExcludedDefs,"recoveryExcludedDefs",LookMode.Value);
        if(Scribe.mode==LoadSaveMode.PostLoadInit) Normalize();
    }
    public void Normalize()
    {
        UpperLimit=Math.Clamp(UpperLimit,0,9);UndergroundLimit=Math.Clamp(UndergroundLimit,0,9);GlobalLimit=Math.Clamp(GlobalLimit,0,100);
        float Clamp(float x,float min,float max)=>float.IsFinite(x)?Math.Clamp(x,min,max):1;
        StairTime=Clamp(StairTime,.25f,4);PoweredLiftTime=Clamp(PoweredLiftTime,.25f,4);EmergencyLiftTime=Clamp(EmergencyLiftTime,.25f,4);
        MaterialCost=Clamp(MaterialCost,.25f,4);WorkCost=Clamp(WorkCost,.25f,4);LiftPower=Clamp(LiftPower,.25f,4);
        HeightRange=Clamp(HeightRange,0,2);Minerals=Clamp(Minerals,0,3);
        if(!Enum.IsDefined(RecoveryMode))RecoveryMode=RecoveryPreference.FallThenReset;
        RecoveryExcludedDefs??=new();
        Environment??=new();Environment.Normalize();
    }
    public void Reset()
    {
        var defaults=new RimLayersSettings();
        foreach(var field in typeof(RimLayersSettings).GetFields())field.SetValue(this,field.GetValue(defaults));
    }
}
