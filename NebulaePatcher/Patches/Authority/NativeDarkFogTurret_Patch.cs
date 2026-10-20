using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

// Vanilla's last space-turret firing check reads the camera player's sailing flag.
// The collision/aim target can be another pilot, including when the host is on a planet.
[HarmonyPatch(typeof(DFSTurretComponent), nameof(DFSTurretComponent.Aim))]
internal static class NativeDarkFogTurret_Patch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var sailing = AccessTools.PropertyGetter(typeof(Player), nameof(Player.sailing));
        foreach (var code in instructions)
        {
            if (code.Calls(sailing))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(NativeDarkFogTurret_Patch), nameof(TargetSailing));
            }
            yield return code;
        }
    }
    private static bool TargetSailing(Player player, ref DFSTurretComponent turret)
    {
        if (!Multiplayer.IsActive || turret.target.type != ETargetType.Player) return player.sailing;
        var combat = Multiplayer.Session.Combat;
        return combat.IndexByPlayerId.TryGetValue(turret.target.id, out var index) && combat.Players[index].isAlive &&
            combat.Players[index].mecha?.player?.sailing == true;
    }
}
