#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(MechaLab))]
internal class MechaLab_Transpiler
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MechaLab.GameTick))]
    public static bool GameTick_Prefix(MechaLab __instance) => !Multiplayer.IsActive || Multiplayer.Session.IsServer || Multiplayer.Session.Research.CanTick(__instance.gameHistory);

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MechaLab.GameTick))]
    public static IEnumerable<CodeInstruction> GameTick_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // Target: make this.gameHistory.AddTechHash((long)num4) only run in host

        var codeInstructions = instructions as CodeInstruction[] ?? instructions.ToArray();
        var matcher = new CodeMatcher(codeInstructions)
            .MatchForward(true,
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld),
                new CodeMatch(OpCodes.Ldloc_S),
                new CodeMatch(OpCodes.Conv_I8),
                new CodeMatch(i => i.opcode == OpCodes.Callvirt && ((MethodInfo)i.operand).Name == "AddTechHash")
            );

        if (!matcher.IsInvalid)
        {
            return matcher
                .SetInstruction(
                    HarmonyLib.Transpilers.EmitDelegate<Action<GameHistoryData, long>>((history, addcnt) =>
                    {
                        //Host in multiplayer can do normal research in the mecha
                        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
                        {
                            history.AddTechHash(addcnt);
                            return;
                        }

                        Multiplayer.Session.Research.Record(history, addcnt);
                    })
                )
                .InstructionEnumeration();
        }
        Log.Error("MechaLab.GameTick_Transpiler failed. Mod version not compatible with game version.");
        return codeInstructions;

    }
}
