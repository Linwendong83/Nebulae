using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;
using NebulaeWorld.GameStates;

namespace NebulaeNetwork.PacketProcessors.GameStates;
internal class MetadataProductionProcessor : PacketProcessor<MetadataProductionPacket>
{
    protected override void ProcessPacket(MetadataProductionPacket packet, NebulaeConnection conn)
    {
        if (IsHost || !Multiplayer.Session.IsGameLoaded || packet.WorldId != SaveManager.WorldId) return;
        if (MetadataManager.Apply(packet))
            conn.SendPacket(new MetadataReceiptPacket { WorldId = packet.WorldId, Sequence = packet.Sequence });
    }
}
internal class MetadataReceiptProcessor : PacketProcessor<MetadataReceiptPacket>
{
    protected override void ProcessPacket(MetadataReceiptPacket packet, NebulaeConnection conn)
    {
        if (!IsHost || packet.WorldId != SaveManager.WorldId) return;
        var player = Players.Get(conn);
        if (player != null) Multiplayer.Session.Metadata.Acknowledge(player.Id, packet.Sequence);
    }
}
