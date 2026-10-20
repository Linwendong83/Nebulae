#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class StorageUIProcessor : PacketProcessor<StorageUI>
{
    protected override void ProcessPacket(StorageUI packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            // always update values for host
            packet.ShouldRefund = false;
            Multiplayer.Session.StationsUI.UpdateStorage(packet);

            // broadcast to other clients 
            Server.SendPacketExclude(packet, conn);

            // as we block some methods for the client he must run it once he receives this packet.
            // but only the one issued the request should get items refund
            packet.ShouldRefund = true;
            conn.SendPacket(packet);
        }

        if (IsClient)
        {
            Multiplayer.Session.StationsUI.UpdateStorage(packet);
        }
    }
}
