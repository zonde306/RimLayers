using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLayers.Core;
using UnityEngine;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Dubs Mint Minimap assumes <see cref="Map.Size"/> and <see cref="Map.AllCells"/> describe the
/// whole playable map. A layered map stores every floor inside one coordinate band, so that mod
/// allocates a 120x408 texture, draws a 3.4:1 minimap, writes its own per-cell array past the end
/// from its <c>Section.TryUpdate</c> prefix, and clamps every marker to the band edge.
///
/// Nothing here changes <see cref="Map.Size"/>, the minimap assembly, or saved state. While the
/// minimap's own call stack is active on this thread, sizes and cells are answered in floor-local
/// coordinates; <c>procCell</c> translates back to storage once, so every native grid query still
/// reads the real world.
/// </summary>
internal static class DubsMinimapCompatibility
{
    internal const string PackageId = "dubwise.dubsmintminimap";

    [ThreadStatic] private static int depth;
    [ThreadStatic] private static int viewedLayer;
    [ThreadStatic] private static int stride;

    /// <summary>True while a redirected minimap call is running on this thread.</summary>
    internal static bool Active => depth > 0;
    internal static int RedirectedCalls { get; private set; }
    internal static int IndexMisses { get; private set; }

    internal static LayerMapComponent? Owner(Map? map) => map == null ? null : LayerMapComponent.For(map);

    [ThreadStatic] private static int lastMapId;
    [ThreadStatic] private static int lastViewed;

    /// <summary>Set when the drawn floor changed and the cached texture must be rebuilt.</summary>
    internal static bool RefreshPending { get; private set; }

    internal static void Enter(Map? map)
    {
        if (depth++ > 0) return;
        var owner = Owner(map);
        viewedLayer = owner?.ViewedLayer ?? 0;
        stride = owner?.Layout?.Stride ?? 0;
        RedirectedCalls++;
        if (owner?.Layout == null || map == null) return;
        // dirty flags are keyed by floor-local index, so a floor switch leaves the cached pixels
        // showing the previous floor. Ask the minimap to rebuild its texture once.
        if (lastMapId != map.uniqueID || lastViewed != viewedLayer)
        {
            lastMapId = map.uniqueID;
            lastViewed = viewedLayer;
            RefreshPending = true;
        }
    }

    internal static void ConsumeRefresh() => RefreshPending = false;

    internal static void Exit() => depth = Math.Max(0, depth - 1);

    /// <summary>Logical size of the viewed floor; the native size for an unlayered map.</summary>
    internal static IntVec3 Size(Map map) => Owner(map)?.Layout is { } layout
        ? new IntVec3(layout.Width, 1, layout.Height) : map.Size;

    /// <summary>
    /// Viewed-floor cells in local coordinates, in the same z-then-y-then-x order as the native
    /// enumerator, so the minimap's running index stays aligned with the arrays it sizes from this.
    /// </summary>
    internal static IEnumerable<IntVec3> Cells(Map map)
    {
        if (Owner(map)?.Layout is not { } layout)
        {
            for (int z = 0; z < map.Size.z; z++)
                for (int y = 0; y < map.Size.y; y++)
                    for (int x = 0; x < map.Size.x; x++)
                        yield return new IntVec3(x, y, z);
            yield break;
        }
        for (int z = 0; z < layout.Height; z++)
            for (int x = 0; x < layout.Width; x++)
                yield return new IntVec3(x, 0, z);
    }

    /// <summary>Cell count the minimap must size its flat per-cell arrays with.</summary>
    internal static int CellCount(Map map) => Owner(map)?.Layout is { } layout
        ? layout.Width * layout.Height : map.Area;

    /// <summary>
    /// Flat index for a cell already expressed in the viewed floor's local coordinates. Every
    /// redirected caller enumerates <see cref="Cells"/>, so this never needs a stride offset.
    /// Out-of-floor cells fold to 0 rather than reporting an error: a wrong dirty flag only costs
    /// one extra recompute, while an out-of-range write would abort the whole map update.
    /// </summary>
    internal static int LocalIndex(Map map, IntVec3 local)
    {
        if (Owner(map)?.Layout is not { } layout) return map.cellIndices.CellToIndex(local);
        if ((uint)local.x >= (uint)layout.Width || (uint)local.z >= (uint)layout.Height)
        {
            // Only reachable for a cell outside the drawn floor. Folding is safe: a wrong dirty
            // flag costs one recompute, an out-of-range write would abort the whole map update.
            IndexMisses++;
            return 0;
        }
        return local.z * layout.Width + local.x;
    }

    /// <summary>Local cell to real storage cell, applied once at the procCell entry.</summary>
    internal static IntVec3 ToStorage(Map map, IntVec3 local)
    {
        if (Owner(map)?.Layout is not { } layout || (uint)local.z >= (uint)layout.Height || (uint)local.x >= (uint)layout.Width)
            return local;
        return new IntVec3(local.x, 0, viewedLayer * layout.Stride + local.z);
    }

    /// <summary>
    /// A thing's draw position folded onto the viewed floor's footprint. Every floor overlays the
    /// same rectangle, which is what makes one minimap usable for locating things across layers.
    /// </summary>
    internal static Vector3 LocalDrawPos(Thing thing)
    {
        var owner = Owner(thing.Map);
        if (owner?.Layout == null || !owner.TryDecode(thing.PositionHeld, out var logical)) return thing.DrawPos;
        // Keep sub-cell animation offsets (tweens, thrown motes, in-flight projectiles) intact.
        return new Vector3(thing.DrawPos.x, thing.DrawPos.y, logical.Z + (thing.DrawPos.z - thing.PositionHeld.z));
    }

    /// <summary>The viewed floor's camera position in floor-local z.</summary>
    internal static Vector3 LocalCameraPosition(Vector3 real, Map? map) =>
        Owner(map)?.Layout is { } layout ? real - new Vector3(0, 0, viewedLayer * layout.Stride) : real;

    internal static Vector3 LocalRootPos(CameraDriver driver)
    {
        var root = AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos")(driver);
        return LocalCameraPosition(root, Find.CurrentMap);
    }

    /// <summary>Re-apply the floor offset to a floor-local jump target.</summary>
    internal static Vector3 StorageJumpTarget(Vector3 local, Map? map) =>
        Owner(map)?.Layout is { } layout ? local + new Vector3(0, 0, viewedLayer * layout.Stride) : local;

    internal static IntVec3 StorageJumpTarget(IntVec3 local, Map? map) =>
        Owner(map)?.Layout is { } layout && (uint)local.z < (uint)layout.Height && (uint)local.x < (uint)layout.Width
            ? new IntVec3(local.x, 0, viewedLayer * layout.Stride + local.z) : local;
}

/// <summary>
/// Redirects the minimap's geometry reads. Only members of the minimap assembly are patched, and
/// each carries a thread-scoped guard, so nothing else in the game sees a translated size.
/// The minimap's own <c>Section.TryUpdate</c> prefix is included because it converts cells to flat
/// indices with the native storage layout.
/// </summary>
[HarmonyPatch]
internal static class DubsMinimapGeometryPatch
{
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        if (AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap") is { } window)
            foreach (var method in AccessTools.GetDeclaredMethods(window))
                if (method.Name is "UpdateMapTex" or "BaseMapRectSize" or "Minimap" or "DrawCam" or "DrawAllPawns"
                    or "DrawFires" or "DrawPings" or "DrawProjectiles" or "DrawGrid" or "DrawThingLocators"
                    or "DoWindowContents")
                    yield return method;
        if (AccessTools.TypeByName("DubsMintMinimap.Harmony_TryUpdate") is { } update &&
            AccessTools.Method(update, "Prefix", new[] { typeof(Section) }) is { } prefix)
            yield return prefix;
    }

    private static void Prefix() => DubsMinimapCompatibility.Enter(Find.CurrentMap);
    private static void Finalizer() => DubsMinimapCompatibility.Exit();

    /// <summary>The field load sits one or two instructions before the call, with the cell load between.</summary>
    private static CodeInstruction? FindFieldLoad(List<CodeInstruction> list, int callIndex, string field)
    {
        for (int j = callIndex - 1; j >= 0 && j >= callIndex - 2; j--)
            if (list[j].operand is FieldInfo info && info.Name == field) return list[j];
        return null;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var size = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Size));
        var allCells = AccessTools.PropertyGetter(typeof(Map), nameof(Map.AllCells));
        var area = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Area));
        var drawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
        var cellToIndex = AccessTools.Method(typeof(CellIndices), nameof(CellIndices.CellToIndex), new[] { typeof(IntVec3) });
        var ourSize = AccessTools.Method(typeof(DubsMinimapCompatibility), nameof(DubsMinimapCompatibility.Size));
        var ourCells = AccessTools.Method(typeof(DubsMinimapCompatibility), nameof(DubsMinimapCompatibility.Cells));
        var ourCount = AccessTools.Method(typeof(DubsMinimapCompatibility), nameof(DubsMinimapCompatibility.CellCount));
        var ourIndex = AccessTools.Method(typeof(DubsMinimapCompatibility), nameof(DubsMinimapCompatibility.LocalIndex));
        var ourDrawPos = AccessTools.Method(typeof(DubsMinimapCompatibility), nameof(DubsMinimapCompatibility.LocalDrawPos));
        var ourRootPos = AccessTools.Method(typeof(DubsMinimapCompatibility), nameof(DubsMinimapCompatibility.LocalRootPos));
        var list = instructions.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var code = list[i];
            if (code.Calls(size)) code.operand = ourSize;
            else if (code.Calls(allCells)) code.operand = ourCells;
            else if (code.Calls(area)) code.operand = ourCount;
            else if (code.Calls(drawPos)) { code.opcode = OpCodes.Call; code.operand = ourDrawPos; }
            else if (code.Calls(cellToIndex) && FindFieldLoad(list, i, "cellIndices") is { } fieldLoad)
            {
                // `map.cellIndices.CellToIndex(c)` pushes the struct address between the map and
                // the cell; the cell itself is loaded in between. Dropping the field load leaves
                // the map on the stack for LocalIndex(map, localCell).
                fieldLoad.opcode = OpCodes.Nop;
                fieldLoad.operand = null;
                code.operand = ourIndex;
            }
            else if (code.opcode == OpCodes.Ldfld && code.operand is FieldInfo { Name: "rootPos", DeclaringType: var owner } &&
                     owner == typeof(CameraDriver))
            {
                // The anchor button remembers the raw camera field. Store it floor-local so the
                // later JumpToCurrentMapLoc offset is applied exactly once.
                code.opcode = OpCodes.Call;
                code.operand = ourRootPos;
            }
            yield return code;
        }
    }
}

/// <summary>
/// The minimap enumerates floor-local cells, but every native grid query needs a storage cell.
/// Translating once here keeps terrain, edifices, plants and fog reading the actual world.
/// </summary>
[HarmonyPatch]
internal static class DubsMinimapProcCellPatch
{
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    private static MethodBase? TargetMethod() =>
        AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap") is { } window
            ? AccessTools.Method(window, "procCell", new[] { typeof(IntVec3), typeof(Map) })
            : null;

    private static void Prefix(ref IntVec3 c, Map map)
    {
        if (DubsMinimapCompatibility.Active) c = DubsMinimapCompatibility.ToStorage(map, c);
    }
}

/// <summary>
/// The minimap's <c>Section.TryUpdate</c> prefix marks per-cell dirty flags for every dirty
/// section. Reserved storage bands contain no playable cells, so on a layered map those writes
/// fall outside the minimap's arrays and abort <c>MapUpdate</c> every frame until the minimap is
/// closed. Sections never straddle bands (the stride is a multiple of 17), so restricting the
/// prefix to the viewed floor removes the crash and still refreshes exactly the floor drawn.
/// </summary>
[HarmonyPatch]
internal static class DubsMinimapSectionPatch
{
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    private static MethodBase? TargetMethod() =>
        AccessTools.TypeByName("DubsMintMinimap.Harmony_TryUpdate") is { } type
            ? AccessTools.Method(type, "Prefix", new[] { typeof(Section) })
            : null;

    // The target is a static method, so `__instance` would bind to a null instance rather than
    // to the Section argument; take the argument positionally instead.
    private static bool Prefix(Section __0)
    {
        if (__0 == null) return true;
        var owner = DubsMinimapCompatibility.Owner(__0.map);
        if (owner?.Layout == null) return true;
        var viewed = owner.LayerRect(owner.ViewedLayer);
        var rect = __0.CellRect;
        return rect.minX >= viewed.minX && rect.maxX <= viewed.maxX &&
            rect.minZ >= viewed.minZ && rect.maxZ <= viewed.maxZ;
    }
}

/// <summary>
/// The minimap normalises the camera against <see cref="Map.Size"/> and jumps to the resulting
/// floor-local coordinates. Both directions need the viewed floor's offset; the guard keeps every
/// other caller of these native members untouched.
/// </summary>
/// <summary>
/// The minimap normalises the camera against <see cref="Map.Size"/>; its reported position must
/// therefore be floor-local. One postfix on the single source of that position covers the
/// minimap's own use (including <see cref="CameraDriver.MapPosition"/>, which derives from it).
/// </summary>
[HarmonyPatch]
internal static class DubsMinimapCameraPositionPatch
{
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    private static MethodBase TargetMethod() => AccessTools.PropertyGetter(typeof(CameraDriver), "CurrentRealPosition");

    private static void Postfix(ref Vector3 __result)
    {
        if (DubsMinimapCompatibility.Active) __result = DubsMinimapCompatibility.LocalCameraPosition(__result, Find.CurrentMap);
    }
}

/// <summary>
/// RimLayers' placeholder terrains are declared <c>dontRender</c>, so they never build a graphic
/// and the minimap's colour sampler lands on the engine's missing-texture material. Upper floors
/// are mostly open air, which would paint the whole minimap magenta and destroy its readability.
/// Give the two RimLayers-owned placeholders a flat colour instead; every real terrain keeps the
/// minimap's own sampling.
/// </summary>
[HarmonyPatch]
internal static class DubsMinimapPlaceholderTerrainPatch
{
    internal static readonly Color OpenAir = new(0.10f, 0.11f, 0.13f, 1f);
    internal static readonly Color Isolation = new(0.04f, 0.04f, 0.05f, 1f);

    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    private static MethodBase? TargetMethod() =>
        AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap") is { } window
            ? AccessTools.Method(window, "Blitter", new[] { typeof(TerrainDef) })
            : null;

    private static bool Prefix(TerrainDef def, ref Color __result)
    {
        if (def == RimLayersDefOf.RL_OpenAir) { __result = OpenAir; return false; }
        if (def == RimLayersDefOf.RL_Isolation) { __result = Isolation; return false; }
        return true;
    }
}

/// <summary>
/// The minimap only rebuilds its texture when the map size changes, which never happens when the
/// player switches floors: the storage size is constant. Left alone, the minimap keeps showing the
/// previous floor's pixels. After a floor change, force one full rebuild.
/// </summary>
[HarmonyPatch]
internal static class DubsMinimapFloorRefreshPatch
{
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    private static MethodBase? TargetMethod() =>
        AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap") is { } window
            ? AccessTools.Method(window, "WindowUpdate")
            : null;

    private static void Postfix()
    {
        if (!DubsMinimapCompatibility.RefreshPending) return;
        DubsMinimapCompatibility.ConsumeRefresh();
        if (AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap") is not { } window) return;
        AccessTools.Method(window, "UpdateMapTex", new[] { typeof(Map), typeof(bool) })
            ?.Invoke(null, new object[] { Find.CurrentMap, true });
    }
}

/// <summary>
/// The minimap's click-to-jump passes floor-local coordinates, so they must be lifted back into
/// storage space before the native camera clamps them to the viewed floor's band.
/// Each overload gets its own patch class: Harmony binds patch parameters by name per target, so
/// one class cannot declare two differently named parameters.
/// </summary>
[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.JumpToCurrentMapLoc), new[] { typeof(Vector3) })]
internal static class DubsMinimapCameraVectorJumpPatch
{
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    private static void Prefix(ref Vector3 loc)
    {
        if (DubsMinimapCompatibility.Active) loc = DubsMinimapCompatibility.StorageJumpTarget(loc, Find.CurrentMap);
    }
}

[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.JumpToCurrentMapLoc), new[] { typeof(IntVec3) })]
internal static class DubsMinimapCameraCellJumpPatch
{
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier(DubsMinimapCompatibility.PackageId, false) != null;

    private static void Prefix(ref IntVec3 cell)
    {
        if (DubsMinimapCompatibility.Active) cell = DubsMinimapCompatibility.StorageJumpTarget(cell, Find.CurrentMap);
    }
}
