using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(UIPowerNodeWindow))]
internal class UIPowerNodeWindow_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(UIPowerNodeWindow._OnUpdate))]
    public static IEnumerable<CodeInstruction> OnUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = instructions.ToArray();
        try
        {
            var code = original.Select(i => new CodeInstruction(i)).ToArray();
            var matcher = new CodeMatcher(code)
                .MatchForward(true,
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(Mecha), nameof(Mecha.energyChanges))),
                    new CodeMatch(OpCodes.Ldc_I4_2),
                    new CodeMatch(OpCodes.Ldelem_R8));
            if (matcher.IsInvalid) throw new InvalidOperationException("Charger UI energy check was not found.");
            matcher.Advance(1).InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UIPowerNodeWindow_Transpiler), nameof(ChargingEnergy))));

            matcher.MatchForward(true,
                new CodeMatch(OpCodes.Ldstr, "正在充电"),
                new CodeMatch(i => i.opcode == OpCodes.Call));
            if (matcher.IsInvalid) throw new InvalidOperationException("Charger UI state text was not found.");
            matcher.Advance(1).Insert(
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UIPowerNodeWindow_Transpiler), nameof(ChargeStateText))));
            return matcher.InstructionEnumeration();
        }
        catch (Exception e)
        {
            Log.Warn("UIPowerNodeWindow._OnUpdate transpiler failed. Charger UI will be unchanged.");
            Log.Warn(e);
            return original;
        }
    }

    private static double ChargingEnergy(double localEnergy, UIPowerNodeWindow window) =>
        Multiplayer.IsActive
            ? Multiplayer.Session.PowerTowers.GetChargerCount(window.factory.planetId, window.nodeId)
            : localEnergy;

    private static string lastChargeStateText;
    private static string lastChargeStateOriginal;
    private static int lastChargePlanetId, lastChargeNodeId, lastChargeCount;

    private static string ChargeStateText(string originalValue, UIPowerNodeWindow window)
    {
        if (!Multiplayer.IsActive) return originalValue;
        var planetId = window.factory.planetId;
        var nodeId = window.nodeId;
        var count = Multiplayer.Session.PowerTowers.GetChargerCount(planetId, nodeId);
        // The window re-runs this every frame while open. Returning the same string
        // instance keeps Unity's Text setter from dirtying the canvas on every frame.
        if (count == lastChargeCount && nodeId == lastChargeNodeId && planetId == lastChargePlanetId &&
            originalValue == lastChargeStateOriginal)
        {
            return lastChargeStateText;
        }
        lastChargeCount = count;
        lastChargeNodeId = nodeId;
        lastChargePlanetId = planetId;
        lastChargeStateOriginal = originalValue;
        return lastChargeStateText = originalValue + '[' + count + ']';
    }
}
