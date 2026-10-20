
#region
using HarmonyLib;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic
{
    [HarmonyPatch(typeof(UICombatSettingsDF))]
    internal class UICombatSettingsDF_Patch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(UICombatSettingsDF.ApplySettings))]
        public static void ApplySettings_Postfix(UICombatSettingsDF __instance)
        {
            if (!Multiplayer.IsInMultiplayerMenu || !Multiplayer.Session.LocalPlayer.IsHost)
            {
                return;
            }
            // Pending players receive lobby combat settings through their packet handler.
            var server = Multiplayer.Session.Server;
            var players = server.Players;
            server.SendToPlayers(players.Syncing, new LobbyUpdateCombatValues(__instance.gameDesc.combatSettings));
            server.SendToPlayers(players.Pending, new LobbyUpdateCombatValues(__instance.gameDesc.combatSettings));
        }
    }
}
