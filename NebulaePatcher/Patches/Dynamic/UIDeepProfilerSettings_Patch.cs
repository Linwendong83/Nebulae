#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDeepProfilerSettings))]
internal class UIDeepProfilerSettings_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDeepProfilerSettings.OnGameStatusButtonClick))]
    public static bool OnGameStatusButtonClick_Prefix()
    {
        // The profiler's game-status toggle is a diagnostic freeze. In multiplayer the pause is a
        // host-authoritative world state, so a local toggle must not write the flag.
        return !Multiplayer.IsActive;
    }
}
