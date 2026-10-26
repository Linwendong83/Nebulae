using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.ChangeFleetConfig))]
internal static class FleetConfiguration_Patch
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static bool Prefix(CombatModuleComponent __instance, int fleetIndex, int newConfigId, out bool? __state)
    {
        __state = null;
        if (!Multiplayer.IsActive || Multiplayer.Session?.Authority.IsActive != true || WorldSimulation.IsClient) return true;
        if (FleetConfiguration.IsCurrent(__instance, fleetIndex, newConfigId)) return true;
        if (!FleetConfiguration.TryPrepare(__instance, fleetIndex)) return false;
        __state = __instance.moduleFleets[fleetIndex].fleetEnabled;
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(CombatModuleComponent __instance, int fleetIndex, bool? __state)
    {
        if (__state.HasValue) __instance.moduleFleets[fleetIndex].fleetEnabled = __state.Value;
    }
}
