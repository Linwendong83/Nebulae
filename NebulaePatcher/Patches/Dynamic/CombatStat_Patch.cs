#region

using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(CombatStat))]
internal class CombatStat_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.HandleZeroHp))]
    public static bool HandleZeroHp_Prefix(ref CombatStat __instance)
    {
        if (!AuthorityRuleGuard.AllowHostRule())
        {
            return false;
        }

        if (Multiplayer.IsActive &&
            Multiplayer.Session?.AuthorityRuntime?.IsHostAuthority == true)
        {
            Multiplayer.Session.AuthorityRuntime.HostDeathCapture?.Capture(
                __instance.originAstroId, __instance.objectType, __instance.objectId);
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.TickSkillLogic))]
    public static bool TickSkillLogic_Prefix()
    {
        if (WorldSimulation.IsClient) return false;
        if (!AuthorityRuleGuard.AllowHostRule())
        {
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.HandleFullHp))]
    public static bool HandleFullHp_Prefix()
    {
        if (!AuthorityRuleGuard.AllowHostRule())
        {
            return false;
        }

        return true;
    }
}
