using System.Reflection;
using HarmonyLib;
using RimLayers.Presentation;
using RimLayers.Compatibility;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static int commandPhase, commandWait, commandShotBaseline;
    private static Vector3 commandMouse;
    private static Pawn commandTarget = null!;
    private static bool commandGuiBusy;
    private static readonly List<string> commandTrace=new();
    private static Vector2 CommandScreenPoint => new(UI.screenWidth*.5f,UI.screenHeight*.25f);
    private static bool CommandMouseOnUI(ref Vector2 __result){__result=new Vector2(CommandScreenPoint.x,UI.screenHeight-CommandScreenPoint.y);return false;}
    private static void CommandAbsorb(Rect r)
    {if(commandGuiBusy&&r.Contains(Event.current.mousePosition))commandTrace.Add($"absorbing rect={r},eventMouse={Event.current.mousePosition},UI={UI.screenWidth}x{UI.screenHeight},UIScale={Prefs.UIScale}");}
    private static void CommandButton(Rect position)
    {if(commandGuiBusy&&position.Contains(Event.current.mousePosition))commandTrace.Add($"GUI button hit rect={position}, mouse={Event.current.mousePosition}, requested={CommandScreenPoint}, UI={UI.screenWidth}x{UI.screenHeight}, matrix={GUI.matrix}");}
    private static void CommandEventUsed()
    {if(commandGuiBusy&&commandTrace.Count<100)commandTrace.Add("Event.Use\n"+System.Environment.StackTrace);}
    private static void CommandTrace(MethodBase __originalMethod)
    {
        if(commandGuiBusy)commandTrace.Add($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}:{Event.current.type}, windows="+string.Join(",",Find.WindowStack.Windows.Select(w=>w.GetType().Name)));
    }
    private static bool CommandMouse(ref Vector3 __result) { __result=commandMouse;return false; }
    private static void BeginCommandInput()
    {
        pawn=map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>!p.WorkTagIsDisabled(WorkTags.Violent))!;
        if(pawn==null)
        {
            // Quicktest can randomly start with only nonviolent colonists. The input fixture needs a real capable actor.
            for(int i=0;i<20;i++)
            {
                var candidate=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
                if(candidate.WorkTagIsDisabled(WorkTags.Violent)){candidate.Destroy(DestroyMode.Vanish);continue;}
                pawn=candidate;GenSpawn.Spawn(pawn,At(0,48,49),map);break;
            }
            if(pawn==null)throw new InvalidOperationException("Could not generate a violence-capable input fixture actor.");
            foreach(var other in map.mapPawns.FreeColonistsSpawned.Where(p=>p!=pawn&&p.Position==At(0,54,54)).ToList())
            {other.Position=At(0,48,48);other.Notify_Teleported();}
        }
        pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.drafter.FireAtWill=false;
        pawn.Position=At(0,54,54);pawn.Notify_Teleported();
        pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
        pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle")));
        var gun=pawn.equipment.Primary!;
        shootingVerb=gun.GetComp<CompEquippable>().PrimaryVerb;
        var props=(VerbProperties)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(shootingVerb.verbProps,null)!;
        props.canGoWild=false;props.warmupTime=.1f;shootingVerb.verbProps=props;
        pawn.skills.GetSkill(SkillDefOf.Shooting).Level=20;
        commandTarget=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfAncientsHostile);
        for(int x=65;x<=80;x++)for(int z=59;z<=61;z++)
        {
            var c=At(0,x,z);
            foreach(var t in c.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,TerrainDefOf.Concrete);map.fogGrid.Unfog(c);
        }
        GenSpawn.Spawn(commandTarget,At(0,78,60),map);frozenCombatPawn=commandTarget;
        commandTarget.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture,50000),JobCondition.InterruptForced);
        Find.DesignatorManager.Deselect();Find.Targeter.StopTargeting();Find.Selector.ClearSelection();Find.Selector.Select(pawn);
        LayerView.Switch(owner,0);Find.CameraDriver.SetRootPosAndSize(pawn.Position.ToVector3Shifted(),18);
        var providers=(System.Collections.ICollection?)AccessTools.Field(typeof(FloatMenuMakerMap),"providers").GetValue(null);
        Check("command-fixture-uses-real-actor-weapon",pawn.CanTakeOrder,$"pawn={pawn.def.defName},weapon={gun.def.defName},verb={shootingVerb.GetType().FullName}");
        Check("native-float-menu-providers-initialized",providers?.Count>0,$"providers={providers?.Count.ToString()??"null"}");
        if(ModLister.GetActiveModWithIdentifier("rimiris.dashboard")!=null)
            Check("dashboard-known-bad-field-is-isolated-in-developer-audit",DashboardStartupInspection.SkippedFields==1,$"skipped={DashboardStartupInspection.SkippedFields}");
        commandPhase=0;commandWait=0;stage=310;
    }
    private static void CommandInputStep()
    {
        commandWait++;
        // Let native movement / attack toils execute after the actual input path submitted a job.
        if(commandPhase==1 && (pawn.Position==At(0,57,54)||commandWait>1500))
        {
            Check("right-click-order-reaches-same-floor-cell",pawn.Position==At(0,57,54),DescribePawn());
            commandPhase=2;commandWait=0;
        }
        if(commandPhase==3 && (pawn.Position==At(2,64,60)||commandWait>2500))
        {
            Check("right-click-order-walks-through-three-floors",pawn.Position==At(2,64,60),DescribePawn());
            commandPhase=4;commandWait=0;
        }
        if(commandPhase is 5 or 7 or 9 && (shootingVerb.LastShotTick>commandShotBaseline || commandWait>1500))
        {
            string name=commandPhase==5?"right-click-native-attack-toil-fires":commandPhase==7?"shoot-gizmo-native-attack-toil-fires":"shoot-gizmo-fires-at-visible-real-background-pawn";
            Check(name,shootingVerb.LastShotTick>commandShotBaseline,$"before={commandShotBaseline},lastShot={shootingVerb.LastShotTick},job={pawn.CurJob}");
            pawn.jobs.StopAll();
            if(commandPhase==9){Finish();return;}
            commandPhase++;commandWait=0;
        }
    }
    private static void CommandInputOnGUI()
    {
        if(scenario!="command-input"||stage!=310||commandGuiBusy||Event.current.type!=EventType.Repaint||commandPhase%2!=0)return;
        commandGuiBusy=true;
        var previous=Event.current;var mouseHook=new Harmony("rimlayers.diagnostics.command-mouse");
        try
        {
            commandTrace.Clear();
            mouseHook.Patch(AccessTools.Method(typeof(GUI),"ButtonImpl"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandButton)));
            mouseHook.Patch(AccessTools.Method(typeof(GenUI),nameof(GenUI.AbsorbClicksInRect)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandAbsorb)));
            mouseHook.Patch(AccessTools.Method(typeof(Event),nameof(Event.Use)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandEventUsed)));
            mouseHook.Patch(AccessTools.Method(typeof(UI),nameof(UI.MouseMapPosition)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandMouse)));
            mouseHook.Patch(AccessTools.PropertyGetter(typeof(UI),nameof(UI.MousePositionOnUI)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(CommandMouseOnUI)));
            foreach(var method in new[]{AccessTools.Method(typeof(UIRoot),nameof(UIRoot.UIRootOnGUI)),AccessTools.Method(typeof(MapInterface),nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs)),AccessTools.Method(typeof(MapInterface),nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs)),AccessTools.Method(typeof(WindowStack),nameof(WindowStack.HandleEventsHighPriority)),AccessTools.Method(typeof(WindowStack),nameof(WindowStack.WindowStackOnGUI)),AccessTools.Method(typeof(DesignatorManager),nameof(DesignatorManager.ProcessInputEvents)),AccessTools.Method(typeof(Targeter),nameof(Targeter.ProcessInputEvents)),AccessTools.Method(typeof(Selector),"HandleMapClicks")})
                mouseHook.Patch(method,postfix:new HarmonyMethod(typeof(V1Integration),nameof(CommandTrace)));
            if(commandPhase is 0 or 2)
            {
                int layer=commandPhase==0?0:2;
                var cell=commandPhase==0?At(0,57,54):At(2,64,60);
                LayerView.Switch(owner,layer);commandMouse=cell.ToVector3Shifted();
                Event.current=new Event{type=EventType.MouseDown,rawType=EventType.MouseDown,button=1,clickCount=1,mousePosition=CommandScreenPoint};
                ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
                Check(commandPhase==0?"native-right-click-submits-goto":"native-right-click-submits-cross-floor-goto",pawn.CurJob?.def==JobDefOf.Goto&&pawn.CurJob.targetA.Cell==cell,$"event={Event.current.type},job={pawn.CurJob}");
                commandPhase++;commandWait=0;
            }
            else
            {
                pawn.jobs.StopAll();commandShotBaseline=shootingVerb.LastShotTick;
                if(commandPhase==4)
                {
                    // The target's real layer is viewed, while the selected caster stays upstairs.
                    LayerView.Switch(owner,0);commandMouse=commandTarget.DrawPos;
                    Event.current=new Event{type=EventType.MouseDown,rawType=EventType.MouseDown,button=1,clickCount=1,mousePosition=CommandScreenPoint};
                    ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
                    Check("native-right-click-submits-attack-real-pawn",pawn.CurJob?.def==JobDefOf.AttackStatic&&pawn.CurJob.targetA.Thing==commandTarget,$"target={commandTarget},job={pawn.CurJob}");
                }
                else
                {
                    if(commandPhase==8)LayerView.Switch(owner,2);else LayerView.Switch(owner,0);
                    commandMouse=LowerFloorBackdrop.Project(owner,commandTarget.DrawPos);
                    var gizmo=pawn.GetGizmos().OfType<Command_VerbTarget>().First(g=>g.verb==shootingVerb);
                    gizmo.ProcessInput(new Event{type=EventType.MouseDown,rawType=EventType.MouseDown,button=0});
                    Check("native-shoot-gizmo-starts-targeting-"+commandPhase,Find.Targeter.IsTargeting&&Find.Targeter.targetingSource==shootingVerb);
                    var underMouse=(LocalTargetInfo)AccessTools.Method(typeof(Targeter),"CurrentTargetUnderMouse").Invoke(Find.Targeter,new object[]{false})!;
                    Check("native-shoot-targeter-keeps-real-pawn-"+commandPhase,underMouse.Thing==commandTarget,underMouse.ToString());
                    Event.current=new Event{type=EventType.MouseDown,rawType=EventType.MouseDown,button=0,clickCount=1,mousePosition=CommandScreenPoint};
                    ((UIRoot_Play)Find.UIRoot).UIRootOnGUI();
                    Check("native-shoot-click-submits-attack-"+commandPhase,pawn.CurJob?.def==JobDefOf.AttackStatic&&pawn.CurJob.targetA.Thing==commandTarget,$"event={Event.current.type},job={pawn.CurJob},targeting={Find.Targeter.IsTargeting}");
                }
                commandPhase++;commandWait=0;
            }
        }
        catch(Exception ex){Check("native-command-input-exception-phase-"+commandPhase,false,ex.ToString());commandPhase++;commandWait=0;}
        finally{Log.Message("[RimLayers.CommandTrace] phase="+commandPhase+"\n"+string.Join("\n",commandTrace));mouseHook.UnpatchAll(mouseHook.Id);Event.current=previous;commandGuiBusy=false;}
    }
}
