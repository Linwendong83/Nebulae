
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

                LobbyRequestProcessor.BeginPlayerJoin(player, conn, setBirthPoint: true);

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
            Multiplayer.Session.InventoryColumns = packet.InventoryColumns;
            ((LocalPlayer)Multiplayer.Session.LocalPlayer).IsHost = false;
            ((LocalPlayer)Multiplayer.Session.LocalPlayer).SetPlayerData(packet.LocalPlayerData, packet.IsNewPlayer);
            Multiplayer.Session.Goals.SetExistingPlayer(!packet.IsNewPlayer);
            Multiplayer.Session.Authority.ConfirmHandshake();

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
            DSPGame.GameDesc = NebulaeModel.Utils.NativeGameAccess.GetGalaxyDescription(UIRoot.instance.galaxySelect);

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
