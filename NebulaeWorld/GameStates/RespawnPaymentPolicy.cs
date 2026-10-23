using System;
using HarmonyLib;

namespace NebulaeWorld.GameStates;

internal static class RespawnPaymentPolicy
{
    private static readonly int[][][] Costs = (int[][][])AccessTools.Field(typeof(PlayerAction_Death), "respawnCosts").GetValue(null);

    public static int[] Calculate(int deathCount, int mode, int option, bool sandbox)
    {
        if (mode is not (2 or 3)) throw new InvalidOperationException("Player is not awaiting respawn");
        var options = Costs[Math.Max(0, Math.Min(Costs.Length - 1, deathCount - 1))];
        if (option < 0 || option >= options.Length) throw new InvalidOperationException("Invalid respawn option");
        var result = new int[6];
        if (!sandbox)
            for (var i = 0; i < result.Length; i++) result[i] = options[option][i] * (mode == 2 ? 2 : 1);
        return result;
    }
}
