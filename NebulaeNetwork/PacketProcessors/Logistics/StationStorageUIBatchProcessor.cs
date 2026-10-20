#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics;

internal class StationStorageUIBatchProcessor : PacketProcessor<StationStorageUIBatchPacket>
{
    protected override void ProcessPacket(StationStorageUIBatchPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            // The host applies the batch as one update, then fans the same bytes out. As with the
            // single-entry packet, only the player who asked for the change may have items refunded.
            packet.ShouldRefund = false;
            Multiplayer.Session.StationsUI.UpdateStorageBatch(packet);

            Server.SendPacketExclude(packet, conn);

            packet.ShouldRefund = true;
            conn.SendPacket(packet);
        }

        if (IsClient)
        {
            Multiplayer.Session.StationsUI.UpdateStorageBatch(packet);
        }
    }
}
