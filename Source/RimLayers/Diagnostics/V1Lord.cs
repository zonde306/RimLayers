using System.Text.Json;
using RimLayers.Presentation;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimLayers.Diagnostics;

internal static partial class V1Integration
{
    private static Lord testLord=null!;
    private static readonly List<Pawn> lordPawns=new();
    private static readonly Dictionary<string,HashSet<int>> lordVisited=new();
    private static bool lordSaved,lordIdentityHeld=true,lordDutyHeld=true;
    private static int lordPhase;
    private static void BeginLord()
    {
        int offset=0;
        foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList())
        {
            p.jobs.StopAll();p.drafter.Drafted=true;
            p.Position=At(0,100+offset++,100);p.Notify_Teleported();
        }
        for(int i=0;i<3;i++)
        {
            var p=PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("AncientSoldier"),Faction.OfAncientsHostile);
            GenSpawn.Spawn(p,At(0,54+i,54),map);
            if(p.needs.food!=null)p.needs.food.CurLevelPercentage=1;
            if(p.needs.rest!=null)p.needs.rest.CurLevelPercentage=1;
            lordPawns.Add(p);lordVisited[p.ThingID]=new();
        }
        destination=At(2,60,60);
        testLord=LordMaker.MakeNewLord(Faction.OfAncientsHostile,new LordJob_Travel(destination),map,lordPawns);
        Check("native-hostile-travel-lord-owns-three-real-members",testLord.ownedPawns.Count==3&&lordPawns.All(p=>p.GetLord()==testLord));
        stage=230;ticks=0;
    }
    private static void RestoreLord()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"lord-baseline.json")));
        var b=baseline.RootElement;
        testLord=map.lordManager.lords.Single(l=>l.loadID==b.GetProperty("lord").GetInt32());
        foreach(var id in b.GetProperty("pawns").EnumerateArray())
        {
            var p=map.mapPawns.AllPawnsSpawned.Single(p=>p.ThingID==id.GetString());
            lordPawns.Add(p);lordVisited[p.ThingID]=new();
        }
        destination=At(2,60,60);lordSaved=true;
        Check("restart-keeps-single-native-lord-and-exact-members",testLord.LordJob is LordJob_Travel&&
            testLord.CurLordToil is LordToil_Travel&&testLord.ownedPawns.Count==3&&lordPawns.All(p=>p.GetLord()==testLord)&&
            map.lordManager.lords.Count(l=>l.loadID==testLord.loadID)==1);
        Check("restart-keeps-final-duty-focus-on-third-floor",lordPawns.All(p=>p.mindState.duty?.focus.Cell==destination));
        Check("restart-really-resumes-party-in-middle-floor",lordPawns.Any(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId==1));
        stage=230;ticks=0;
    }
    private static bool LordAt(int floor,IntVec3 point)=>lordPawns.All(p=>p.Spawned&&owner.TryDecode(p.Position,out var c)&&
        c.LayerId==floor&&p.Position.InHorDistOf(point,10));
    private static void LordStep()
    {
        lordIdentityHeld&=testLord.Map==map&&testLord.ownedPawns.Count==3&&testLord.ownedPawns.Distinct().Count()==3&&
            lordPawns.All(p=>p.Spawned&&p.Map==map&&p.GetLord()==testLord)&&testLord.numPawnsLostViolently==0;
        lordDutyHeld&=lordPawns.All(p=>p.mindState.duty?.focus.Cell==destination);
        foreach(var p in lordPawns)if(owner.TryDecode(p.Position,out var c))lordVisited[p.ThingID].Add(c.LayerId);
        if(!lordSaved&&lordPawns.Any(p=>owner.TryDecode(p.Position,out var c)&&c.LayerId==1))
        {
            GameDataSaveLoader.SaveGame("RimLayers_V1Lord");
            File.WriteAllText(Path.Combine(directory,"lord-baseline.json"),JsonSerializer.Serialize(new {lord=testLord.loadID,pawns=lordPawns.Select(p=>p.ThingID).ToArray()}));
            lordSaved=true;Check("saved-real-native-lord-with-party-on-middle-floor",true);
        }
        if(lordPhase==0&&LordAt(2,destination))
        {
            Check("native-duty-ai-moves-entire-hostile-party-to-third-floor",true,$"ticks={ticks}");
            Check("stairs-do-not-remove-or-duplicate-lord-members",lordIdentityHeld);
            Check("middle-floor-does-not-replace-final-duty",lordDutyHeld);
            // An actual injury exercises the native Travel -> DefendPoint transition.
            lordPawns[0].TakeDamage(new DamageInfo(DamageDefOf.Blunt,1));
            Check("actual-harm-triggers-native-lord-defend-transition",testLord.CurLordToil is LordToil_DefendPoint);
            if(testLord.CurLordToil is not LordToil_DefendPoint defend){Finish();return;}
            // Travel's temporary defend state returns to its original destination after
            // 1200 harmless ticks. Retask the same native Lord with a persistent job.
            destination=At(0,56,60);
            testLord.SetJob(new LordJob_DefendPoint(destination,wanderRadius:3,defendRadius:8,addFleeToil:false));
            testLord.GotoToil(testLord.Graph.StartingToil);
            lordPhase=1;ticks=0;
        }
        else if(lordPhase==1&&LordAt(0,destination))
        {
            Check("native-defend-duty-returns-entire-party-through-middle-floor",lordVisited.Values.All(v=>v.Contains(1)&&v.Contains(2)),$"ticks={ticks}");
            Check("retasked-native-lord-keeps-identity-and-focus",lordIdentityHeld&&lordDutyHeld);
            Check("party-crossing-did-not-count-as-violent-loss",testLord.numPawnsLostViolently==0);
            var victim=lordPawns[0];victim.Kill(null);
            Check("real-death-still-removes-member-and-records-native-loss",victim.Dead&&testLord.numPawnsLostViolently==1&&
                testLord.ownedPawns.Count==2&&!testLord.ownedPawns.Contains(victim)&&lordPawns.Skip(1).All(p=>p.GetLord()==testLord));
            Check("lord-mid-route-save-checkpoint-was-reached",lordSaved);
            LayerView.Switch(owner,0);Find.Selector.ClearSelection();Find.Selector.Select(lordPawns[1]);
            Find.CameraDriver.SetRootPosAndSize(destination.ToVector3Shifted(),14);
            pendingScreenshot=Path.Combine(directory,mode+"-lord.png");screenshotHold=3;stage=11;ticks=0;
        }
        else if(ticks>5000)
        {
            Check("native-lord-party-reaches-current-duty",false,$"phase={lordPhase},toil={testLord.CurLordToil},focus={destination},pawns="+
                string.Join(";",lordPawns.Select(p=>$"{p.Position}/{p.CurJob}/focus={p.mindState.duty?.focus}")));
            Finish();
        }
    }
}
