using RimLayers.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Cargo;

public class Building_CargoPort:Building,IHaulDestination
{
    public Building_CargoHoist? Master;
    public bool AccessEnabled=true;
    public CargoProperties Props=>def.GetModExtension<CargoProperties>();
    public int Elevation=>LayerMapComponent.For(Map) is {} owner&&owner.TryDecode(Position,out var at)?owner.Elevation(at.LayerId):0;
    public bool Available=>Spawned&&AccessEnabled&&Master is {Spawned:true}&&CargoNetwork.For(Map).Connected(this)&&
        CargoPlacement.Supported(Map,Position)&&CargoPlacement.HasOperatorCell(Map,Position);
    public bool StorageTabVisible=>Master!=null;
    public bool HaulDestinationEnabled=>Available;
    public virtual StorageSettings GetStoreSettings()=>Master?.GetStoreSettings()??def.building.defaultStorageSettings;
    public StorageSettings GetParentStoreSettings()=>(Master?.def??def).building.fixedStorageSettings;
    public bool Accepts(Thing t)=>t.def.category==ThingCategory.Item&&Available&&GetStoreSettings().AllowedToAccept(t)&&Master!.SpaceFor(t,includePromises:false)>0;
    public virtual void Notify_SettingsChanged()=>Master?.Notify_SettingsChanged();
    public override void SpawnSetup(Map map,bool respawningAfterLoad)
    {
        base.SpawnSetup(map,respawningAfterLoad);CargoNetwork.For(map).Register(this);
        if(!respawningAfterLoad)CargoNetwork.For(map).Reconnect();
    }
    public override void DeSpawn(DestroyMode mode=DestroyMode.Vanish)
    {
        if(Spawned)CargoNetwork.For(Map).Remove(this);
        base.DeSpawn(mode);
    }
    public override void ExposeData()
    {
        base.ExposeData();Scribe_References.Look(ref Master,"cargoMaster");
        Scribe_Values.Look(ref AccessEnabled,"cargoAccess",true);
    }
    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach(var gizmo in base.GetGizmos())yield return gizmo;
        if(Faction!=Faction.OfPlayer)yield break;
        if(Master!=null)foreach(var g in StorageSettingsClipboard.CopyPasteGizmosFor(GetStoreSettings()))yield return g;
        yield return new Command_Toggle{defaultLabel="RL_CargoAccess".Translate(),defaultDesc="RL_CargoAccessDesc".Translate(),
            icon=TexCommand.ForbidOff,isActive=()=>AccessEnabled,toggleAction=()=>AccessEnabled=!AccessEnabled};
        yield return new Command_Action{defaultLabel="RL_CargoCall".Translate(),defaultDesc="RL_CargoCallDesc".Translate(),
            icon=TexCommand.Install,action=()=>
            {
                var choices=Map.mapPawns.FreeColonistsSpawned.Where(p=>CargoNetwork.CanOperate(p)&&p.CanReach(this,PathEndMode.Touch,Danger.Some))
                    .Select(p=>new FloatMenuOption(p.LabelShortCap,()=>p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(CargoDefOf.RL_CallCargo,this)))).ToList();
                if(choices.Count>0)Find.WindowStack.Add(new FloatMenu(choices));
                else Messages.Message("RL_CargoNoOperator".Translate(),MessageTypeDefOf.RejectInput,false);
            }};
    }
    public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn pawn)
    {
        foreach(var option in base.GetFloatMenuOptions(pawn))yield return option;
        if(Available&&CargoNetwork.CanOperate(pawn)&&pawn.CanReach(this,PathEndMode.Touch,Danger.Some))
            yield return new FloatMenuOption("RL_CargoCall".Translate(),()=>pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(CargoDefOf.RL_CallCargo,this)));
        if(Master==null||!Available||!CargoNetwork.CanOperate(pawn)||!pawn.CanReach(this,PathEndMode.Touch,Danger.Some))yield break;
        foreach(var item in Master.Contents)
        {
            Thing captured=item;
            if(item.IsForbidden(pawn)||!pawn.CanReserve(item))continue;
            if(item.def.IsWeapon&&pawn.equipment!=null&&!pawn.WorkTagIsDisabled(WorkTags.Violent)&&EquipmentUtility.CanEquip(item,pawn))
                yield return new FloatMenuOption("RL_CargoEquip".Translate(item.LabelCap),()=>pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Equip,captured)));
            if(item is Apparel apparel&&pawn.apparel!=null&&EquipmentUtility.CanEquip(item,pawn)&&ApparelUtility.HasPartsToWear(pawn,apparel.def))
                yield return new FloatMenuOption("RL_CargoWear".Translate(item.LabelCap),()=>pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Wear,captured)));
            if(item.IngestibleNow&&pawn.RaceProps.CanEverEat(item))
                yield return new FloatMenuOption("RL_CargoEat".Translate(item.LabelCap),()=>
                {
                    var job=JobMaker.MakeJob(JobDefOf.Ingest,captured);
                    job.count=FoodUtility.WillIngestStackCountOf(pawn,captured.def,FoodUtility.GetNutrition(pawn,captured,captured.def));
                    pawn.jobs.TryTakeOrderedJob(job);
                });
            yield return new FloatMenuOption("RL_CargoTake".Translate(item.LabelCap),()=>
            {
                var job=HaulAIUtility.HaulToStorageJob(pawn,captured,true);
                if(job!=null)pawn.jobs.TryTakeOrderedJob(job);
                else Messages.Message("RL_CargoNoDestination".Translate(),MessageTypeDefOf.RejectInput,false);
            });
        }
    }
    public override string GetInspectString()
    {
        string text=base.GetInspectString();
        if(Master==null||!Spawned||!CargoNetwork.For(Map).Connected(this))return text+"\n"+"RL_CargoDisconnected".Translate();
        var main=Master;
        if(!main.RouteExists(main.Motion.Position,Elevation))text+="\n"+"RL_CargoDisconnected".Translate();
        string Level(int elevation)=>elevation<0?"RL_UndergroundLevel".Translate(-elevation):"RL_SurfaceLevel".Translate(elevation+1);
        string location=main.Motion.Moving?Level((int)Math.Floor(main.Motion.Position))+" – "+Level((int)Math.Ceiling(main.Motion.Position)):Level((int)main.Motion.Position);
        text+="\n"+"RL_CargoStatus".Translate(location,main.Contents.Count,main.Props.capacity,Math.Max(0,main.Requests.Count-1));
        if(main.Motion.Moving)text+="\n"+"RL_CargoMoving".Translate(Level(main.Motion.Destination),main.Motion.Remaining(main.TicksPerFloor));
        if(main.Props.electric&&!main.Powered)text+="\n"+"RL_CargoNoPower".Translate();
        if(!CargoPlacement.HasOperatorCell(Map,Position))text+="\n"+"RL_CargoBlocked".Translate();
        return text.Trim();
    }
    protected override void DrawAt(Vector3 loc,bool flip=false)
    {
        // Code-native top-down frame: wooden winch or metal motor, closed shutter when away.
        bool electric=Master?.Props.electric??Props.electric;
        CargoDrawing.Draw(this,loc,electric,Master?.AtPort(this)==true);
    }
}
[StaticConstructorOnStartup]
internal static class CargoDrawing
{
    private static readonly Material Wood=SolidColorMaterials.SimpleSolidColorMaterial(new Color(.43f,.28f,.15f));
    private static readonly Material Metal=SolidColorMaterials.SimpleSolidColorMaterial(new Color(.38f,.46f,.49f));
    private static readonly Material Dark=SolidColorMaterials.SimpleSolidColorMaterial(new Color(.12f,.15f,.16f));
    private static readonly Material Basket=SolidColorMaterials.SimpleSolidColorMaterial(new Color(.68f,.51f,.29f));
    private static readonly Material Rope=SolidColorMaterials.SimpleSolidColorMaterial(new Color(.8f,.72f,.52f));
    private static void Rect(Vector3 p,float x,float z,float w,float h,Material m,float y)
    {p+=new Vector3(x,y,z);Graphics.DrawMesh(MeshPool.plane10,Matrix4x4.TRS(p,Quaternion.identity,new(w,1,h)),m,0);}
    internal static void Draw(Building_CargoPort port,Vector3 loc,bool electric,bool docked)
    {
        var frame=electric?Metal:Wood;loc.y=AltitudeLayer.Building.AltitudeFor();
        Graphics.DrawMesh(MeshPool.plane10,Matrix4x4.TRS(loc,Quaternion.identity,Vector3.one),CargoArt.Material(electric),0);
        if(docked)
        {
            if(port.Master!=null)
                for(int i=0;i<port.Master.Contents.Count;i++)
                {
                    var item=port.Master.Contents[i];
                    var at=loc+new Vector3((i-1)*.2f,.04f,-.05f);
                    Graphics.DrawMesh(MeshPool.plane10,Matrix4x4.TRS(at,Quaternion.identity,new(.28f,1,.28f)),item.Graphic.MatSingle,0);
                }
        }
        else {Rect(loc,0,-.09f,.6f,.57f,Dark,.01f);for(int i=-2;i<=2;i++)Rect(loc,0,i*.10f-.09f,.58f,.065f,frame,.02f);}
    }
}
public sealed class ITab_CargoContents:ITab
{
    public ITab_CargoContents(){size=new Vector2(380,240);labelKey="RL_CargoContents";}
    public override bool IsVisible=>SelThing is Building_CargoPort {Master:not null};
    protected override void FillTab()
    {
        if(SelThing is not Building_CargoPort {Master:{} main})return;
        var listing=new Listing_Standard();listing.Begin(new Rect(15,15,size.x-30,size.y-30));
        listing.Label("RL_CargoContentsCount".Translate(main.Contents.Count,main.Props.capacity));
        listing.GapLine();
        foreach(var t in main.Contents)listing.Label(t.LabelCap);
        if(main.Contents.Count==0)listing.Label("RL_CargoEmpty".Translate());
        listing.End();
    }
}
