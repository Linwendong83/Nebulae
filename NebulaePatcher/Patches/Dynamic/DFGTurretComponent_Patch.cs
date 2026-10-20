using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(DFGTurretComponent))]
internal class DFGTurretComponent_Patch
{
    [HarmonyPrefix, HarmonyPatch(nameof(DFGTurretComponent.TargetIsInRange))]
    public static bool TargetIsInRange_Prefix(ref DFGTurretComponent __instance, PlanetFactory factory,
        SkillTargetLocal target, float sensorRange, float attackRange, DFGBaseComponent @base, out float dist2, ref bool __result)
    {
        dist2 = 0;
        if (!Multiplayer.IsActive) return true;
        __result = GroundTurretCombat.InRange(__instance, factory, target, sensorRange, attackRange, @base, out dist2);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(DFGTurretComponent.SearchTarget))]
    public static bool SearchTarget(ref DFGTurretComponent __instance, PlanetFactory factory, DFGBaseComponent @base,
        EAggressiveLevel aggressiveLevel, int threadIdx, ref bool __result)
    {
        if (!Multiplayer.IsActive) return true;
        __result = GroundTurretCombat.Search(ref __instance, factory, @base, aggressiveLevel, threadIdx);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(DFGTurretComponent.Aim))]
    public static bool Aim(ref DFGTurretComponent __instance, PlanetFactory factory, DFGBaseComponent @base,
        EnemyBuilderComponent[] builderPool, EAggressiveLevel aggressiveLevel, int threadIdx)
    {
        if (!Multiplayer.IsActive) return true;
        GroundTurretCombat.Aim(ref __instance, factory, @base, builderPool, aggressiveLevel, threadIdx);
        return false;
    }

    [HarmonyTranspiler, HarmonyPatch(nameof(DFGTurretComponent.Shoot_Plasma))]
    internal static IEnumerable<CodeInstruction> PlasmaVelocity(IEnumerable<CodeInstruction> instructions)
    {
        var count = 0;
        var field = AccessTools.Field(typeof(SkillSystem), nameof(SkillSystem.playerVelocityL));
        foreach (var code in instructions)
        {
            if (code.LoadsField(field))
            {
                count++;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GroundTurretCombat), nameof(GroundTurretCombat.TargetVelocity)));
            }
            else yield return code;
        }
        if (count != 1) throw new InvalidOperationException("Ground turret plasma velocity input changed: " + count);
    }
}

// Vanilla erases every Player hatred when the camera player dies. A dedicated host is
// permanently dead; each named player must be validated independently before turret post-ticks.
[HarmonyPatch(typeof(DFGBaseComponent), nameof(DFGBaseComponent.PostGameTick))]
internal static class GroundTurretBaseTarget_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(DFGBaseComponent __instance, PlanetFactory factory)
    {
        if (!Multiplayer.IsActive) return true;
        GroundTurretCombat.PruneHatred(__instance, factory);
        return false;
    }
}
