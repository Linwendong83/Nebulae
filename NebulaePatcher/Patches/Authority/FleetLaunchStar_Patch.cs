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

[HarmonyPatch]
internal static class FleetDiscoveryScope_Patch
{
    [HarmonyTranspiler, HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.GameTick))]
    private static IEnumerable<CodeInstruction> Tick(IEnumerable<CodeInstruction> instructions) =>
        Replace(instructions, nameof(GameMain.localPlanet), nameof(CombatAuthorityManager.CasterPlanet), 1);

    [HarmonyTranspiler, HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.DiscoverSpaceEnemy))]
    private static IEnumerable<CodeInstruction> Discover(IEnumerable<CodeInstruction> instructions) =>
        Replace(instructions, nameof(GameMain.localStar), nameof(CombatAuthorityManager.CasterStar), 2);

    private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string property, string method, int expected)
    {
        var count = 0;
        var getter = AccessTools.PropertyGetter(typeof(GameMain), property);
        foreach (var code in instructions)
        {
            if (code.Calls(getter))
            { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(CombatAuthorityManager), method); count++; }
            yield return code;
        }
        if (count != expected) throw new InvalidOperationException("Fleet discovery scope reads changed: " + property + " " + count);
    }
}
