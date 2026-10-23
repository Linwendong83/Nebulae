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
    [HarmonyPrefix, HarmonyPatch(nameof(MechaLab.ManageTakeback))]
    public static bool ManageTakeback_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer ||
        Multiplayer.Session.Research.BeforeTakeback();

    private static bool ManageAndCanResearch(MechaLab lab)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.Life.IsExiting)
            Multiplayer.Session.Research.TakeBackAfterPending();
        else lab.AutoManage();
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer || Multiplayer.Session.Research.CanTick(lab.gameHistory);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MechaLab.GameTick))]
    public static IEnumerable<CodeInstruction> GameTick_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        // Target: make this.gameHistory.AddTechHash((long)num4) only run in host

        var codeInstructions = instructions.ToList();
        var supply = codeInstructions.FindIndex(c => c.Calls(AccessTools.Method(typeof(MechaLab), nameof(MechaLab.AutoManage))));
        if (supply < 0 || supply + 1 >= codeInstructions.Count)
            throw new InvalidOperationException("MechaLab.GameTick supply boundary changed.");
        var resume = generator.DefineLabel();
        codeInstructions[supply + 1].labels.Add(resume);
        codeInstructions[supply].opcode = OpCodes.Call;
        codeInstructions[supply].operand = AccessTools.Method(typeof(MechaLab_Transpiler), nameof(ManageAndCanResearch));
        codeInstructions.InsertRange(supply + 1, new[] { new CodeInstruction(OpCodes.Brtrue, resume), new CodeInstruction(OpCodes.Ret) });
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
