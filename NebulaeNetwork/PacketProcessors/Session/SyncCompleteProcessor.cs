
#region
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeModel.Packets.Session;
using NebulaeModel.Utils;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class SyncCompleteProcessor : PacketProcessor<SyncComplete>
{
    protected override void ProcessPacket(SyncComplete packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            ServerSyncComplete(packet, conn);
        }
        else // IsClient
        {
            // Everyone is now connected, we can safely spawn the player model of all the other players that are currently connected
            foreach (var playerData in packet.AllPlayers)
            {
                if (playerData.PlayerId != Multiplayer.Session.LocalPlayer.Id)
                {
                    Multiplayer.Session.World.SpawnRemotePlayerModel(playerData);
                }
            }

            if (packet.IsFinal)
            {
                try { Multiplayer.Session.JoinSync.Final(packet); }
                catch (System.Exception error) { Multiplayer.Session.JoinSync.FailLocal(error); }
            }
        }
    }

    private void ServerSyncComplete(SyncComplete packet, NebulaeConnection conn)
    {
        var player = Players.Get(conn, EConnectionStatus.Syncing);
        if (player == null)
        {
            Log.Warn("Received a SyncComplete packet, but no player is joining.");
            return;
        }

        // store the player now, not when he enters the lobby. that would cause weird teleportations when clients reenter the lobby without ever having loaded into the game
        var clientCertHash = ((NebulaeModel.DataStructures.PlayerData)player.Data).PersistentId;
        if (clientCertHash != CryptoUtils.Hash(packet.ClientCert))
        {
            Server.Disconnect(conn, DisconnectionReason.InvalidData);
            return;
        }

        if (packet.JoinGeneration != conn.Id) return;
        // These imports replace editable personal UI state. Put them before the replay
        // barrier, while input is still locked, rather than after the client sends its ack.
        SendPersonalState(player);
        Multiplayer.Session.JoinSync.Loaded(conn, packet.JoinGeneration, () => CompletePlayer(player, conn));
    }

    private static void SendPersonalState(NebulaeModel.Contracts.INebulaePlayer player)
    {
        if (player.Data.Appearance != null)
        {
            using var writer = new BinaryUtils.Writer();
            player.Data.Appearance.Export(writer.BinaryWriter);
            player.SendPacket(new PlayerMechaArmor(player.Id, writer.CloseAndGetBytes()));
        }
        if (player.Data.DIYAppearance != null)
        {
            using var writer = new BinaryUtils.Writer();
            player.Data.DIYAppearance.Export(writer.BinaryWriter);
            player.SendPacket(new PlayerMechaDIYArmor(writer.CloseAndGetBytes(), player.Data.DIYItemId,
                player.Data.DIYItemValue));
        }
        if (player.Data.DashboardData?.Length > 0)
            player.SendPacket(new PlayerDashboardPacket(player.Id, player.Data.DashboardData));
    }

    private void CompletePlayer(NebulaeModel.Contracts.INebulaePlayer player, NebulaeConnection conn)
    {
        var clientCertHash = ((NebulaeModel.DataStructures.PlayerData)player.Data).PersistentId;
        SaveManager.TryAdd(clientCertHash, player.Data);

        Multiplayer.Session.Server.Players.TryUpgrade(player, EConnectionStatus.Connected);
        Multiplayer.Session.Metadata.Join(player.Id, clientCertHash);
        player.SendPacket(new NebulaeModel.Packets.Statistics.KillStatisticsPacket
        { Snapshot = true, ToTick = GameMain.gameTick, Data = Multiplayer.Session.Kills.ExportSnapshot(player.Id) });
        // Import the latest transport revisions after the join tail, so neither a newer
        // eager loading response nor an old lifecycle delta can overwrite the other.
        Multiplayer.Session.Ships.FlushLifecycle();
        Logistics.ILSgStationPoolSyncRequestProcessor.SendSnapshot(0, conn);
        if (player.Data.LocalStarId > 0)
            Logistics.ILSArriveStarPlanetRequestProcessor.SendSnapshot(player.Data.LocalStarId, 0, conn);
        // Event-driven visuals do not resend every live object periodically. A joining client
        // receives an explicit baseline even when its saved star already matches its first pose.
        Multiplayer.Session.BattleVisuals.SendInitial(conn, player.Data.LocalStarId);
        Multiplayer.Session.PlayerWeapons.SendInitial(conn, player.Data.LocalStarId);

        // Since the player is now connected, we can safely spawn his player model
        SimulatedWorld.OnPlayerJoinedGame(player);

        var inGamePlayersDatas = Multiplayer.Session.Server.Players.GetAllPlayerData(Multiplayer.IsDedicated ? null : Multiplayer.Session.LocalPlayer.Data);
        Server.SendPacket(new SyncComplete(inGamePlayersDatas));

        // Since the host is always in the game he could already have changed his mecha armor, so send it to the new player.
        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.mainPlayer.mecha.appearance.Export(writer.BinaryWriter);
            player.SendPacket(new PlayerMechaArmor(Multiplayer.Session.LocalPlayer.Id, writer.CloseAndGetBytes()));
        }

        // if the client had used a custom armor we should have saved a copy of it, so send it back
        if (player.Data.Appearance != null)
        {
            using (var writer = new BinaryUtils.Writer())
            {
                player.Data.Appearance.Export(writer.BinaryWriter);
                Server.SendPacketExclude(new PlayerMechaArmor(player.Id, writer.CloseAndGetBytes()), conn);
            }

            // and load custom appearance on host side too
            // this is the code from PlayerMechaArmonrProcessor
            using (Multiplayer.Session.World.GetRemotePlayersModels(
                       out var remotePlayersModels))
            {
                if (remotePlayersModels.TryGetValue(player.Id, out var playerModel))
                {
                    if (playerModel.MechaInstance.appearance == null)
                    {
                        playerModel.MechaInstance.appearance = new MechaAppearance();
                        playerModel.MechaInstance.appearance.Init();
                    }

                    player.Data.Appearance.CopyTo(playerModel.MechaInstance.appearance);
                    playerModel.PlayerInstance.mechaArmorModel.RefreshAllPartObjects();
                    playerModel.PlayerInstance.mechaArmorModel.RefreshAllBoneObjects();
                    playerModel.MechaInstance.appearance.NotifyAllEvents();
                    playerModel.PlayerInstance.mechaArmorModel._Init(playerModel.PlayerInstance);
                    playerModel.PlayerInstance.mechaArmorModel._OnOpen();
                }
            }
        }

        Multiplayer.Session.World.OnAllPlayersSyncCompleted();
        Multiplayer.Session.World.IsPlayerJoining = Players.Syncing.Count != 0;
    }
}
