using NebulaeModel.Networking;
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
            var data = (PlayerData)player.Data;
            packet.PlayerId = player.Id;
            if (double.IsNaN(packet.CoreEnergyDebitAcknowledged) || double.IsInfinity(packet.CoreEnergyDebitAcknowledged) || packet.CoreEnergyDebitAcknowledged < 0) return;
            var playerId = player.Id;
            var requestedRevision = packet.Life.Revision;
            var connection = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(playerId);
            Multiplayer.Session.CombatAuthority.QueueCheckpoint(data, packet.PlayerSnapshot, packet.LastCombatCommand,
                packet.CoreEnergyDebitAcknowledged, packet.DebitItemsAcknowledged, packet.DebitTotalsAcknowledged, accepted =>
            {
                if (!Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(playerId).Equals(connection)) return;
                if (accepted)
                {
                    var update = new PlayerLifePacket { PlayerId = playerId, Life = data.Life, PlayerSnapshot = System.Array.Empty<byte>() };
                    Server.SendPacketExclude(update, conn);
                    Multiplayer.Session.Life.ApplyRemote(playerId, data.Life);
                }
                conn.SendPacket(PlayerLifePacket.CreateReceipt(playerId, requestedRevision));
            }, packet.CombatRevision, packet.InventoryTransferSequence);
        }
        else if (packet.Acknowledgement) Multiplayer.Session.Life.Acknowledge(packet.Life.Revision);
        else Multiplayer.Session.Life.ApplyRemote(packet.PlayerId, packet.Life);
    }
}
