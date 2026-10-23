#region

using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class HandshakeResponseProcessor : PacketProcessor<HandshakeResponse>
{
    protected override void ProcessPacket(HandshakeResponse packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        var combatSettings = new CombatSettings();
        using (var p = new BinaryUtils.Reader(packet.CombatSettingsData, 0))
        {
            combatSettings.Import(p.BinaryReader);
            p.RequireEnd();
        }
        Multiplayer.Session.JoinSync.BeginLocal();
        ((LocalPlayer)Multiplayer.Session.LocalPlayer).IsHost = false;
        ((LocalPlayer)Multiplayer.Session.LocalPlayer).SetPlayerData(packet.LocalPlayerData, packet.IsNewPlayer);
        Multiplayer.Session.Goals.SetExistingPlayer(!packet.IsNewPlayer);

        Multiplayer.Session.Authority.ConfirmHandshake();

        Multiplayer.Session.IsInLobby = false;
        Multiplayer.ShouldReturnToJoinMenu = false;

        var gameDesc = new GameDesc();
        gameDesc.SetForNewGame(packet.GalaxyAlgo, packet.GalaxySeed, packet.StarCount, 1, packet.ResourceMultiplier);
        gameDesc.starNameLCID = packet.StarNameLCID;
        gameDesc.isPeaceMode = packet.IsPeaceMode;
        gameDesc.goalLevel = (EGoalLevel)packet.GoalLevel;
        gameDesc.isSandboxMode = packet.IsSandboxMode;
        gameDesc.savedThemeIds = packet.SavedThemeIds;
        gameDesc.combatSettings = combatSettings;
        //Request global part of GameData from host
        Multiplayer.Session.Network.SendPacket(new GlobalGameDataRequest());
        if (DSPGame.Game != null)
        {
            // Client: Close planetDetail before requesting to prevent error
            UIRoot.instance.uiGame.SetPlanetDetail(null);
            DSPGame.EndGame();
        }
        // Prepare gameDesc to later start in GlobalGameDataResponseProcessor
        DSPGame.GameDesc = gameDesc;

        UIRoot.instance.OpenLoadingUI();

        Multiplayer.Session.NumPlayers = packet.NumPlayers;
    }
}
