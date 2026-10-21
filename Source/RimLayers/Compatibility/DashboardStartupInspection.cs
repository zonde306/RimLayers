using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>Keep the native developer asset audit from aborting play-data initialization
/// on one confirmed incompatible Dashboard field. No gameplay or Dashboard behavior is replaced.</summary>
[HarmonyPatch]
internal static class DashboardStartupInspection
{
    private static int skipped;
    internal static int SkippedFields => skipped;

    [HarmonyPrepare]
    private static bool Prepare() => ModLister.GetActiveModWithIdentifier("rimiris.dashboard") != null;

    private static MethodBase TargetMethod() => typeof(StaticConstructorOnStartupUtility)
        .GetNestedTypes(BindingFlags.NonPublic)
        .SelectMany(AccessTools.GetDeclaredMethods)
        .Single(m => m.Name.StartsWith("<ReportProbablyMissingAttributes>", StringComparison.Ordinal) &&
            m.ReturnType == typeof(bool) && m.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(FieldInfo));

    private static bool Prefix(FieldInfo __0, ref bool __result)
    {
        if (__0.Name != "savedLevels" || __0.DeclaringType?.FullName != "Rimiris.Dashboard.Net.ConsoleTap" ||
            __0.DeclaringType.Assembly.GetName().Name != "RimirisDashboard") return true;
        try { _ = __0.FieldType; return true; }
        catch (TypeLoadException ex) when (ex.Message.Contains("UnityEngine.StackTraceLogType", StringComparison.Ordinal))
        {
            __result = false;
            if (Interlocked.Increment(ref skipped) == 1)
                Log.Warning("[RimLayers] Developer asset audit skipped RimirisDashboard: Rimiris.Dashboard.Net.ConsoleTap.savedLevels references missing UnityEngine.StackTraceLogType. Native material, menu and atlas initialization will continue. Update Dashboard for this runtime; its console stack-level feature remains incompatible.");
            return false;
        }
    }
}
