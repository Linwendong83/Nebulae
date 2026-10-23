#region

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using NebulaeWorld;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIUPSTool))]
internal class UIUPSTool_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIUPSTool.ApplySimSpeedIncrease))]
    public static void ApplySimSpeedIncrease_Postfix(UIUPSTool __instance)
    {
        RequestSimSpeed(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIUPSTool.ApplySimSpeedDecrease))]
    public static void ApplySimSpeedDecrease_Postfix(UIUPSTool __instance)
    {
        RequestSimSpeed(__instance);
    }

    private static void RequestSimSpeed(UIUPSTool upsTool)
    {
        if (Multiplayer.IsActive) GameStatesManager.ChangeWorldSimSpeed((float)upsTool.targetSimSpeed);
    }

    // These native setters also run when the tool opens, closes or enters a star system.
    // Multiplayer applies a single shared value instead of each UI easing its own clock.
    [HarmonyPrefix, HarmonyPatch("SetSimSpeed")]
    [HarmonyPatch("RevertSimSpeed")]
    public static bool NativeSpeedWrite_Prefix() => !Multiplayer.IsActive;

    [HarmonyPostfix, HarmonyPatch("_OnOpen")]
    [HarmonyPatch("_OnClose")]
    [HarmonyPatch("_OnUpdate")]
    public static void Lifecycle_Postfix(UIUPSTool __instance)
    {
        if (!Multiplayer.IsActive) return;
        __instance.targetSimSpeed = Multiplayer.Session.WorldSimSpeed;
    }

    [HarmonyTranspiler, HarmonyPatch("_OnOpen")]
    [HarmonyPatch("_OnClose")]
    [HarmonyPatch("_OnUpdate")]
    public static IEnumerable<CodeInstruction> PreserveSharedTarget(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var target = AccessTools.Field(typeof(UIUPSTool), nameof(UIUPSTool.targetSimSpeed));
        var changed = 0;
        for (var i = 0; i + 1 < code.Count; i++)
        {
            if (!code[i].LoadsConstant(1d) || !code[i + 1].StoresField(target)) continue;
            code[i].opcode = OpCodes.Call;
            code[i].operand = AccessTools.Method(typeof(UIUPSTool_Patch), nameof(DefaultSpeed));
            changed++;
        }
        if (changed != 1) throw new InvalidOperationException("Native UPS tool speed reset changed.");
        return code;
    }
    private static double DefaultSpeed() => Multiplayer.IsActive ? Multiplayer.Session.WorldSimSpeed : 1d;
}
