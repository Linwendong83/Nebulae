using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

// These buttons write fields directly. Publish before a combat checkpoint or another
// native sensor tick can observe the old settings on the host.
[HarmonyPatch]
internal static class FleetSettings_Patch
{
    [HarmonyPostfix, HarmonyPatch(typeof(UIZS_FleetPanel), "OnAttackBuildingButtonClick")]
    [HarmonyPatch(typeof(UIZS_FleetPanel), "OnAttackRelayButtonClick")]
    [HarmonyPatch(typeof(UIZS_FleetPanel), "OnEnableModuleButtonClick")]
    [HarmonyPatch(typeof(UIZS_FleetEntry), "OnFleetEnableButtonClick")]
    [HarmonyPatch(typeof(UIMechaWindow), nameof(UIMechaWindow.OnAutoReplenishButtonClick))]
    internal static void Changed()
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.Authority.IsActive)
            Multiplayer.Session.CombatAuthority.SendSettings(GameMain.gameTick, immediate: true);
    }
}

[HarmonyPatch(typeof(UIZS_FleetEntry), "_OnUpdate")]
internal static class FleetEntryReadiness_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(GameData ___gameData, Mecha ___mecha, bool ___isSpace, int ___fleetIndex)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return true;
        var module = ___isSpace ? ___mecha?.spaceCombatModule : ___mecha?.groundCombatModule;
        if (module?.moduleFleets == null || ___fleetIndex < 0 || ___fleetIndex >= module.fleetCount ||
            ___fleetIndex >= module.moduleFleets.Length) return false;
        // Personal combat facts and craft pool facts arrive independently. Until the
        // matching pool exists, vanilla's direct dispatch lookup is unsafe.
        var slot = module.moduleFleets[___fleetIndex];
        if (slot.fleetId == 0) return true;
        var factories = ___gameData?.spaceSector?.skillSystem?.astroFactories;
        var fleets = ___isSpace ? ___gameData?.spaceSector?.combatSpaceSystem?.fleets :
            factories != null && slot.fleetAstroId > 0 && slot.fleetAstroId < factories.Length
                ? factories[slot.fleetAstroId]?.combatGroundSystem?.fleets : null;
        return fleets?.buffer != null && slot.fleetId > 0 && slot.fleetId < fleets.cursor &&
            slot.fleetId < fleets.buffer.Length && fleets.buffer[slot.fleetId].id == slot.fleetId;
    }
}
