using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(AchievementSystem))]
internal static class AchievementSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(AchievementSystem.UnlockAchievement))]
    [HarmonyPatch(nameof(AchievementSystem.SetAchievementProgress))]
    [HarmonyPatch(nameof(AchievementSystem.SyncAchievementsToPlatform))]
    public static bool Award_Prefix()
    {
        // Guard the award entry points as well as normal detection: progress can
        // unlock an achievement, and periodic platform sync can upload past awards.
        return !Multiplayer.IsActive;
    }
}
