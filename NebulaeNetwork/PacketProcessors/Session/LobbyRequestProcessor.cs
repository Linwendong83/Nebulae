
#region
using NebulaeModel;
using System.Linq;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Session;
using NebulaeModel.Packets.Universe;
using NebulaeModel.Utils;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class LobbyRequestProcessor : PacketProcessor<LobbyRequest>
{
    protected override void ProcessPacket(LobbyRequest packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        var player = Players.Get(conn, EConnectionStatus.Pending);

        if (player is null)
        {
            Multiplayer.Session.Server.Disconnect(conn, DisconnectionReason.InvalidData);
            Log.Warn("WARNING: Player tried to enter lobby without being in the pending list");
            return;
        }

        if (!Multiplayer.Session.IsGameLoaded && GameMain.isFullscreenPaused)
        {
            Log.Warn("Reject connection because server is still loading");
            Multiplayer.Session.Server.Disconnect(conn, DisconnectionReason.HostStillLoading);
            return;
        }

        if (Multiplayer.Session.IsGameLoaded && !SaveManager.CanSave)
        {
            Server.Disconnect(conn, DisconnectionReason.InvalidData,
                "Multiplayer save data could not be loaded. Restore the server save before joining.");
            return;
        }

        if (!VersionCheck(packet, out var disconnectionReason, out var reasonMessage))
        {
            Diagnostics.Warn("handshake:refused:" + conn.Id, () => $"[network] handshake refused connection={conn.Id} player={player.Id} reason={disconnectionReason} localVersion={Config.ModVersion} remoteVersion={packet.ModVersion}");

            Multiplayer.Session.Server.Disconnect(conn, disconnectionReason, reasonMessage);
            return;
        }

        var isNewUser = false;

        // Load old data of the client
        var clientCertHash = CryptoUtils.Hash(packet.ClientCert);
        var previousSeats = Players.Connected.Values.Concat(Players.Syncing.Values).Concat(Players.Pending.Values)
            .Where(other => !ReferenceEquals(other.Connection, conn) &&
                            (other.Data as PlayerData)?.PersistentId == clientCertHash).ToArray();
        foreach (var previous in previousSeats)
        {
            if (previous.Connection.IsAlive)
            {
                Server.Disconnect(conn, DisconnectionReason.InvalidData, "This player identity is already connected. Disconnect it before joining again.");
                return;
            }
            ((NebulaeNetwork.Server)Server).OnSocketDisconnection(previous.Connection);
        }
        conn.CompleteHandshake();
        Multiplayer.Session.Authority.ConfirmHandshake();
        ((PlayerData)player.Data).PersistentId = clientCertHash;
        if (SaveManager.PlayerSaves.TryGetValue(clientCertHash, out var value))
        {
            var playerData = value;
            player.LoadUserData(playerData);
            // Old servers could save an entry before the first inventory snapshot arrived.
            isNewUser = playerData.Mecha?.ReactorStorage == null;
        }
        else
        {
            // store player data once he fully loaded into the game (SyncCompleteProcessor)
            isNewUser = true;
        }

        // Add the username to the player data
        Log.Debug($"[network] handshake completed connection={conn.Id} player={player.Id} newPlayer={isNewUser} remoteVersion={packet.ModVersion}");
        player.Data.Username = !string.IsNullOrWhiteSpace(packet.Username) ? packet.Username : $"Player {player.Id}";
        if (Multiplayer.Session.IsGameLoaded)
        {
            Multiplayer.Session.PropertyTransactions.RestoreServerPlayer((PlayerData)player.Data);
        }

        if (!((PlayerData)player.Data).SessionCounted)
        {
            ((PlayerData)player.Data).SessionCounted = true;
            Multiplayer.Session.NumPlayers += 1;
        }

        // While the host's game is loaded, skip the lobby page for every joining player:
        // the page offers nothing a running server does not already own. Only a host still
        // configuring a new game keeps players in the lobby as a waiting room.
        if (Multiplayer.Session.IsGameLoaded)
        {
            Multiplayer.Session.Server.Players.TryUpgrade(player, EConnectionStatus.Syncing);

            if (isNewUser)
            {
                // A first-time player carries no spawn data yet: assign the host's birth
                // planet before the data is sent.
                NebulaeWorld.Player.SpawnManager.SetBirthPoint((PlayerData)player.Data);
            }

            Multiplayer.Session.World.OnPlayerJoining(player.Data.Username);
            Multiplayer.Session.Planets.HostPreparation.Prepare(
                GameMain.galaxy.PlanetById(player.Data.LocalPlanetId), conn);

            // Make sure that each player that is currently in the game receives that a new player as join so they can create its RemotePlayerCharacter
            var pdata = new PlayerJoining((PlayerData)player.Data.CreateCopyWithoutMechaData(),
                Multiplayer.Session.NumPlayers); // Remove inventory from mecha data

            Server.SendPacket(pdata);

            //Add current tech bonuses to the connecting player based on the Host's mecha
            ((MechaData)player.Data.Mecha).TechBonuses = new PlayerTechBonuses(GameMain.mainPlayer.mecha);

            var gameDesc = GameMain.data.gameDesc;
            byte[] combatSettingsData;
            using (var p = new BinaryUtils.Writer())
            {
                gameDesc.combatSettings.Export(p.BinaryWriter);
                combatSettingsData = p.CloseAndGetBytes();
            }
            player.SendPacket(new HandshakeResponse(in gameDesc, combatSettingsData, isNewUser, (PlayerData)player.Data, Multiplayer.Session.NumPlayers));

            SendAuthorityWelcome(player);
        }
        else
        {
            var gameDesc = NebulaeModel.Utils.NativeGameAccess.GetGalaxyDescription(UIRoot.instance.galaxySelect);
            byte[] combatSettingsData;
            using (var p = new BinaryUtils.Writer())
            {
                gameDesc.combatSettings.Export(p.BinaryWriter);
                combatSettingsData = p.CloseAndGetBytes();
            }
            player.SendPacket(new LobbyResponse(in gameDesc, combatSettingsData,
                Multiplayer.Session.NumPlayers));

            // Send overriden Planet and Star names
            player.SendPacket(new NameInputPacket(UIRoot.instance.galaxySelect.starmap.galaxyData));
        }
    }

    internal static void SendAuthorityWelcome(INebulaePlayer player)
    {
        var runtime = Multiplayer.Session.AuthorityRuntime;
        var identity = Multiplayer.Session.Authority;
        if (runtime is null || !identity.IsHost || !identity.IsActive) return;

        var connectionEpoch = runtime.AssignConnectionEpoch(player.Id);
        Log.Debug($"[authority] welcome connection={player.Connection.Id} player={player.Id} world={identity.Epoch} connectionEpoch={connectionEpoch}");
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Welcome, identity.Epoch,
            connectionEpoch, sequence: 1, hostTick: GameMain.gameTick, claimedPlayerId: player.Id,
            payloadLength: 0);
        var welcome = AuthorityWelcomePacket.Create(header);
        player.SendPacket(welcome);
    }

    internal static bool VersionCheck(in LobbyRequest packet, out DisconnectionReason reason, out string reasonString)
    {
        reason = DisconnectionReason.Normal;
        reasonString = null;
        if (string.IsNullOrWhiteSpace(packet.ModVersion) || packet.ClientCert == null || packet.ClientCert.Length == 0)
        {
            reason = DisconnectionReason.InvalidData;
            return false;
        }
        reason = DisconnectionReason.ModVersionMismatch;
        if (!NebulaeVersion.IsCompatible(Config.ModVersion, packet.ModVersion))
        {
            reasonString = $"dsp.nebulae;{packet.ModVersion};{Config.ModVersion}";
            return false;
        }
        reason = DisconnectionReason.Normal;
        reasonString = null;
        return true;
    }
}
