using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(UIZS_FleetEntry), "OnCommandButtonClick")]
internal static class FleetCommandButton_Patch
{
    private static bool Client => Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.Authority.IsActive;

    [HarmonyPrefix]
    internal static bool Prefix(Mecha ___mecha, bool ___isSpace, int ___fleetIndex)
    {
        if (!Client) return true;
        var module = ___isSpace ? ___mecha.spaceCombatModule : ___mecha.groundCombatModule;
        // First launch already travels through LaunchFleet_Prefix. The deployed branch only
        // writes native fields, so it needs its own explicit command and prediction lifetime.
        ref var slot = ref module.moduleFleets[___fleetIndex];
        if (slot.fleetId == 0 || !module.moduleEnabled || !slot.fleetEnabled) return true;
        if (!___isSpace && ___mecha.player.factory?.planetId != slot.fleetAstroId) return false;
        if (!FleetCommandMode.TryGetPool(module, ___fleetIndex, out var fleets)) return false;
        var inCommand = !fleets.buffer[slot.fleetId].dispatch || !slot.inCommand;
        if (Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetCommand,
            ___fleetIndex + (___isSpace ? 100 : 0), inCommand ? 1 : 0), default, out var sequence))
            Multiplayer.Session.FleetCommands.Begin(module, ___fleetIndex, false, sequence, inCommand);
        return false;
    }
}
