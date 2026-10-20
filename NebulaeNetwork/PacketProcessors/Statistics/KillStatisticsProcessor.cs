using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Statistics;
internal class KillStatisticsProcessor : PacketProcessor<KillStatisticsPacket>
{
    protected override void ProcessPacket(KillStatisticsPacket packet, NebulaeConnection conn)
    {
        if (IsHost || !Multiplayer.Session.IsGameLoaded) return;
        if (packet.Snapshot) Multiplayer.Session.Kills.ApplySnapshot(packet.Data);
        else Multiplayer.Session.Kills.ApplyDelta(packet);
    }
}
internal class KillStatisticsRequestProcessor : PacketProcessor<KillStatisticsRequest>
{
    protected override void ProcessPacket(KillStatisticsRequest packet, NebulaeConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player == null) return;
        if (packet.Subscribe) Multiplayer.Session.Kills.Subscribe(player.Id, conn);
        else Multiplayer.Session.Kills.Unsubscribe(player.Id);
    }
}
