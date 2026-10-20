#region

using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(EnemyDFGroundSystem))]
internal class EnemyDFGroundSystem_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.GameTickLogic_Prepare))]
    public static IEnumerable<CodeInstruction> GameTickLogic_Prepare_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            /*  Overwrite the condition of local player and position in MP
                First make it skips the original logic that only work for single player, by setting local_player_exist = false
            to:
                if (isLocalLoaded && !Multiplayer.IsActive) {
                    ...
                }
                else {
                    ...
                }
            */

            var codeMatcher = new CodeMatcher(instructions)
                .MatchForward(true,
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.isLocalLoaded))),
                    new CodeMatch(OpCodes.Brfalse));

            var jumpOperand = codeMatcher.Instruction.operand;
            codeMatcher.Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(Multiplayer), nameof(Multiplayer.IsActive))),
                    new CodeInstruction(OpCodes.Brtrue_S, jumpOperand)
                );

            return codeMatcher.InstructionEnumeration();
        }
        catch (System.Exception e)
        {
            Log.Error("Transpiler GameTickLogic_Prepare failed. Ground Dark Fog unit target will not in sync.");
            Log.Error(e);
            return instructions;
        }
    }
}
