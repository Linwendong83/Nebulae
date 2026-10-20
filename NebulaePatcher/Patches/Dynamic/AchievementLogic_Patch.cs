#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(AchievementLogic))]
internal class AchievementLogic_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(AchievementLogic.isSelfFormalGame), MethodType.Getter)]
    public static bool IsSelfFormalGame_Prefix(ref bool __result)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        // The metadata window also uses this account check. Keep multiplayer account
        // compatibility separate from achievement eligibility, which is always off.
        __result = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(AchievementLogic.active), MethodType.Getter)]
    public static bool Active_Prefix(ref bool __result)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        __result = false;
        return false;
    }
}
