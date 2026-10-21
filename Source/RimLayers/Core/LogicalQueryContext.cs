using Verse;

namespace RimLayers.Core;

/// <summary>Scoped arithmetic for candidate filters and rankings, never for actual Map/grid addresses.</summary>
public static class LogicalQueryContext
{
    [ThreadStatic] public static Map? Map;
}
