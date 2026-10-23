using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Verse;

namespace RimLayers.Generation;

/// <summary>Prepared field swaps. Old allocations remain alive until the map has passed validation.</summary>
internal sealed class ExpansionBuffers
{
    private sealed record Change(object Owner,FieldInfo Field,object? Before,object? After);
    private readonly List<Change> changes=new();
    private readonly List<Action> releaseOld=new(),releaseNew=new(),beforeRestore=new();
    internal void OnCommit(Action action)=>releaseOld.Add(action);
    internal void OnRestore(Action action)=>beforeRestore.Add(action);
    private readonly Dictionary<Array,Array> arrays=new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyList<string> Inventory=>changes.Select(c=>$"{c.Owner.GetType().FullName}.{c.Field.Name}: {Describe(c.Before)} -> {Describe(c.After)}").ToList();
    internal bool Has(object owner,FieldInfo field)=>changes.Any(c=>ReferenceEquals(c.Owner,owner)&&c.Field==field);
    internal void Set(object owner,string name,object? value)=>Set(owner,AccessTools.Field(owner.GetType(),name),value);
    internal void Set(object owner,FieldInfo field,object? value)
    {
        int index=changes.FindIndex(c=>ReferenceEquals(c.Owner,owner)&&c.Field==field);
        if(index>=0){changes[index]=changes[index] with{After=value};return;}
        changes.Add(new(owner,field,field.GetValue(owner),value));
    }
    internal void Install(object owner,string name,object value)
    {Set(owner,name,value);AccessTools.Field(owner.GetType(),name).SetValue(owner,value);}
    internal void GrowArray(object owner,FieldInfo field,Array old,int count)
    {
        if(!arrays.TryGetValue(old,out var next))
        {
            next=Array.CreateInstance(old.GetType().GetElementType()!,count);Array.Copy(old,next,old.Length);arrays.Add(old,next);
            if(owner is ThingGrid)for(int i=old.Length;i<count;i++)next.SetValue(new List<Thing>(4),i);
        }
        Set(owner,field,next);
    }
    internal void GrowStorageGroups(RimWorld.HaulDestinationManager owner,int width,int oldHeight,int newHeight)
    {
        var field=AccessTools.Field(owner.GetType(),"groupGrid");var old=(RimWorld.SlotGroup[,,])field.GetValue(owner)!;
        if(old.GetLength(0)!=width||old.GetLength(1)!=1||old.GetLength(2)!=oldHeight)throw new InvalidOperationException("Unexpected storage grid dimensions.");
        var next=new RimWorld.SlotGroup[width,1,newHeight];
        for(int x=0;x<width;x++)for(int z=0;z<oldHeight;z++)next[x,0,z]=old[x,0,z];
        Set(owner,field,next);
    }
    internal void GrowNative<T>(object owner,string name,int oldCount,int newCount) where T:unmanaged
    {
        var field=AccessTools.Field(owner.GetType(),name);var old=(NativeArray<T>)field.GetValue(owner)!;
        if(old.Length!=oldCount)throw new InvalidOperationException($"Unexpected native grid {owner.GetType().Name}.{name}: {old.Length} != {oldCount}");
        var next=new NativeArray<T>(newCount,Allocator.Persistent);
        NativeArray<T>.Copy(old,next,oldCount);Set(owner,field,next);
        releaseOld.Add(()=>old.Dispose());releaseNew.Add(()=>next.Dispose());
    }
    /// <summary>
    /// Registers a prepared native replacement for a declared per-cell buffer, so it joins the same
    /// transaction as the native grids: the old allocation survives validation and is released on
    /// commit, or the new one is released on rollback. Reallocation itself lives in
    /// <see cref="RimLayers.Compatibility.LayerCellBufferCompatibility.Reallocate"/> and needs the
    /// field to be declared first, because only the owning mod knows the index convention.
    /// </summary>
    internal void AdoptNative(object owner,FieldInfo field,object old,object next)
    {
        Set(owner,field,next);
        if(old is IDisposable previous)releaseOld.Add(previous.Dispose);
        if(next is IDisposable created)releaseNew.Add(created.Dispose);
    }
    internal void GrowBits(object owner,string name,int oldCount,int newCount,bool unsafeBits=false)
    {
        var field=AccessTools.Field(owner.GetType(),name);
        if(unsafeBits)
        {
            var old=(UnsafeBitArray)field.GetValue(owner)!;
            if(old.Length<oldCount)throw new InvalidOperationException("Truncated dirty bit grid.");
            var next=new UnsafeBitArray(newCount,Allocator.Persistent);
            for(int i=0;i<oldCount;i++)if(old.IsSet(i))next.Set(i,true);
            Set(owner,field,next);releaseOld.Add(()=>old.Dispose());releaseNew.Add(()=>next.Dispose());
        }
        else
        {
            var old=(NativeBitArray)field.GetValue(owner)!;
            if(old.Length<oldCount)throw new InvalidOperationException("Truncated native bit grid.");
            var next=new NativeBitArray(newCount,Allocator.Persistent);
            next.Copy(0,ref old,0,oldCount);Set(owner,field,next);
            releaseOld.Add(()=>old.Dispose());releaseNew.Add(()=>next.Dispose());
        }
    }
    internal void Apply(){foreach(var c in changes)c.Field.SetValue(c.Owner,c.After);}
    internal void Restore(){foreach(var action in beforeRestore)action();for(int i=changes.Count-1;i>=0;i--)changes[i].Field.SetValue(changes[i].Owner,changes[i].Before);}
    internal void Commit(){foreach(var release in releaseOld)release();releaseOld.Clear();releaseNew.Clear();}
    internal void Discard(){foreach(var release in releaseNew)release();releaseOld.Clear();releaseNew.Clear();}
    private static string Describe(object? value)=>value is Array a?$"{a.GetType().Name}[{a.Length}]":value?.GetType().Name??"null";
}
