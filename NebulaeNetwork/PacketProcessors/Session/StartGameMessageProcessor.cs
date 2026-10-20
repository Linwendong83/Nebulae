
#region
using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class StartGameMessageProcessor : PacketProcessor<StartGameMessage>
{
    public StartGameMessageProcessor()
    {
    }

    protected override void ProcessPacket(StartGameMessage packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            if (Multiplayer.Session.IsGameLoaded)
            {
                var player = Players.Get(conn, EConnectionStatus.Pending);
                if (player is null)
                {
                    Multiplayer.Session.Server.Disconnect(conn, DisconnectionReason.InvalidData);
                    Log.Warn("WARNING: Player tried to enter the game without being in the pending list");
                    return;
                }

                Multiplayer.Session.Server.Players.TryUpgrade(player, EConnectionStatus.Syncing);
                NebulaeWorld.Player.SpawnManager.SetBirthPoint((PlayerData)player.Data);

                Multiplayer.Session.World.OnPlayerJoining(player.Data.Username);
                Multiplayer.Session.Planets.HostPreparation.Prepare(
                    GameMain.galaxy.PlanetById(player.Data.LocalPlanetId), conn);

                // Make sure that each player that is currently in the game receives that a new player as join so they can create its RemotePlayerCharacter
                var pdata = new PlayerJoining((PlayerData)player.Data.CreateCopyWithoutMechaData(),
                    Multiplayer.Session.NumPlayers); // Remove inventory from mecha data

                Server.SendPacket(pdata);

                //Add current tech bonuses to the connecting player based on the Host's mecha
                ((MechaData)player.Data.Mecha).TechBonuses = new PlayerTechBonuses(GameMain.mainPlayer.mecha);

                var identity = ((PlayerData)player.Data).PersistentId;
                var isNewPlayer = !SaveManager.PlayerSaves.TryGetValue(identity, out var savedData) ||
                                  savedData.Mecha?.ReactorStorage == null;
                conn.SendPacket(new StartGameMessage(true, (PlayerData)player.Data, isNewPlayer));
                LobbyRequestProcessor.SendAuthorityWelcome(player);
            }
            else
            {
                conn.SendPacket(new StartGameMessage(false, null));
            }
        }
        else if (packet.IsAllowedToStart)
        {
            Multiplayer.Session.JoinSync.BeginLocal();
            Multiplayer.Session.Planets.BeginWorldLoad();
            ((LocalPlayer)Multiplayer.Session.LocalPlayer).IsHost = false;
            ((LocalPlayer)Multiplayer.Session.LocalPlayer).SetPlayerData(packet.LocalPlayerData, packet.IsNewPlayer);
            Multiplayer.Session.Goals.SetExistingPlayer(!packet.IsNewPlayer);

            UIRoot.instance.uiGame.planetDetail.gameObject.SetActive(false);
            Multiplayer.Session.IsInLobby = false;
            Multiplayer.ShouldReturnToJoinMenu = false;

            //Request global part of GameData from host
            Multiplayer.Session.Network.SendPacket(new GlobalGameDataRequest());
            if (DSPGame.Game != null)
            {
                DSPGame.EndGame();
            }
            // Prepare gameDesc to later start in GlobalGameDataResponseProcessor
            DSPGame.GameDesc = UIRoot.instance.galaxySelect.gameDesc;

            UIRoot.instance.OpenLoadingUI();
        }
        else
        {
            UIRoot.instance.CloseLoadingUI();
            InGamePopup.ShowInfo("Server Busy".Translate(), "The host is not ready to let you in, please wait!".Translate(),
                "OK".Translate());
        }
    }
}
