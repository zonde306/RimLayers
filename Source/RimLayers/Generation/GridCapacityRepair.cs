// Adapted from ProjectZ GridFixer.cs (MIT), copyright (c) 2026 Lin (peren717).
// See ThirdPartyNotices.md. Repairs prefix-preserving grids after generation or a prepared legacy-map load.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace RimLayers.Generation
{
    // Grids made from map.Size while the map was shrunk for generation (areas from AddStartingAreas, lazily made
    // scratch grids such as FloodFiller.parentGrid ...) have only the ground rows. After the size is restored they are
    // resized to the full map, keeping their ground contents (indices are unchanged because the width is unchanged).
    // Native legacy Load grids with their own saved size use the same repair; serialized byte grids are padded separately.
    // Searched: the map's fields, the fields of those objects (two levels), areas, zones, plans and map components.
    internal static class GridCapacityRepair
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static int Fix(Map map, int groundCells)
        {
            int fixedCount = 0;
            HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            List<object> roots = new List<object> { map };
            foreach (FieldInfo f in typeof(Map).GetFields(Inst))
            {
                if (!f.FieldType.IsValueType && f.FieldType != typeof(string))
                {
                    object? v = f.GetValue(map);
                    if (v != null)
                    {
                        roots.Add(v);
                    }
                }
            }
            if (map.areaManager?.AllAreas != null)
            {
                roots.AddRange(map.areaManager.AllAreas);
            }
            if (map.zoneManager?.AllZones != null)
            {
                roots.AddRange(map.zoneManager.AllZones);
            }
            if (map.planManager?.AllPlans != null)
            {
                roots.AddRange(map.planManager.AllPlans);
            }
            if (map.components != null)
            {
                roots.AddRange(map.components);
            }
            foreach (object root in roots)
            {
                if (root == null || !seen.Add(root) || root is IEnumerable && root is not Area)
                {
                    continue;
                }
                for (Type? t = root.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (FieldInfo f in t.GetFields(Inst | BindingFlags.DeclaredOnly))
                    {
                        // NativeArray<T> is a struct, so a blanket value-type skip would hide exactly the
                        // per-cell buffers a mod can declare. Keep skipping plain scalars only.
                        if (f.FieldType.IsValueType && !f.FieldType.IsGenericType)
                        {
                            continue;
                        }
                        object? v;
                        try
                        {
                            v = f.GetValue(root);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (v != null && TryFix(v, map, groundCells))
                        {
                            fixedCount++;
                            Log.Message($"resized {t.Name}.{f.Name} ({v.GetType().Name}) to the full map");
                        }
                        // Plain per-cell arrays indexed by cellIndices (z-major, e.g. MixedBiomeMapComponent.biomeGrid):
                        // the ground part is a prefix of the full array.
                        else if (v is Array arr && IsCellBuffer(arr, groundCells))
                        {
                            Array full = GrowCells(arr, groundCells, map.cellIndices.NumGridCells);
                            f.SetValue(root, full);
                            fixedCount++;
                            Log.Message($"resized {t.Name}.{f.Name} ({arr.GetType().Name}[{groundCells}]) to the full map");
                        }
                        // A NativeArray a mod declared through LayerCellBufferCompatibility (e.g. VVRace's mana
                        // buffers): same prefix rule, but the element type is only known at runtime.
                        else if (v != null &&
                                 RimLayers.Compatibility.LayerCellBufferCompatibility.IsDeclared(root.GetType(), f, v, groundCells, out Type native))
                        {
                            f.SetValue(root, RimLayers.Compatibility.LayerCellBufferCompatibility.Reallocate(native, v, map.cellIndices.NumGridCells));
                            if (v is IDisposable released) released.Dispose();
                            fixedCount++;
                            Log.Message($"resized {t.Name}.{f.Name} ({native.Name}[{groundCells}]) to the full map");
                        }
                        // Generation cannot roll back, so an unrecognised native per-cell buffer is reported
                        // instead of silently staying ground-sized (which would be an unchecked out-of-range
                        // write as soon as anything addresses an appended band).
                        else if (v != null &&
                                 v.GetType().Namespace?.StartsWith("Unity.Collections", StringComparison.Ordinal) == true &&
                                 (v.GetType().GetProperty("IsCreated")?.GetValue(v) is not bool created || created) &&
                                 v.GetType().GetProperty("Length")?.GetValue(v) is int nativeLength &&
                                 nativeLength >= groundCells && nativeLength <= groundCells + 63)
                        {
                            Log.Warning($"[RimLayers] Unresized native per-cell buffer {t.Name}.{f.Name} ({v.GetType().Name}, {nativeLength} cells). " +
                                        "Declare it through LayerCellBufferCompatibility.RegisterNativeCellBuffer so it grows with the map.");
                        }
                    }
                }
            }
            return fixedCount;
        }

        // A per-cell buffer keeps the cell dimension last: T[cells], T[rows, cells].
        // Rank-1 arrays were the original prefix case; rank-2 matrices (Dubs' PipeGrid is
        // int[pipeTypes, cells]) and rank-1 arrays of references (a RoofDef[] roof copy) follow
        // the same rule, so they are grown here instead of staying ground-sized. Arrays keyed by
        // sections (bool[sectionsX, sectionsZ]) do not match because their last dimension is not
        // the cell count, so they are left to their owner.
        internal static bool IsCellBuffer(Array array, int cells) =>
            array.Rank == 1 ? array.Length == cells : array.Rank == 2 && array.GetLength(1) == cells;

        internal static Array GrowCells(Array old, int oldCount, int newCount)
        {
            Type element = old.GetType().GetElementType()!;
            if (old.Rank == 1)
            {
                Array flat = Array.CreateInstance(element, newCount);
                Array.Copy(old, flat, oldCount);
                return flat;
            }
            int rows = old.GetLength(0);
            Array next = Array.CreateInstance(element, rows, newCount);
            // A multi-dimensional array is contiguous row-major, so each row is one block copy
            // (Array.Copy handles both value and reference element types).
            for (int row = 0; row < rows; row++)
            {
                Array.Copy(old, row * oldCount, next, row * newCount, oldCount);
            }
            return next;
        }

        // ByteGrid / IntGrid / CellGrid keep their size in private mapSizeX / mapSizeZ.
        private static bool IsGround(object grid, Map map, int groundCells)
        {
            Type t = grid.GetType();
            FieldInfo? fx = t.GetField("mapSizeX", Inst);
            FieldInfo? fz = t.GetField("mapSizeZ", Inst);
            if (fx == null || fz == null)
            {
                return false;
            }
            int x = (int)fx.GetValue(grid)!;
            int z = (int)fz.GetValue(grid)!;
            return x == map.Size.x && x > 0 && z == groundCells / x;
        }

        private static bool TryFix(object grid, Map map, int groundCells)
        {
            switch (grid)
            {
                case BoolGrid b when b.Width == map.Size.x && b.Height == groundCells / map.Size.x:
                {
                    List<IntVec3> on = new List<IntVec3>(b.ActiveCells);
                    b.ClearAndResizeTo(map);
                    foreach (IntVec3 c in on)
                    {
                        b[c] = true;
                    }
                    return true;
                }
                case ByteGrid g when IsGround(g, map, groundCells):
                {
                    byte[] old = new byte[groundCells];
                    for (int i = 0; i < groundCells; i++)
                    {
                        old[i] = g[i];
                    }
                    g.ClearAndResizeTo(map);
                    for (int i = 0; i < groundCells; i++)
                    {
                        g[i] = old[i];
                    }
                    return true;
                }
                case IntGrid g when IsGround(g, map, groundCells):
                {
                    int[] old = new int[groundCells];
                    for (int i = 0; i < groundCells; i++)
                    {
                        old[i] = g[i];
                    }
                    g.ClearAndResizeTo(map);
                    for (int i = 0; i < groundCells; i++)
                    {
                        g[i] = old[i];
                    }
                    return true;
                }
                case CellGrid g when IsGround(g, map, groundCells):
                {
                    IntVec3[] old = new IntVec3[groundCells];
                    for (int i = 0; i < groundCells; i++)
                    {
                        old[i] = g[i];
                    }
                    g.ClearAndResizeTo(map);
                    for (int i = 0; i < groundCells; i++)
                    {
                        g[i] = old[i];
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
