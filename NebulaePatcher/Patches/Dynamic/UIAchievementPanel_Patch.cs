using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIAchievementPanel))]
internal static class UIAchievementPanel_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIAchievementPanel._OnOpen))]
    public static void OnOpen_Postfix(UIAchievementPanel __instance)
    {
        // Leave the original list and progress display intact. Restore the button
        // when the panel is reused in single player after leaving a session.
        __instance.enableAchBtn.gameObject.SetActive(!Multiplayer.IsActive);
        if (Multiplayer.IsActive)
        {
            __instance.enableDetectionTip.SetActive(false);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIAchievementPanel.EnableAchievementForOldSave))]
    public static bool EnableAchievementForOldSave_Prefix()
    {
        return !Multiplayer.IsActive;
    }
}
