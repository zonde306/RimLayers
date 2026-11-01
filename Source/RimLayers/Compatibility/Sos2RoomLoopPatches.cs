using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimLayers.Compatibility;

/// <summary>
/// Stops <c>MoveShip</c> from dereferencing a room that does not exist at the destination.
/// </summary>
/// <remarks>
/// <c>MoveShip</c> remembers the temperature of every sealed source room by its *destination* cell and
/// then restores it at the end with
/// <code>
/// Room room2 = t.Item1.GetRoom(targetMap);
/// room2.Temperature = t.Item2;
/// </code>
/// There is no null check. On a single-plane map that is safe because a rotated ship lies on itself:
/// a floor cell maps to a floor cell. A multi-deck hull does not have to be symmetric, so a source
/// floor can land on the cell a beam, an engine or a hull tile occupies - those are all
/// <c>FillCategory.Full</c>, <c>GetExpectedRegionType</c> answers <c>RegionType.None</c> for them,
/// <c>RegionMaker</c> never builds a region there, and <c>GetRoom</c> returns null for the rest of the
/// map's life. The ship is already half transferred when the NRE hits, which is exactly the "flip
/// destroys the hull" report.
/// <para>
/// The repair is deliberately shallow: the setter call is redirected to a helper with an identical
/// stack signature, so the move keeps running and only the rooms that genuinely have nowhere to go
/// lose their stored temperature. No control flow, no local indices and no offsets are touched, so a
/// future SOS2 build that reorganises the method body still only costs this one patch instead of
/// silently dropping a hull. The anchor is the property setter call site, and its count is asserted:
/// a silent zero would mean the guard stopped covering anything.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class Sos2RoomLoopPatches
{
    private static MethodBase? TargetMethod() =>
        AccessTools.Method(Sos2Compatibility.ShipInteriorType, "MoveShip", new[]
        {
            typeof(Building), typeof(Map), typeof(IntVec3), typeof(Faction), typeof(byte), typeof(bool), typeof(bool)
        });

    /// <summary>Rooms whose stored state the destination could not accept, counted for the diagnostics.</summary>
    internal static int Skipped { get; private set; }

    internal static void Reset() => Skipped = 0;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? temperature = AccessTools.PropertySetter(typeof(Room), nameof(Room.Temperature));
        MethodInfo? vacuum = AccessTools.PropertySetter(typeof(Room), nameof(Room.Vacuum));
        MethodInfo? setTemperature = AccessTools.Method(typeof(Sos2RoomLoopPatches), nameof(SetTemperature));
        MethodInfo? setVacuum = AccessTools.Method(typeof(Sos2RoomLoopPatches), nameof(SetVacuum));
        if (temperature == null || vacuum == null || setTemperature == null || setVacuum == null)
            throw new InvalidOperationException("RimLayers could not resolve the room property setters.");

        int temperatures = 0, vacuums = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(temperature))
            {
                temperatures++;
                yield return Retarget(instruction, setTemperature);
                continue;
            }
            if (instruction.Calls(vacuum))
            {
                vacuums++;
                yield return Retarget(instruction, setVacuum);
                continue;
            }
            yield return instruction;
        }
        if (temperatures != 1 || vacuums != 1)
            throw new InvalidOperationException(
                $"RimLayers SOS2 room-restore anchor changed: Temperature={temperatures}, Vacuum={vacuums} (expected 1 each).");
    }

    private static CodeInstruction Retarget(CodeInstruction original, MethodInfo replacement) =>
        new(original) { opcode = System.Reflection.Emit.OpCodes.Call, operand = replacement };

    private static void SetTemperature(Room? room, float value)
    {
        if (room != null)
        {
            room.Temperature = value;
            return;
        }
        Skipped++;
    }

    private static void SetVacuum(Room? room, float value)
    {
        if (room != null)
        {
            room.Vacuum = value;
            return;
        }
        Skipped++;
    }
}
