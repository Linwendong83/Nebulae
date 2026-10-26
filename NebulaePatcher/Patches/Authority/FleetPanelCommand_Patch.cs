using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

// Keep vanilla's group and keyboard selection rules; transmit the mode writes those
// paths make directly, including exiting command mode after CancelFleetOrder.
[HarmonyPatch(typeof(UIZS_FleetPanel))]
internal static class FleetPanelCommand_Patch
{
    private static bool Client => Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.Authority.IsActive;

    [HarmonyPrefix, HarmonyPatch("OnAllCommandButtonClick")]
    internal static bool Ready(UIZS_FleetPanel __instance)
    {
        if (!Client) return true;
        var module = __instance.fleetTypeIndex == 1 ? __instance.mecha.spaceCombatModule : __instance.mecha.groundCombatModule;
        if (!module.moduleEnabled) return true;
        // Personal combat facts can arrive before their local fleet replicas. Vanilla
        // reads dispatch (and calls CancelFleetOrder) through these arrays directly.
        // Wait for the whole group so a click cannot apply only part of a batch.
        var sector = __instance.gameData?.spaceSector;
        for (var i = 0; i < module.fleetCount; i++)
        {
            var slot = module.moduleFleets[i];
            if (!slot.fleetEnabled || slot.fleetId == 0) continue;
            var factories = sector?.galaxy?.astrosFactory;
            var fleets = module.isSpace ? sector?.combatSpaceSystem?.fleets :
                factories != null && slot.fleetAstroId > 0 && slot.fleetAstroId < factories.Length
                    ? factories[slot.fleetAstroId]?.combatGroundSystem?.fleets : null;
            if (fleets?.buffer == null || slot.fleetId < 0 || slot.fleetId >= fleets.cursor ||
                slot.fleetId >= fleets.buffer.Length || fleets.buffer[slot.fleetId].id != slot.fleetId) return false;
        }
        return true;
    }

    [HarmonyPrefix, HarmonyPatch("RefreshCompletely"), HarmonyPatch("OnAllCommandButtonClick")]
    internal static void Before(Mecha ___mecha, out (int Ground, int Space)? __state) =>
        __state = Client ? (Modes(___mecha.groundCombatModule), Modes(___mecha.spaceCombatModule)) : null;

    [HarmonyPostfix, HarmonyPatch("RefreshCompletely"), HarmonyPatch("OnAllCommandButtonClick")]
    internal static void After(Mecha ___mecha, (int Ground, int Space)? __state)
    {
        if (!__state.HasValue) return;
        Publish(___mecha.groundCombatModule, __state.Value.Ground);
        Publish(___mecha.spaceCombatModule, __state.Value.Space);
    }

    internal static int Modes(CombatModuleComponent module)
    {
        var mask = 0;
        for (var i = 0; i < module.fleetCount; i++) if (module.moduleFleets[i].inCommand) mask |= 1 << i;
        return mask;
    }

    private static void Publish(CombatModuleComponent module, int before)
    {
        for (var i = 0; i < module.fleetCount; i++)
        {
            var old = (before & (1 << i)) != 0;
            var mode = module.moduleFleets[i].inCommand;
            if (old == mode || Multiplayer.Session.FleetCommands.HasMode(module, i, mode)) continue;
            module.moduleFleets[i].inCommand = old;
            if (Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetCommand,
                i + (module.isSpace ? 100 : 0), mode ? 1 : 0), default, out var sequence))
                Multiplayer.Session.FleetCommands.Begin(module, i, false, sequence, mode);
        }
    }
}
