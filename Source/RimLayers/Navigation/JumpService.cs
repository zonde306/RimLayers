using RimLayers.Combat;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Verse;

namespace RimLayers.Navigation;

internal static class JumpService
{
    [ThreadStatic] internal static JumpArc? LaunchArc;
    [ThreadStatic] internal static Pawn? HandlingPawn;
    internal static LayerMapComponent? Cross(Pawn pawn,IntVec3 root,IntVec3 target)
    {
        var owner=LayerMapComponent.For(pawn.Map);
        return owner!=null&&owner.TryDecode(root,out var a)&&owner.TryDecode(target,out var b)&&a.LayerId!=b.LayerId?owner:null;
    }
    internal static JumpArc Arc(LayerMapComponent owner,Pawn pawn,IntVec3 root,IntVec3 target)
    {
        var scene=new LayerShotScene(owner);
        ShotPoint a=scene.Point(root,JumpGeometry.FeetOffset),b=scene.Point(target,JumpGeometry.FeetOffset);
        return new(a,b,JumpGeometry.RiseFor(b.Height-a.Height),CombatService.TopHeight(pawn),Math.Clamp(.22*Math.Sqrt(pawn.BodySize),.15,.48));
    }
    internal static bool Landing(LayerMapComponent owner,Thing flying,IntVec3 cell)
    {
        if(!owner.TryDecode(cell,out var at)||!JumpUtility.ValidJumpTarget(flying,owner.map,cell))return false;
        foreach(var c in GenAdj.OccupiedRect(cell,flying.Rotation,flying.def.Size))
        {
            if(!owner.TryDecode(c,out var part)||part.LayerId!=at.LayerId||!owner.Structure.HasSurface(c))return false;
            var terrain=owner.map.terrainGrid.TerrainAt(c);
            if(terrain==RimLayersDefOf.RL_OpenAir||terrain==RimLayersDefOf.RL_Isolation||terrain.defName=="Space")return false;
            foreach(var other in c.GetThingList(owner.map))
                if(other!=flying&&(other is Pawn||GenSpawn.SpawningWipes(flying.def,other.def)))return false;
        }
        return true;
    }
    internal static bool Solve(Pawn pawn,IntVec3 root,IntVec3 target,float range,out JumpArc arc,out string reason)
    {
        arc=default;reason="RL_JumpNoLanding";
        var owner=LayerMapComponent.For(pawn.Map);
        if(owner==null||!owner.TryDecode(root,out _)||!Landing(owner,pawn,target))return false;
        arc=Arc(owner,pawn,root,target);
        if(!JumpGeometry.InRange(arc,range)){reason="RL_JumpOutOfRange";return false;}
        if(JumpGeometry.First(arc,new LayerShotScene(owner,pawn,includeCover:true))!=null)
        {reason="RL_JumpBlocked";return false;}
        reason="RL_JumpClear";return true;
    }
    internal static bool Launch(Verb_Jump verb)
    {
        var pawn=verb.CasterPawn;
        if(!Solve(pawn,pawn.Position,verb.CurrentTarget.Cell,verb.EffectiveRange,out var arc,out var reason))
        {
            Messages.Message(reason.Translate(),pawn,MessageTypeDefOf.RejectInput,false);return false;
        }
        var previous=LaunchArc;var previousPawn=HandlingPawn;LaunchArc=arc;HandlingPawn=pawn;
        try
        {
            return JumpUtility.DoJump(pawn,verb.CurrentTarget,verb.ReloadableCompSource,verb.verbProps,
                pawnFlyerOverride:DefDatabase<ThingDef>.GetNamed("RL_LayerJumpFlyer"));
        }
        finally{LaunchArc=previous;HandlingPawn=previousPawn;}
    }
    internal static IntVec3 Cell(LayerMapComponent owner,ShotPoint point)
    {
        int layer=owner.Layout!.ProjectionLayer(point.Height);
        var cell=new LayerCell(layer,(int)Math.Floor(point.X),(int)Math.Floor(point.Z));
        return owner.Layout.Contains(cell)?owner.ToStorage(cell):IntVec3.Invalid;
    }
}
