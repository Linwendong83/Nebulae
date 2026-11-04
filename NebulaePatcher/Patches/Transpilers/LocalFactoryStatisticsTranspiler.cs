using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Statistics;

namespace NebulaePatcher.Patches.Transpilers;

internal static class LocalFactoryStatisticsTranspiler
{
    internal static IEnumerable<CodeInstruction> ReplaceFactoryAccess(
        IEnumerable<CodeInstruction> instructions, FieldInfo gameDataField)
    {
        instructions = new CodeMatcher(instructions)
            .MatchForward(false,
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, gameDataField),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(GameData), nameof(GameData.factoryCount))))
            .Repeat(matcher => matcher
                .SetAndAdvance(OpCodes.Call, AccessTools.Method(typeof(LocalReferenceStatistics), nameof(LocalReferenceStatistics.GetFactoryCount)))
                .RemoveInstructions(2))
            .InstructionEnumeration();

        return new CodeMatcher(instructions)
            .MatchForward(false,
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlanetData), nameof(PlanetData.factoryIndex))))
            .Repeat(matcher => matcher
                .SetAndAdvance(OpCodes.Call, AccessTools.Method(typeof(LocalReferenceStatistics), nameof(LocalReferenceStatistics.GetFactoryIndex))))
            .InstructionEnumeration();
    }
}
