using System;
using System.Collections.Generic;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Transpilers;

/// <summary>Lets the runtime schedule collection instead of blocking every multiplayer build.</summary>
[HarmonyPatch]
internal static class BuildToolGc_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_Addon), nameof(BuildTool_Addon.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_Inserter), nameof(BuildTool_Inserter.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CreatePrebuilds))]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var collect = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
        var replacement = AccessTools.Method(typeof(BuildToolGc_Transpiler), nameof(CollectOnlyInSingleplayer));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            // Reuse the instruction to preserve branch labels and exception blocks.
            if (instruction.Calls(collect))
            {
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced == 0)
            Log.Error("BuildToolGc_Transpiler found no GC.Collect call in CreatePrebuilds.");
    }

    private static void CollectOnlyInSingleplayer()
    {
        if (!Multiplayer.IsActive) GC.Collect();
    }
}
