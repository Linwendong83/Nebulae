using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic.Combat;

// Almost all relays target a different planet than the host's loaded factory.
// Test the three plain fields before computing the per-relay modulo. Neither
// branch changes simulation frequency, realization timing or native calls.
[HarmonyPatch(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.GameTickLogic))]
internal static class HiveRelayGuard_Patch
{
    internal static bool Enabled = true;
    [HarmonyPrepare]
    internal static bool Prepare() => Multiplayer.IsDedicated;

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var codes = instructions.ToList();
        var target = AccessTools.Field(typeof(DFRelayComponent), nameof(DFRelayComponent.targetAstroId));
        var hiveId = AccessTools.Field(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.hiveAstroId));
        var start = -1;
        for (var i = 8; i + 13 < codes.Count; i++)
            if (codes[i + 1].LoadsField(target) && codes[i - 6].LoadsField(hiveId) && codes[i - 3].opcode == OpCodes.Rem &&
                codes[i - 1].opcode.FlowControl == FlowControl.Cond_Branch &&
                codes[i + 5].LoadsField(AccessTools.Field(typeof(DFRelayComponent), nameof(DFRelayComponent.baseState))) &&
                codes[i + 9].LoadsField(AccessTools.Field(typeof(DFRelayComponent), nameof(DFRelayComponent.stage))))
            {
                if (start >= 0) throw new InvalidOperationException("Ambiguous relay realization guard.");
                start = i - 8;
            }
        if (start < 0) throw new InvalidOperationException("Relay realization guard changed.");
        var original = generator.DefineLabel(); var success = generator.DefineLabel();
        var replacement = new List<CodeInstruction>
        {
            new(OpCodes.Ldsfld, AccessTools.Field(typeof(HiveRelayGuard_Patch), nameof(Enabled))),
            new(OpCodes.Brfalse, original)
        };
        // Clones must not duplicate labels or exception boundaries.
        foreach (var offset in new[] { 8, 12, 16, 0 })
            for (var n = 0; n < (offset == 0 ? 8 : 4); n++)
            {
                var code = codes[start + offset + n];
                replacement.Add(new CodeInstruction(code.opcode, code.operand));
            }
        replacement.Add(new CodeInstruction(OpCodes.Br, success));
        replacement[0].labels.AddRange(codes[start].labels); codes[start].labels.Clear();
        codes[start].labels.Add(original); codes[start + 20].labels.Add(success);
        codes.InsertRange(start, replacement);
        return codes;
    }
}
