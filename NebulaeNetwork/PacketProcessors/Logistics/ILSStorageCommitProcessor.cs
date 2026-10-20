using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Logistics;

internal class ILSStorageCommitProcessor : PacketProcessor<ILSStorageCommitPacket>
{
    protected override void ProcessPacket(ILSStorageCommitPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) Multiplayer.Session.Ships.ReceiveStorage(packet);
    }
}
