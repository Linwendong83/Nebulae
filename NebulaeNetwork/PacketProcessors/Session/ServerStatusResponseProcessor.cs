#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;

// Status replies are consumed inline by ServerStatusProbe's dedicated processor.
internal class ServerStatusResponseProcessor : PacketProcessor<ServerStatusResponse>
{
    protected override void ProcessPacket(ServerStatusResponse packet, NebulaeConnection conn)
    {
    }
}
