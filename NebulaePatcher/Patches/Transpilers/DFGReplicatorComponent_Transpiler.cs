#region

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(DFGReplicatorComponent))]
internal class DFGReplicatorComponent_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(DFGReplicatorComponent.LogicTick))]
    public static IEnumerable<CodeInstruction> LogicTick_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            /* Let enemyFormation.AddUnit() authorize by server
            from:
                int num5 = enemyFormation.AddUnit();
            to:
                int num5 = AddUnit(enemyFormation);
            */

            var codeMatcher = new CodeMatcher(instructions)
                .MatchForward(false,
                    new CodeMatch(i => i.opcode == OpCodes.Callvirt && ((MethodInfo)i.operand).Name == "AddUnit"))
                .Repeat(
                    matcher => matcher.SetAndAdvance(OpCodes.Call,
                        AccessTools.Method(typeof(DFGReplicatorComponent_Transpiler), nameof(AddUnit)))
                    );

            return codeMatcher.InstructionEnumeration();
        }
        catch (System.Exception e)
        {
            Log.Error("Transpiler DFGReplicatorComponent.LogicTick failed.");
            Log.Error(e);
            return instructions;
        }
    }

    static int AddUnit(EnemyFormation enemyFormation)
    {
        if (!Multiplayer.IsActive)
        {
            return enemyFormation.AddUnit();
        }
        if (Multiplayer.Session.IsClient)
        {
            return 0;
        }

        enemyFormation.AddUnit();
        return 0; // Skip the following call to InitiateUnitDeferred
    }
}
