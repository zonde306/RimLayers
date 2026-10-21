using Verse.AI;
using RimWorld;
using Verse;

namespace RimLayers.Cargo;

public sealed class CargoProperties:DefModExtension
{
    public bool electric;
    public bool shaft;
    public bool station;
    public int direction;
    public int capacity=3;
    public int handlingTicks=30;
    public int manualTicksPerFloor=180;
    public int electricTicksPerFloor=45;
    public int electricSetupTicks=30;
    public float idleWatts=30;
    public float runningWatts=120;
}
[DefOf]
public static class CargoDefOf
{
    public static ThingDef RL_CargoPort=null!;
    public static JobDef RL_CallCargo=null!;
    static CargoDefOf()=>DefOfHelper.EnsureInitializedInCtor(typeof(CargoDefOf));
}
public sealed class CargoRequest:IExposable
{
    public Pawn Pawn=null!;
    public Job Job=null!;
    public Building_CargoPort Port=null!;
    public Thing? Item;
    public int Count;
    public bool Deposit;
    public bool CallOnly;
    public int Handled;
    public int ToilIndex=-1;
    public bool Valid=>Pawn!=null&&CargoNetwork.CanOperate(Pawn)&&Pawn.CurJob==Job&&Port?.Available==true&&
        Pawn.Map==Port.Map&&!Port.IsForbidden(Pawn)&&!Pawn.Downed&&
        Port.Master is {} master&&master.RouteExists(master.Motion.Position,Port.Elevation)&&
        (CallOnly||Item is {Destroyed:false}&&!Item.IsForbidden(Pawn));
    public void ExposeData()
    {
        Scribe_References.Look(ref Pawn,"pawn");Scribe_References.Look(ref Job,"job");
        Scribe_References.Look(ref Port,"port");Scribe_References.Look(ref Item,"item");
        Scribe_Values.Look(ref Count,"count");Scribe_Values.Look(ref Deposit,"deposit");
        Scribe_Values.Look(ref CallOnly,"callOnly");Scribe_Values.Look(ref Handled,"handled");
        Scribe_Values.Look(ref ToilIndex,"toilIndex",-1);
    }
}
