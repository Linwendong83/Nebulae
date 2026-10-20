using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Trash;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Trash;
internal class PersistentDropProcessor : PacketProcessor<PersistentDropPacket>
{
    protected override void ProcessPacket(PersistentDropPacket packet, NebulaeConnection conn)
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null || packet.Acknowledgement) return;
            var owner = ((PlayerData)player.Data).PersistentId;
            if (!Multiplayer.Session.PropertyTransactions.OwnsCommittedOperation(packet.OperationId, owner)) return;
            Multiplayer.Session.Drops.Accept(packet, owner);
            conn.SendPacket(new PersistentDropPacket
            { OperationId = packet.OperationId, Ordinal = packet.Ordinal, Acknowledgement = true });
        }
        else Multiplayer.Session.Drops.Receive(packet);
    }
}
