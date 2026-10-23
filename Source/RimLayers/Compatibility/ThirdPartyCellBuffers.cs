using System.Reflection;
using RimLayers.Generation;
using Unity.Collections;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Registration point for other mods' private per-cell buffers.
///
/// <para><see cref="LiveMapExpansion"/> appends coordinate rows to one existing <see cref="Map"/>:
/// the width never changes, so a buffer indexed by <see cref="CellIndices"/> (index = z * width + x)
/// keeps every original index and only needs its tail extended. A buffer indexed x-major
/// (index = x * height + z, as the native river flow map is) is NOT a prefix and must be remapped
/// by its owner, not by this class.</para>
///
/// <para>Reflection cannot tell either convention apart, and picking wrong silently corrupts data,
/// so expansion refuses unknown native buffers instead of guessing. A mod (or a compatibility
/// adapter such as <see cref="VVRaceCompatibility"/>) declares its own storage-indexed fields here;
/// the declared fields are then grown with their prefix preserved and the appended rows zero-filled.</para>
/// </summary>
public static class LayerCellBufferCompatibility
{
    private sealed class Rule
    {
        internal string Mod = "";
        internal readonly HashSet<string> Fields = new(StringComparer.Ordinal);
        internal Action<object>? BeforeResize;
    }

    private static readonly Dictionary<Type, Rule> rules = new();

    /// <summary>
    /// Declares storage-indexed (<see cref="CellIndices"/>-indexed) <see cref="NativeArray{T}"/> fields
    /// of <paramref name="componentType"/> that must follow a map expansion.
    /// The fields must be exactly <c>NumGridCells</c> long and indexed like <see cref="CellIndices"/>.
    /// </summary>
    public static void RegisterNativeCellBuffer(Type componentType, string modName, params string[] fields)
    {
        if (fields.Length == 0) throw new ArgumentException("At least one field is required.", nameof(fields));
        var rule = RuleFor(componentType);
        rule.Mod = modName;
        foreach (var field in fields) rule.Fields.Add(field);
    }

    /// <summary>
    /// Runs immediately before <paramref name="componentType"/>'s declared buffers are replaced.
    /// Use it to settle work (jobs, pending writes) that still holds the old allocations.
    /// Called on the main thread, inside the expansion transaction, before any field is read.
    /// </summary>
    public static void RegisterBeforeResize(Type componentType, Action<object> hook) => RuleFor(componentType).BeforeResize = hook;

    /// <summary>Declared field names, for diagnostics.</summary>
    public static IReadOnlyCollection<string> DeclaredFields(Type componentType) =>
        RuleFor(componentType, create: false)?.Fields.ToArray() ?? Array.Empty<string>();

    public static string? DeclaringMod(Type componentType) => RuleFor(componentType, create: false)?.Mod;

    private static Rule RuleFor(Type type, bool create = true)
    {
        if (rules.TryGetValue(type, out var existing)) return existing;
        if (!create) return null!;
        var rule = new Rule();
        rules[type] = rule;
        return rule;
    }

    /// <summary>Closest declared rule, walking base types (a subclass may add fields above its parent's).</summary>
    private static Rule? Find(Type type)
    {
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            if (rules.TryGetValue(t, out var rule)) return rule;
        return null;
    }

    /// <summary>Transaction seam: settle a registered component's in-flight work before its buffers move.</summary>
    internal static void Prepare(object owner)
    {
        var rule = Find(owner.GetType());
        rule?.BeforeResize?.Invoke(owner);
    }

    /// <summary>
    /// Grows one declared native buffer. Returns false when the field is not declared or is not an
    /// exactly-sized <see cref="NativeArray{T}"/>, so the caller can keep its safe refusal for the rest.
    /// </summary>
    internal static bool TryGrow(ExpansionBuffers changes, object owner, FieldInfo field, object value, int oldCount, int newCount)
    {
        if (!IsDeclared(owner.GetType(), field, value, oldCount, out var type)) return false;
        var next = Reallocate(type, value, newCount);
        changes.AdoptNative(owner, field, value, next);
        return true;
    }

    /// <summary>True when <paramref name="value"/> is a declared, exactly-sized <see cref="NativeArray{T}"/>.</summary>
    internal static bool IsDeclared(Type ownerType, FieldInfo field, object value, int oldCount, out Type type)
    {
        type = value.GetType();
        var rule = Find(ownerType);
        if (rule == null || !rule.Fields.Contains(field.Name)) return false;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(NativeArray<>)) return false;
        return type.GetProperty("Length", typeof(int))?.GetValue(value) as int? == oldCount;
    }

    /// <summary>
    /// Allocates the replacement and copies the original prefix. The appended rows stay zero-filled,
    /// which is the correct "no data yet" value for a newly appended storage band.
    /// </summary>
    internal static object Reallocate(Type type, object source, int newCount)
    {
        var next = Activator.CreateInstance(type, new object[] { newCount, Allocator.Persistent, NativeArrayOptions.ClearMemory })
            ?? throw new NotSupportedException($"Could not reallocate {type}.");
        var copy = type.GetMethod("Copy", BindingFlags.Public | BindingFlags.Static, null, new[] { type, type, typeof(int) }, null);
        if (copy != null) copy.Invoke(null, new[] { source, next, (object)NewCount(source) });
        else
        {
            // Same closed generic on both sides, so one indexer property serves both.
            var indexer = type.GetProperty("Item", new[] { typeof(int) })!;
            for (int i = 0; i < NewCount(source); i++) indexer.SetValue(next, indexer.GetValue(source, new object[] { i }), new object[] { i });
        }
        return next;
    }

    private static int NewCount(object source) => (int)source.GetType().GetProperty("Length", typeof(int))!.GetValue(source)!;
}
