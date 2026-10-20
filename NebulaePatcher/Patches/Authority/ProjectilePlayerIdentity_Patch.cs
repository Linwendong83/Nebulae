using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
internal static class ProjectilePlayerIdentity_Patch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var field = AccessTools.Field(typeof(SkillTargetLocal), nameof(SkillTargetLocal.id));
        var count = 0;
        for (var i = 0; i < codes.Count - 1; i++)
            if (codes[i].LoadsConstant(1) && codes[i + 1].opcode == OpCodes.Stfld && Equals(codes[i + 1].operand, field))
            { codes[i].opcode = OpCodes.Call; codes[i].operand = AccessTools.Method(typeof(CombatTargetContext), nameof(CombatTargetContext.TargetPlayerId)); count++; }
        if (count != 1) throw new InvalidOperationException("Local projectile player collision identity changed: " + count);
        return codes;
    }
}
