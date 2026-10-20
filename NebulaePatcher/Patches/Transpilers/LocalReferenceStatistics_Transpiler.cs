using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Statistics;

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch]
internal static class LocalReferenceStatistics_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(UIReferenceSpeedTip), nameof(UIReferenceSpeedTip.AddEntryDataWithFactory))]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => Rewrite(instructions);

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(ProductionExtraInfoCalculator), nameof(ProductionExtraInfoCalculator.CalculateFactory))]
    public static IEnumerable<CodeInstruction> ExtraInfo_Transpiler(IEnumerable<CodeInstruction> instructions)
        => Rewrite(instructions);

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions).MatchForward(false,
            new CodeMatch(instruction => instruction.Calls(AccessTools.PropertyGetter(typeof(PlanetFactory), nameof(PlanetFactory.index)))),
            new CodeMatch(OpCodes.Ldelem_Ref));
        if (!matcher.IsValid) throw new InvalidOperationException("Missing native reference-statistics pool lookup");
        return matcher.SetAndAdvance(OpCodes.Call, AccessTools.Method(typeof(LocalReferenceStatistics), nameof(LocalReferenceStatistics.ResolveStat)))
            .RemoveInstruction().InstructionEnumeration();
    }
}
