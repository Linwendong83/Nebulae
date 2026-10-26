#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class LobbyResponseProcessor : PacketProcessor<LobbyResponse>
{
    protected override void ProcessPacket(LobbyResponse packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        var combatSettings = new CombatSettings();
        using (var p = new BinaryUtils.Reader(packet.CombatSettingsData, 0))
        {
            combatSettings.Import(p.BinaryReader);
            p.RequireEnd();
        }
        ((LocalPlayer)Multiplayer.Session.LocalPlayer).IsHost = false;
        Multiplayer.Session.NumPlayers = packet.NumPlayers;
        Multiplayer.Session.IsInLobby = true;

        UIRoot.instance.galaxySelect._Open();
        UIRoot.instance.uiMainMenu._Close();

        var gameDesc = new GameDesc();
        gameDesc.SetForNewGame(packet.GalaxyAlgo, packet.GalaxySeed, packet.StarCount, 1, packet.ResourceMultiplier);
        gameDesc.starNameLCID = packet.StarNameLCID;
        gameDesc.isPeaceMode = packet.IsPeaceMode;
        gameDesc.goalLevel = (EGoalLevel)packet.GoalLevel;
        gameDesc.isSandboxMode = packet.IsSandboxMode;
        gameDesc.savedThemeIds = new int[packet.SavedThemeIds.Length];
        Array.Copy(packet.SavedThemeIds, gameDesc.savedThemeIds, packet.SavedThemeIds.Length);
        gameDesc.combatSettings = combatSettings;

        NebulaeModel.Utils.NativeGameAccess.SetGalaxyDescription(UIRoot.instance.galaxySelect, gameDesc);
        UIRoot.instance.galaxySelect.uiCombat.gameDesc = gameDesc;
        UIRoot.instance.galaxySelect.GoalSetting.gameDesc = gameDesc;
        UIRoot.instance.galaxySelect.SetStarmapGalaxy();
        UIRoot.instance.galaxySelect.sandboxToggle.isOn = gameDesc.isSandboxMode;
    }
}
