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
    public static bool OnGameStatusButtonClick_Prefix(bool ___event_lock)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return true;
        if (!___event_lock) SimulatedWorld.RequestWorldPause();
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIDeepProfilerSettings.OnGameStatusButtonClick))]
    public static void OnGameStatusButtonClick_Postfix()
    {
        if (Multiplayer.IsActive) SimulatedWorld.SyncWorldPauseAfterToggle();
    }
}

[HarmonyPatch(typeof(UIDeepProfiler), "_OnClose")]
internal static class UIDeepProfilerClose_Patch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (Multiplayer.IsActive) SimulatedWorld.SyncWorldPauseAfterWindowClosed(false);
    }
}
