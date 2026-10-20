#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(PlanetTransport))]
internal class PlanetTransport_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(PlanetTransport.RefreshDispenserOnStoragePrebuildBuild))]
    public static IEnumerable<CodeInstruction> RefreshDispenserOnStoragePrebuildBuild_Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var codeInstructions = instructions as CodeInstruction[] ?? instructions.ToArray();
        try
        {
            // factoryModel.gpuiManager is null for remote planets, so we need to use GameMain.gpuiManager which is initialized by nebulae
            // replace : this.factory.planet.factoryModel.gpuiManager
            // with    : GameMain.gpuiManager
            var codeMatcher = new CodeMatcher(codeInstructions)
                .MatchForward(false,
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldfld),
                    new CodeMatch(OpCodes.Callvirt),
                    new CodeMatch(OpCodes.Ldfld),
                    new CodeMatch(i => i.opcode == OpCodes.Ldfld && ((FieldInfo)i.operand).Name == "gpuiManager")
                )
                .Repeat(matcher => matcher
                    .RemoveInstructions(4)
                    .SetAndAdvance(OpCodes.Call, typeof(GameMain).GetProperty("gpuiManager").GetGetMethod()
                    ));

            return codeMatcher.InstructionEnumeration();
        }
        catch (Exception e)
        {
            Log.Error("RefreshDispenserOnStoragePrebuildBuild_Transpiler fail!");
            Log.Error(e);
            return codeInstructions;
        }
    }
}
