using NebulaeModel.Networking;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Combat.Mecha;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Combat.Mecha;
internal class PlayerLifeProcessor : PacketProcessor<PlayerLifePacket>
{
    protected override void ProcessPacket(PlayerLifePacket packet, NebulaeConnection conn)
    {
        if (packet.Life == null) return;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null || packet.Acknowledgement || packet.PlayerSnapshot == null) return;
            var identity = Multiplayer.Session.Authority;
            var actorConnection = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(player.Id);
            if (new AuthorityEpoch(packet.EpochHigh, packet.EpochLow) != identity.Epoch ||
                packet.ConnectionEpoch != actorConnection.Value) return;
            var data = (PlayerData)player.Data;
            packet.PlayerId = player.Id;
            if (double.IsNaN(packet.CoreEnergyDebitAcknowledged) || double.IsInfinity(packet.CoreEnergyDebitAcknowledged) || packet.CoreEnergyDebitAcknowledged < 0) return;
            var playerId = player.Id;
            var requestedRevision = packet.Life.Revision;
            var connection = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(playerId);
            var queued = Multiplayer.Session.CombatAuthority.QueueCheckpoint(data, packet.PlayerSnapshot, packet.LastCombatCommand,
                packet.CoreEnergyDebitAcknowledged, packet.DebitItemsAcknowledged, packet.DebitTotalsAcknowledged, (status, reason) =>
            {
                if (!Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(playerId).Equals(connection)) return;
                if (status is CheckpointReceiptStatus.Accepted or CheckpointReceiptStatus.Duplicate)
                {
                    var update = new PlayerLifePacket { PlayerId = playerId, Life = data.Life, PlayerSnapshot = System.Array.Empty<byte>() };
                    Server.SendPacketExclude(update, conn);
                    Multiplayer.Session.Life.ApplyRemote(playerId, data.Life);
                }
                var receipt = PlayerLifePacket.CreateReceipt(playerId, requestedRevision, status, data.Life.Revision, reason);
                receipt.EpochHigh = identity.Epoch.High; receipt.EpochLow = identity.Epoch.Low;
                receipt.ConnectionEpoch = connection.Value;
                conn.SendPacket(receipt);
            }, packet.CombatRevision, packet.InventoryTransferSequence);
            if (!queued)
            {
                var receipt = PlayerLifePacket.CreateReceipt(playerId, requestedRevision, CheckpointReceiptStatus.Retry,
                    data.Life.Revision, CheckpointRejectReason.Busy);
                receipt.EpochHigh = identity.Epoch.High; receipt.EpochLow = identity.Epoch.Low; receipt.ConnectionEpoch = connection.Value;
                conn.SendPacket(receipt);
            }
        }
        else if (packet.Acknowledgement)
        {
            var identity = Multiplayer.Session.Authority;
            if (packet.PlayerId != Multiplayer.Session.LocalPlayer.Id ||
                new AuthorityEpoch(packet.EpochHigh, packet.EpochLow) != identity.Epoch ||
                packet.ConnectionEpoch != identity.Connection.Value) return;
            Multiplayer.Session.Life.Acknowledge(packet);
        }
        else Multiplayer.Session.Life.ApplyRemote(packet.PlayerId, packet.Life);
    }
}
