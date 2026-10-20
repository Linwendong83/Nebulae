#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeWorld;
using NebulaeWorld.Factory;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;
internal class BeltUpdatePutItemOnProcessor : PacketProcessor<BeltUpdatePutItemOnPacket>
{
    protected override void ProcessPacket(BeltUpdatePutItemOnPacket packet, NebulaeConnection conn)
    {
        if (!BeltManager.TryPutItemOnBelt(packet))
        {
            Multiplayer.Session.Belts.RegiserbeltPutdownPacket(packet);
        }
    }
}
