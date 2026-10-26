using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class FleetPose_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(CombatModuleComponent), nameof(CombatModuleComponent.LaunchFleet)),
        AccessTools.Method(typeof(FleetComponent), nameof(FleetComponent.InternalUpdate_Space))
    };

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var count = 0;
        foreach (var code in instructions)
        {
            if (code.Calls(AccessTools.Method(typeof(FleetComponent), nameof(FleetComponent.CalculateFleetPoseScale))))
            {
                var launching = __originalMethod.DeclaringType == typeof(CombatModuleComponent);
                yield return new CodeInstruction(OpCodes.Ldarg, launching ? 3 : 5).MoveLabelsFrom(code).MoveBlocksFrom(code);
                if (!launching) yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Mecha), nameof(Mecha.player)));
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(FleetPose), nameof(FleetPose.Scale));
                count++;
            }
            yield return code;
        }
        if (count != 1) throw new InvalidOperationException("Fleet pose scale call changed: " + __originalMethod.Name);
    }
}
