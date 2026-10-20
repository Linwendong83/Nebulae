using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

// The native controller treats fixUPS <= 1 as "use default 60". Multiplayer's clock
// deliberately requests 1 UPS while ahead, so that boundary must remain an explicit rate.
[HarmonyPatch(typeof(FPSController), "Update")]
internal static class FPSControllerClock_Patch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList(); var changed = 0;
        var field = AccessTools.Field(typeof(FPSController), nameof(FPSController.fixUPS));
        for (var i = 0; i + 1 < code.Count; i++)
        {
            if (!code[i].LoadsField(field) || !code[i + 1].LoadsConstant(1.0)) continue;
            code[i + 1].opcode = OpCodes.Call;
            code[i + 1].operand = AccessTools.Method(typeof(FPSControllerClock_Patch), nameof(ExplicitRateThreshold));
            changed++;
        }
        if (changed != 1) throw new InvalidOperationException("Native explicit UPS threshold changed.");
        return code;
    }

    internal static double ExplicitRateThreshold() => Multiplayer.IsActive && Multiplayer.Session.IsClient ? 0d : 1d;
}
