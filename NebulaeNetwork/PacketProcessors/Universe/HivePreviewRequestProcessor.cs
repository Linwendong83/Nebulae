using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Universe;

internal sealed class HivePreviewRequestProcessor : PacketProcessor<HivePreviewRequest>
{
    protected override void ProcessPacket(HivePreviewRequest packet, NebulaeConnection conn)
    {
        if (!IsHost || packet.StarId < 0 || Players.Get(conn) is not { } player) return;
        // Pool mutations run at the next frame boundary, after the game workers have parked.
        Multiplayer.Session.Enemies.QueueHivePreview(player.Id, packet.StarId);
    }
}
