#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIScreenshotTool))]
internal class UIScreenshotTool_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIScreenshotTool.OnPauseButtonClick))]
    public static bool OnPauseButtonClick_Prefix()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return true;
        SimulatedWorld.RequestWorldPause();
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIScreenshotTool.OnPauseButtonClick))]
    public static void OnPauseButtonClick_Postfix()
    {
        if (Multiplayer.IsActive) SimulatedWorld.SyncWorldPauseAfterToggle();
    }

    [HarmonyPostfix, HarmonyPatch("_OnClose")]
    public static void OnClose_Postfix()
    {
        if (Multiplayer.IsActive) SimulatedWorld.SyncWorldPauseAfterWindowClosed(false);
    }
}
