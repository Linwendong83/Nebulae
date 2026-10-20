#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UITutorialTip))]
internal class UITutorialTip_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UITutorialTip.PopupTutorialTip))]
    public static bool PopupTutorialTip_Prefix()
    {
        // Personal tutorial history already survives reconnects. Let the game display
        // and unlock tips normally, including contextual reminders for unlocked tutorials.
        return !Multiplayer.IsActive || !Multiplayer.IsDedicated;
    }
}
