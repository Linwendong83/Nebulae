#region

using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld.Statistics;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(UIProductEntry))]
internal static class UIProductEntry_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(UIProductEntry.UpdateExtraProductTexts))]
    private static IEnumerable<CodeInstruction> UpdateExtraProductTexts_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        //Change: this.gameData.factoryCount
        //To:     GetFactoryCount()
        //Change: planetData.factoryIndex
        //To:     GetFactoryIndex(planetData)
        //Change: planetData.factory != null
        //To:     HasFactory(planetData)
        var codeInstructions = instructions as CodeInstruction[] ?? instructions.ToArray();
        try
        {
            instructions = LocalFactoryStatisticsTranspiler.ReplaceFactoryAccess(
                codeInstructions, AccessTools.Field(typeof(UIProductEntry), nameof(UIProductEntry.gameData)));

            instructions = new CodeMatcher(instructions)
                .MatchForward(false,
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(PlanetData), nameof(PlanetData.factory))),
                    new CodeMatch(OpCodes.Brfalse)
                )
                .Repeat(matcher => matcher
                    .SetAndAdvance(OpCodes.Call, AccessTools.Method(typeof(LocalReferenceStatistics), nameof(LocalReferenceStatistics.HasFactory)))
                )
                .InstructionEnumeration();

            return instructions;
        }
        catch (System.Exception error)
        {
            Log.Error("Transpiler UIProductEntry.UpdateExtraProductTexts failed. Mod version not compatible with game version.", error);
            return codeInstructions;
        }
    }
}
