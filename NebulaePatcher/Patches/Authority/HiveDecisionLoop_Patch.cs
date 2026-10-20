using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.GameTick))]
internal static class HiveDecisionLoop_Patch
{
    private static bool Client() => Multiplayer.IsActive && Multiplayer.Session.IsClient;
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var field = AccessTools.Field(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.isEmpty));
        var count = 0;
        for (var i = codes.Count - 2; i >= 0; i--)
            if (codes[i].LoadsField(field) && (codes[i + 1].opcode == OpCodes.Brtrue || codes[i + 1].opcode == OpCodes.Brtrue_S))
            {
                codes.InsertRange(i + 2, new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HiveDecisionLoop_Patch), nameof(Client))),
                    new CodeInstruction(OpCodes.Brtrue, codes[i + 1].operand) });
                count++;
            }
        // Native also distributes threat/experience to sibling hives in a second block.
        if (count != 2) throw new InvalidOperationException("Sector hive decision loops changed: " + count);
        return codes;
    }
}
