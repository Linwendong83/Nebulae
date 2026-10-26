using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Factory;

namespace NebulaeNetwork.PacketProcessors.Factory;

internal sealed class BuildingDeathProcessor : PacketProcessor<BuildingDeathPacket>
{
    protected override void ProcessPacket(BuildingDeathPacket packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        var factory = GameMain.galaxy?.PlanetById(packet.PlanetId)?.factory;
        if (factory != null) BuildingDeathReplication.Apply(factory, packet);
    }
}
