using HarmonyLib;
using RimLayers.Presentation;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static readonly List<Pawn> refreshPawns=new();
    private static Thing? refreshDrawing;
    private static readonly HashSet<string> refreshVisible=new();
    private static readonly Dictionary<string,int> refreshMeshes=new();
    private static readonly Dictionary<string,int> refreshDeathFrames=new(),refreshFirstMeshDelay=new();
    private static int refreshFrame,refreshPhase;
    private static bool refreshFixtureIssued;
    private static bool RefreshFixture=>GenCommandLine.CommandLineArgPassed("rimlayerscorpsefixture");
    private static IntVec3 refreshCenter;
    private static bool RefreshDamage=>GenCommandLine.CommandLineArgPassed("rimlayerscorpsedamage");
    private static int refreshLastFrame=-1,refreshSwitches;
    private static void ConfigureCorpseRefresh(Harmony h)
    {
        if(RefreshFixture)h.Patch(AccessTools.Method(typeof(Root_Entry),"Update"),postfix:new HarmonyMethod(typeof(V1Integration),nameof(RefreshLoadFixture)));
        h.Patch(AccessTools.Method(typeof(Corpse),nameof(Corpse.DynamicDrawPhaseAt)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(RefreshDrawing)),postfix:new HarmonyMethod(typeof(V1Integration),nameof(RefreshDrawn)));
        h.Patch(AccessTools.Method(typeof(Graphics),"DrawMeshCore"),prefix:new HarmonyMethod(typeof(V1Integration),nameof(RefreshMesh)){priority=Priority.Last});
        h.Patch(AccessTools.Method(typeof(PawnRenderTree),nameof(PawnRenderTree.Draw)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(RefreshTree)));
        h.Patch(AccessTools.Method(typeof(RimLayers.Core.LayerMapComponent),nameof(RimLayers.Core.LayerMapComponent.SetViewedLayer)),prefix:new HarmonyMethod(typeof(V1Integration),nameof(RefreshSwitched)));
    }
    private static void RefreshLoadFixture()
    {
        if(refreshFixtureIssued||Current.ProgramState!=ProgramState.Entry||LongEventHandler.AnyEventNowOrWaiting||DefDatabase<ThingDef>.DefCount<100)return;
        refreshFixtureIssued=true;GameDataSaveLoader.LoadGame("RimLayers_CorpseFixture");
    }
    private static void BeginCorpseFixture()
    {
        map=Find.CurrentMap;owner=RimLayers.Core.LayerMapComponent.For(map)!;
        LayerView.Switch(owner,2);Find.Selector.ClearSelection();
        refreshPawns.AddRange(map.mapPawns.AllPawnsSpawned.Where(p=>p.kindDef.defName=="Sparrow"&&LowerFloorBackdrop.Exposed(owner,p.Position)));
        foreach(var p in refreshPawns)p.flight.FlightTick(); // Restore saved live flight animations while the copied world is paused.
        Check("original-save-contains-target-birds",refreshPawns.Count>0,string.Join(";",refreshPawns.Select(p=>$"{p.ThingID}:{p.Position}:flying={p.Flying}")));
        if(refreshPawns.Count==0){Finish();return;}
        var first=refreshPawns[0].Position;
        owner.TryDecode(first,out var local);refreshCenter=At(2,local.X,local.Z);
        Find.CameraDriver.SetRootPosAndSize(refreshCenter.ToVector3Shifted(),24);
        foreach(var m in new[]{AccessTools.Method(typeof(Pawn),nameof(Pawn.DynamicDrawPhaseAt)),AccessTools.Method(typeof(PawnRenderTree),nameof(PawnRenderTree.ParallelPreDraw))})
            Log.Message("[RimLayers.CorpseRefresh] patches "+m+" => "+string.Join(";",(IEnumerable<string>?)Harmony.GetPatchInfo(m)?.Owners??Array.Empty<string>()));
        stage=320;refreshFrame=0;refreshPhase=0;
    }
    private static void RefreshSwitched(){if(stage==320)refreshSwitches++;}
    private static void RefreshDrawing(Corpse __instance,DrawPhase phase)
    {if(stage==320&&BackgroundPass.Active&&phase==DrawPhase.Draw)refreshDrawing=__instance;}
    private static void RefreshDrawn(DrawPhase phase){if(phase==DrawPhase.Draw)refreshDrawing=null;}
    private static void RefreshMesh(Matrix4x4 matrix,Material material,int renderQueue)
    {
        if(refreshDrawing is not Corpse corpse||!BackgroundPass.Active)return;
        var expected=LowerFloorBackdrop.Project(owner,corpse.DrawPos);var actual=matrix.MultiplyPoint3x4(Vector3.zero);
        if((new Vector2(actual.x-expected.x,actual.z-expected.z)).sqrMagnitude<9&&renderQueue<2000)
            {
            refreshMeshes[corpse.ThingID]=refreshMeshes.GetValueOrDefault(corpse.ThingID)+1;
            if(refreshDeathFrames.TryGetValue(corpse.InnerPawn.ThingID,out var died))refreshFirstMeshDelay.TryAdd(corpse.ThingID,Time.frameCount-died);
        }
    }
    private static void RefreshTree(PawnDrawParms parms)
    {
        if(stage==320&&parms.flags.FlagSet(PawnRenderFlags.Cache))
            Log.Message($"[RimLayers.CorpseRefresh] cache pawn={parms.pawn},dead={parms.pawn.Dead},background={BackgroundPass.Active},flags={parms.flags}");
    }
    private static void BeginCorpseRefresh()
    {
        foreach(var c in new CellRect(68,55,15,15))
        {
            var pos=At(0,c.x,c.z);
            foreach(var t in pos.GetThingList(map).ToList())if(t is not Pawn){if(t.def.destroyable)t.Destroy(DestroyMode.Vanish);else t.DeSpawn();}
            map.terrainGrid.SetTerrain(pos,TerrainDefOf.Concrete);map.roofGrid.SetRoof(pos,null);map.fogGrid.Unfog(pos);
        }
        foreach(var cell in new[]{At(0,72,60),At(0,78,60),At(0,72,66),At(0,58,58)})
        {
            var p=PawnGenerator.GeneratePawn(refreshPawns.Count==2?DefDatabase<PawnKindDef>.GetNamed("Cow"):PawnKindDefOf.Colonist,Faction.OfPlayer);
            GenSpawn.Spawn(p,cell,map);if(p.drafter!=null)p.drafter.Drafted=true;
            refreshPawns.Add(p);map.fogGrid.Unfog(cell);
        }
        if(ModsConfig.OdysseyActive)
        {
            foreach(var name in new[]{"Sparrow","Sparrow","Quail"})
            {
                var bird=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed(name));
                var cell=At(0,70+(refreshPawns.Count-4)*4,68);
                map.terrainGrid.SetTerrain(cell,TerrainDefOf.Soil);
                GenSpawn.Spawn(bird,cell,map);refreshPawns.Add(bird);
                if(refreshPawns.Count!=6)bird.flight.StartFlying();
            }
        }
        if(RefreshDamage)
        {
            refreshPawns.RemoveAll(p=>p.kindDef.defName is not ("Sparrow" or "Quail"));
            pawn.Position=At(2,64,60);pawn.Notify_Teleported();
        }
        refreshCenter=At(2,69,62);LayerView.Switch(owner,2);Find.Selector.ClearSelection();Find.CameraDriver.SetRootPosAndSize(refreshCenter.ToVector3Shifted(),24);
        stage=320;refreshFrame=0;refreshPhase=0;
    }
    private static void CorpseRefreshStep()
    {
        if(Time.frameCount==refreshLastFrame)return;refreshLastFrame=Time.frameCount;refreshFrame++;
        if(refreshPhase==0&&refreshFrame==12)
        {pendingScreenshot=Path.Combine(directory,"before-death.png");screenshotHold=3;}
        if(refreshPhase==0&&refreshFrame==20)
        {
            // Cold enemies have never had a native on-floor draw or a colonist portrait.
            if(!RefreshFixture&&!RefreshDamage)
            {
                var cold=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfAncientsHostile);
                GenSpawn.Spawn(cold,At(0,78,64),map);refreshPawns.Add(cold);
            }
            Check("fixture-includes-real-flying-birds-at-death",!ModsConfig.OdysseyActive||refreshPawns.Any(p=>p.kindDef.defName=="Sparrow"&&p.Flying),string.Join(";",refreshPawns.Select(p=>$"{p.kindDef.defName}:{p.Flying}")));
            foreach(var p in refreshPawns)if(RefreshFixture||RefreshDamage||p!=refreshPawns[3])refreshVisible.Add(p.ThingID);
            // Native damage exercises health/death notifications; direct Kill remains a separate baseline.
            Check("live-flying-birds-have-native-animation",!ModsConfig.OdysseyActive||refreshPawns.Where(p=>p.Flying).All(p=>p.Drawer.renderer.CurAnimation!=null),string.Join(";",refreshPawns.Select(p=>$"{p}:{p.Drawer.renderer.CurAnimation?.defName}")));
            foreach(var p in refreshPawns)
            {
                refreshDeathFrames[p.ThingID]=Time.frameCount;
                if(RefreshDamage)
                {
                    var core=p.RaceProps.body.corePart;
                    p.TakeDamage(new DamageInfo(DamageDefOf.Bullet,10000,1000,hitPart:core));
                }
                else p.Kill(null);
            }
            Check("native-death-path-completed",refreshPawns.All(p=>p.Dead));
            if(refreshPawns.Any(p=>!p.Dead)){Finish();return;}
            Check("death-created-real-registered-corpses-without-view-switch",refreshPawns.All(p=>p.Corpse is {Spawned:true} c&&map.dynamicDrawManager.DrawThings.Contains(c)));
            Check("upper-view-still-selected-at-moment-of-death",owner.ViewedLayer==2&&refreshSwitches==0);
            refreshMeshes.Clear();refreshPhase=1;refreshFrame=0;
            screenshotHold=3;
        }
        else if(refreshPhase==1&&refreshFrame==12)
        {
            Check("fresh-background-corpses-submit-visible-mesh-without-switch",refreshPawns.Where(p=>refreshVisible.Contains(p.ThingID)).All(p=>refreshMeshes.ContainsKey(p.Corpse.ThingID)),string.Join(";",refreshPawns.Select(p=>$"{p.Corpse.ThingID}={refreshMeshes.GetValueOrDefault(p.Corpse.ThingID)}")));
            Check("fresh-corpses-render-on-first-post-death-frame",refreshPawns.Where(p=>refreshVisible.Contains(p.ThingID)).All(p=>refreshFirstMeshDelay.TryGetValue(p.Corpse.ThingID,out var delay)&&delay<=1),string.Join(";",refreshPawns.Select(p=>$"{p.Corpse.ThingID}={refreshFirstMeshDelay.GetValueOrDefault(p.Corpse.ThingID,-1)}")));
            if(!RefreshFixture&&!RefreshDamage)Check("covered-corpse-does-not-submit-background-mesh",!refreshMeshes.ContainsKey(refreshPawns[3].Corpse.ThingID));
            Check("no-floor-switch-or-corpse-move-to-refresh",owner.ViewedLayer==2&&refreshSwitches==0&&refreshPawns.All(p=>owner.IsGroundCell(p.Corpse.Position)));
            pendingScreenshot=Path.Combine(directory,"fresh-corpses-distant.png");screenshotHold=3;refreshPhase=2;refreshFrame=0;
        }
        else if(refreshPhase==2&&refreshFrame==10)
        {
            Find.CameraDriver.SetRootPosAndSize(refreshCenter.ToVector3Shifted(),13);refreshPhase=3;refreshFrame=0;
        }
        else if(refreshPhase==3&&refreshFrame==12)
        {
            pendingScreenshot=Path.Combine(directory,"fresh-corpses-close.png");screenshotHold=3;refreshPhase=4;refreshFrame=0;
        }
        else if(refreshPhase==4&&refreshFrame==10)Finish();
    }
}
