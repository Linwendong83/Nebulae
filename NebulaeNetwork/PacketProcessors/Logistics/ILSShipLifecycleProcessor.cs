using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Logistics;

internal class ILSShipLifecycleProcessor : PacketProcessor<ILSShipLifecyclePacket>
{
    protected override void ProcessPacket(ILSShipLifecyclePacket packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        Multiplayer.Session.Ships.ReceiveLifecycle(packet);
    }
}
