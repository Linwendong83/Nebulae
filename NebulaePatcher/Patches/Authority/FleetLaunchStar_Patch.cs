using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.LaunchFleet))]
internal static class FleetLaunchStar_Patch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var getter = AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.localStar));
        var replacement = AccessTools.Method(typeof(CombatAuthorityManager), nameof(CombatAuthorityManager.CasterStar));
        var count = 0;
        foreach (var code in codes)
            if (code.Calls(getter)) { code.opcode = OpCodes.Call; code.operand = replacement; count++; }
        if (count != 2) throw new InvalidOperationException("Fleet launch local-star reads changed: " + count);
        return codes;
    }
}
