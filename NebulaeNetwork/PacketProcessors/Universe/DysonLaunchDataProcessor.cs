#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;
internal class DysonLaunchDataProcessor : PacketProcessor<DysonLaunchDataPacket>
{
    protected override void ProcessPacket(DysonLaunchDataPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }
        Multiplayer.Session.Launch.ImportPacket(packet);
    }
}
