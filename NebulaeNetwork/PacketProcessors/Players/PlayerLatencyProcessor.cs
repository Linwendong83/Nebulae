using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;
using NebulaeWorld.Player;

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerLatencyProcessor : PacketProcessor<PlayerLatencyPacket>
{
    protected override void ProcessPacket(PlayerLatencyPacket packet, NebulaeConnection conn)
    {
        if (packet.Kind == 0)
        {
            conn.SendPacket(new PlayerLatencyPacket
            {
                Kind = 1,
                PlayerId = Multiplayer.Session.LocalPlayer.Id,
                SentTicks = packet.SentTicks
            });
        }
        else if (packet.Kind == 1)
        {
            var id = IsHost ? Players.Get(conn)?.Id ?? (ushort)0 : packet.PlayerId;
            PlayerLatencyTracker.Record(id, packet.SentTicks);
        }
        else if (packet.Kind == 2 && IsClient)
        {
            PlayerLatencyTracker.ApplySnapshot(packet.PlayerIds, packet.Milliseconds);
        }
    }
}
