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
        // The screenshot tool freezes the world for a clean shot. In multiplayer the pause is a
        // host-authoritative world state, so a local toggle must not write the flag: the world
        // pause (tech tree, Dyson editor) is the only way to stop the shared clock.
        return !Multiplayer.IsActive;
    }
}
