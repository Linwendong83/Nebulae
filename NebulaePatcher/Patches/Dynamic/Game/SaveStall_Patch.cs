using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.GameStates;

namespace NebulaePatcher.Patches.Dynamic.Game;

[HarmonyPatch]
internal static class SaveBoundary_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GameSave), nameof(GameSave.AutoSave));
        yield return AccessTools.Method(typeof(GameSave), nameof(GameSave.SaveCurrentGame));
    }
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Before(bool __runOriginal, out HostSaveScope __state) => __state = __runOriginal ? HostSaveScope.Begin() : null;
    [HarmonyFinalizer]
    internal static void After(HostSaveScope __state) => __state?.Dispose();
}

[HarmonyPatch(typeof(GameSave), nameof(GameSave.SaveCurrentGame))]
internal static class SaveStream_Patch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var file = AccessTools.Constructor(typeof(FileStream), new[] { typeof(string), typeof(FileMode), typeof(FileAccess), typeof(FileShare) });
        var writer = AccessTools.Constructor(typeof(BinaryWriter), new[] { typeof(Stream) });
        var export = AccessTools.Method(typeof(GameData), nameof(GameData.Export));
        int files = 0, writers = 0, exports = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, file))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SavePerformance), nameof(SavePerformance.Open)); files++; }
            else if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, writer))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SavePerformance), nameof(SavePerformance.Writer)); writers++; }
            else if (instruction.Calls(export))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SavePerformance), nameof(SavePerformance.Export)); exports++; }
            yield return instruction;
        }
        if (files != 1 || writers != 1 || exports != 1) throw new InvalidOperationException("Native save writer layout changed.");
    }
}
