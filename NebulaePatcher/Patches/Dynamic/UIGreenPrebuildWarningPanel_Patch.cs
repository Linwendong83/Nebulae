using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIGreenPrebuildWarningPanel))]
internal class UIGreenPrebuildWarningPanel_Patch
{
    [HarmonyPrefix, HarmonyPatch("OnCurrentPlanetWarningToggle")]
    public static bool OnCurrentPlanetWarningToggle_Prefix(UIGreenPrebuildWarningPanel __instance, bool enable)
    {
        if (!Multiplayer.IsActive) return true;
        if (__instance.planet?.factory != null)
            Multiplayer.Session.Warning.ChangeGreenPrebuildWarning(__instance.planet.id, enable);
        __instance.RefreshSwitches();
        return false;
    }

    [HarmonyPrefix, HarmonyPatch("OnAllPlanetWarningToggle")]
    public static bool OnAllPlanetWarningToggle_Prefix(UIGreenPrebuildWarningPanel __instance, bool enable)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.Warning.ChangeGreenPrebuildWarning(0, enable);
        __instance.RefreshSwitches();
        return false;
    }
}
