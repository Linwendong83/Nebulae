using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
internal static class MissileTargetLifetime_Patch
{
    [HarmonyPrefix]
    public static void Prefix(SkillSystem skillSystem, ref GeneralMissile __instance)
    {
        if (Multiplayer.IsActive) EnemyTargetIdentity.ValidateMissile(skillSystem, ref __instance);
    }
}

[HarmonyPatch(typeof(DataPool<GeneralMissile>), nameof(DataPool<GeneralMissile>.Remove))]
internal static class MissileTargetRetirement_Patch
{
    [HarmonyPrefix]
    public static void Prefix(DataPool<GeneralMissile> __instance, int id) => EnemyTargetIdentity.ForgetMissile(__instance, id);
}
