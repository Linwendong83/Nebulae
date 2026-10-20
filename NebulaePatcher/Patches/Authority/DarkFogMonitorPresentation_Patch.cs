using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class DarkFogMonitorPresentation_Patch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "_OnLateUpdate", "RefreshValuesComplete", "OnAlertToggleClick" })
            yield return AccessTools.Method(typeof(UIDarkFogMonitorEntry), name);
    }

    public static int CoreProgress(DFGBaseComponent item)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient)
        {
            var planet = item.groundSystem.factory.planetId;
            if (Multiplayer.Session.AuthorityRuntime.DarkFogReplica.BaseAlive.TryGetValue((planet, item.id), out var alive)) return alive ? 1 : 0;
            return item.ruinId == 0 ? 1 : 0;
        }
        return item.groundSystem.builders.buffer[item.builderId].sp;
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = new List<CodeInstruction>(instructions);
        var target = AccessTools.Field(typeof(UIDarkFogMonitorEntry), "targetBase");
        var pattern = new[]
        {
            new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Ldfld, target),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(DFGBaseComponent), nameof(DFGBaseComponent.groundSystem))),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.builders))),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(DataPool<EnemyBuilderComponent>), "buffer")),
            new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Ldfld, target),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(DFGBaseComponent), nameof(DFGBaseComponent.builderId))),
            new CodeMatch(OpCodes.Ldelema, typeof(EnemyBuilderComponent)),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(EnemyBuilderComponent), nameof(EnemyBuilderComponent.sp)))
        };
        var label = "UIDarkFogMonitorEntry." + __originalMethod.Name + ".coreProgress";
        var expected = __originalMethod.Name == "OnAlertToggleClick" ? 2 : 1;
        if (!AuthorityTranspilerGuard.VerifyCount(label, codes, expected, true, pattern))
            throw new InvalidOperationException("Unsupported dark fog core monitor layout: " + label);
        var matcher = new CodeMatcher(codes);
        for (var match = 0; match < expected; match++)
        {
            matcher.MatchForward(false, pattern);
            var load = new CodeInstruction(matcher.Instruction);
            var field = new CodeInstruction(matcher.InstructionAt(1));
            var call = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DarkFogMonitorPresentation_Patch), nameof(CoreProgress)));
            for (var part = 2; part < pattern.Length; part++)
            { call.labels.AddRange(matcher.InstructionAt(part).labels); call.blocks.AddRange(matcher.InstructionAt(part).blocks); }
            matcher.RemoveInstructions(pattern.Length).Insert(load, field, call).Advance(3);
        }
        return matcher.InstructionEnumeration();
    }
}
