using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDarkFogMonitorEntry))]
internal static class UIDarkFogMonitorEntry_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDarkFogMonitorEntry.RefreshPlanetNameText))]
    public static bool RefreshPlanetNameText_Prefix(UIDarkFogMonitorEntry __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient)
        {
            return true;
        }

        // Factory unload resets bases before the monitor unregisters its astro-change events.
        var targetBase = __instance.targetBase;
        return targetBase != null && targetBase.id > 0 && targetBase.groundSystem?.planet != null;
    }
}
