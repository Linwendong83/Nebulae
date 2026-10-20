#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;

internal class PlayerSandGainedProcessor : PacketProcessor<PlayerSandGained>
{
    protected override void ProcessPacket(PlayerSandGained packet, NebulaeConnection conn)
    {
        // A client reports what it picked up; the host never reports, it already knows.
        if (IsClient || packet.Gained <= 0)
        {
            return;
        }

        var player = Players.Get(conn);
        if (player?.Data?.Mecha == null)
        {
            return;
        }

        // Accumulated rather than assigned: the checkpoint the client sends every few seconds carries
        // their own total, and this only has to keep the host's figure current in between.
        player.Data.Mecha.SandCount += packet.Gained;
    }
}
