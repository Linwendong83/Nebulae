#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Warning;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Warning;
internal class WarningDataRequestProcessor : PacketProcessor<WarningDataRequest>
{
    protected override void ProcessPacket(WarningDataRequest packet, NebulaeConnection conn)
    {
        Multiplayer.Session.Warning.HandleRequest(packet, conn);
    }
}
