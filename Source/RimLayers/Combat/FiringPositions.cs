using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using RimWorld;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimLayers.Combat;

/// <summary>Enumerate logical floors, then reuse native cell policy and preference, not a second AI policy.</summary>
[HarmonyPatch(typeof(CastPositionFinder),nameof(CastPositionFinder.TryFindCastPosition))]
internal static class LayerFiringPositionPatch
{
    internal sealed class Search
    {
        internal LayerMapComponent Owner=null!;
        internal Pawn Caster=null!;
        internal float TravelCost;
        internal float Exposure=1;
    }
    [ThreadStatic] internal static Search? Current;
    private static readonly Action<IntVec3> Evaluate=AccessTools.MethodDelegate<Action<IntVec3>>(
        AccessTools.Method(typeof(CastPositionFinder),"EvaluateCell"));
    private static readonly Dictionary<string,FieldInfo> Fields=typeof(CastPositionFinder)
        .GetFields(BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).ToDictionary(f=>f.Name);
    private static void Set(string name,object? value)=>Fields[name].SetValue(null,value);
    private static T Get<T>(string name)=>(T)Fields[name].GetValue(null)!;

    static bool Prefix(CastPositionRequest newReq,ref IntVec3 dest,ref bool __result)
    {
        var owner=LayerMapComponent.For(newReq.caster?.Map);
        if(owner==null || newReq.verb is not Verb_LaunchProjectile || newReq.target?.Map!=owner.map) return true;
        var previous=Current;var context=CombatCoordinates.Context;
        Current=new(){Owner=owner,Caster=newReq.caster!};CombatCoordinates.Context=owner.map;
        try
        {
            __result=Find(owner,newReq,out dest);
            return false;
        }
        finally
        {
            Set("req",default(CastPositionRequest));Set("verb",null);
            Current=previous;CombatCoordinates.Context=context;
        }
    }
    private static bool Find(LayerMapComponent owner,CastPositionRequest request,out IntVec3 result)
    {
        result=IntVec3.Invalid;
        if(!owner.TryDecode(request.caster.Position,out var caster) || !owner.TryDecode(request.target.Position,out var target))
            return false;
        if(Math.Abs(request.maxRangeFromTarget-request.verb.EffectiveRange)<0.001f)
            request.maxRangeFromTarget=owner.Combat.MaximumRange(request.verb);
        Set("req",request);Set("casterLoc",request.caster.Position);Set("targetLoc",request.target.Position);
        Set("verb",request.verb);
        Set("avoidGrid",request.caster.TryGetAvoidGrid(out var grid,onlyIfLordAllows:false) ?
            grid.Grid : default(NativeArray<byte>.ReadOnly));
        Set("bestSpot",IntVec3.Invalid);Set("bestSpotPref",0.001f);
        Set("maxRangeFromCasterSquared",request.maxRangeFromCaster*request.maxRangeFromCaster);
        Set("maxRangeFromTargetSquared",request.maxRangeFromTarget*request.maxRangeFromTarget);
        Set("maxRangeFromLocusSquared",request.maxRangeFromLocus*request.maxRangeFromLocus);
        int dx=caster.X-target.X,dz=caster.Z-target.Z;
        Set("rangeFromTargetSquared",(float)(dx*dx+dz*dz));Set("rangeFromTarget",Mathf.Sqrt(dx*dx+dz*dz));
        Set("optimalRangeSquared",request.verb.EffectiveRange*0.8f*(request.verb.verbProps.range*0.8f));
        Set("rangeFromCasterToCellSquared",0f);
        if(!MarkRegions(owner,request)) return false;

        bool Test(IntVec3 cell)
        {
            if(!owner.Structure.HasSurface(cell) || cell.GetRegion(owner.map) is not {} region || region.mark!=Get<int>("inRadiusMark")) return false;
            // Cheap range/LOS before the native cell's more expensive actual route.
            if(!request.verb.CanHitTargetFrom(cell,request.target)) return false;
            if(LayerAIFiringVisibilityPatch.Active&&!LayerAIFiringVisibilityPatch.AutomaticLine(request.caster,cell,request.target,request.verb))return false;
            owner.TryDecode(cell,out var logical);
            int x=logical.X-caster.X,z=logical.Z-caster.Z;
            Set("rangeFromCasterToCellSquared",(float)(x*x+z*z));
            Current!.Exposure=CombatCoordinates.Cross(owner.map,cell,request.target.Position)==null?1:owner.Combat.SolveWeapon(request.caster,cell,request.target,request.verb).Exposure;
            Evaluate(cell);
            return Get<IntVec3>("bestSpot")==cell;
        }
        if(request.preferredCastPosition is {} preferred && preferred.IsValid && Test(preferred))
        { result=preferred;return true; }
        if(Test(request.caster.Position) && (!request.wantCoverFromTarget || Get<float>("bestSpotPref")>=1))
        { result=request.caster.Position;return true; }

        float radius=Math.Min(owner.Combat.MaximumRange(request.verb),
            request.maxRangeFromTarget>0.01f ? request.maxRangeFromTarget : owner.Combat.MaximumRange(request.verb));
        int r=(int)Math.Ceiling(radius);
        CellRect local=new(target.X-r,target.Z-r,2*r+1,2*r+1);
        local.ClipInsideRect(new CellRect(0,0,owner.Layout!.Width,owner.Layout.Height));
        if(request.maxRangeFromCaster>0.01f)
        {
            int bound=(int)Math.Ceiling(request.maxRangeFromCaster);
            local.ClipInsideRect(new CellRect(caster.X-bound,caster.Z-bound,2*bound+1,2*bound+1));
        }
        if(request.maxRangeFromLocus>0.01f && owner.TryDecode(request.locus,out var locus))
        {
            int bound=(int)Math.Ceiling(request.maxRangeFromLocus);
            local.ClipInsideRect(new CellRect(locus.X-bound,locus.Z-bound,2*bound+1,2*bound+1));
        }
        var candidates=new List<IntVec3>();
        for(int layer=0;layer<owner.Layout.LayerCount;layer++)
            foreach(var cell in local)
            {
                IntVec3 storage=owner.ToStorage(new LayerCell(layer,cell.x,cell.z));
                if(owner.Structure.HasSurface(storage)) candidates.Add(storage);
            }
        foreach(var cell in candidates.OrderBy(c=>
        {
            owner.TryDecode(c,out var logical);
            int x=logical.X-caster.X,z=logical.Z-caster.Z;
            return x*x+z*z+owner.Layout!.Distance(logical.LayerId,caster.LayerId)*49;
        })) Test(cell);
        result=Get<IntVec3>("bestSpot");
        return result.IsValid;
    }

    private static bool MarkRegions(LayerMapComponent owner,CastPositionRequest request)
    {
        Region? start=request.caster.Position.GetRegion(owner.map);
        if(start==null) return false;
        var vertical=new Dictionary<Region,List<Region>>();
        foreach(var edge in owner.Connectors.MovementEdges(TraverseParms.For(request.caster,Danger.Some)))
        {
            var from=edge.From.GetRegion(owner.map);var to=edge.To.GetRegion(owner.map);
            if(from==null || to==null) continue;
            if(!vertical.TryGetValue(from,out var list)) vertical[from]=list=new();
            list.Add(to);
        }
        Region? locus=request.maxRegions>0 && request.maxRangeFromLocus>0.01f ? request.locus.GetRegion(owner.map) : null;
        if(request.maxRegions>0 && request.maxRangeFromLocus>0.01f && locus==null) return false;
        int mark=Rand.Int;Set("inRadiusMark",mark);
        var queue=new Queue<Region>();queue.Enqueue(start);
        var visited=new HashSet<Region>{start};
        int count=0;int budget=request.maxRegions>0 ? request.maxRegions : int.MaxValue;bool locusReached=locus==null;
        while(queue.Count>0 && (count<budget || !locusReached))
        {
            Region region=queue.Dequeue();region.mark=mark;count++;
            if(region==locus) locusReached=true;
            IEnumerable<Region> next=region.Neighbors;
            if(vertical.TryGetValue(region,out var extra)) next=next.Concat(extra);
            foreach(var neighbor in next)
                if((neighbor.type&RegionType.Set_Passable)!=0 && visited.Add(neighbor)) queue.Enqueue(neighbor);
        }
        return true;
    }

    public static bool Reach(Reachability original,IntVec3 start,LocalTargetInfo target,PathEndMode mode,TraverseParms parms)
    {
        if(Current==null) return original.CanReach(start,target,mode,parms);
        using var path=Current.Owner.map.pathFinder.FindPathNow(start,target,parms,null,mode);
        Current.TravelCost=path.Found ? path.TotalCost : float.PositiveInfinity;
        return path.Found;
    }
}

[HarmonyPatch(typeof(CastPositionFinder),"CastPositionPreference")]
internal static class LayerFiringTravelPreferencePatch
{
    static void Postfix(ref float __result)
    {
        var search=LayerFiringPositionPatch.Current;
        if(search!=null && __result>0) __result*=Mathf.Pow(0.999f,search.TravelCost)*Math.Max(0.01f,search.Exposure);
    }
}
