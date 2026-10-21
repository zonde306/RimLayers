using Verse;

namespace RimLayers.Recovery;

public enum RecoveryStage { Discovered, Pending, Falling, Resetting, Stored, Completed }
public sealed class RecoveryRecord:IExposable
{
    public Thing? Thing;
    public int MapId,LastMapId,NextAttempt;
    public string MapName="",Reason="";
    public IntVec3 Origin=IntVec3.Invalid,LastValid=IntVec3.Invalid;
    public RecoveryStage Stage;
    public bool ManualOnly,StructuralFall,FallDamageApplied;
    public int FallLevels;
    internal IEnumerator<RecoveryTarget>? Search;
    public void ExposeData()
    {
        Scribe_References.Look(ref Thing,"thing");
        Scribe_Values.Look(ref MapId,"mapId");Scribe_Values.Look(ref LastMapId,"lastMapId");
        Scribe_Values.Look(ref MapName,"mapName","");Scribe_Values.Look(ref Reason,"reason","");
        Scribe_Values.Look(ref Origin,"origin",IntVec3.Invalid);Scribe_Values.Look(ref LastValid,"lastValid",IntVec3.Invalid);
        Scribe_Values.Look(ref Stage,"stage",RecoveryStage.Discovered);Scribe_Values.Look(ref ManualOnly,"manualOnly");
        Scribe_Values.Look(ref NextAttempt,"nextAttempt");
        Scribe_Values.Look(ref StructuralFall,"structuralFall");Scribe_Values.Look(ref FallLevels,"fallLevels");Scribe_Values.Look(ref FallDamageApplied,"fallDamageApplied");
    }
}
public sealed class RecoveryHistory:IExposable
{
    public int Id,Tick,Count;
    public void ExposeData(){Scribe_Values.Look(ref Id,"id");Scribe_Values.Look(ref Tick,"tick");Scribe_Values.Look(ref Count,"count");}
}
