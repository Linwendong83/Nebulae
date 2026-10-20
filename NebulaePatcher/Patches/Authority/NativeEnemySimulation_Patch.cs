using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class NativeEnemySimulation_Patch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.GameTickLogic_Unit));
        yield return AccessTools.Method(typeof(GameLogic), "_enemy_ground_unit_parallel");
        yield return AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.GameTickLogic));
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = new List<CodeInstruction>(instructions);
        var id = AccessTools.Field(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.id));
        var simulationId = AccessTools.Method(typeof(NativeEnemySimulation), nameof(NativeEnemySimulation.SimulationId));
        var ids = 0;
        foreach (var instruction in code)
        {
            if (!instruction.LoadsField(id)) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = simulationId;
            ids++;
        }
        if (ids == 0) throw new InvalidOperationException("Native enemy loop has no unit membership check: " + __originalMethod.Name);
        if (__originalMethod.DeclaringType == typeof(EnemyDFHiveSystem))
        {
            var hostCount = AccessTools.Method(typeof(NativeEnemySimulation_Patch), nameof(HostCount));
            var counts = 0;
            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].operand is not MethodInfo method || method.ReturnType != typeof(int) ||
                    method.Name is not ("get_count" or "get_relayCount" or "get_tinderCount") ||
                    method.DeclaringType == typeof(DataPool<EnemyUnitComponent>)) continue;
                code.Insert(++i, new CodeInstruction(OpCodes.Call, hostCount));
                counts++;
            }
            if (counts < 3) throw new InvalidOperationException("Native hive non-unit loops changed.");
        }
        return code;
    }

    private static int HostCount(int count) => WorldSimulation.IsClient ? 0 : count;
}
