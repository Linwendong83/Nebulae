#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.PowerGenerator;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.PowerGenerator;
internal class PowerGeneratorProductUpdateProcessor : PacketProcessor<PowerGeneratorProductUpdatePacket>
{
    protected override void ProcessPacket(PowerGeneratorProductUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId).factory?.powerSystem.genPool;
        if (pool != null && packet.PowerGeneratorIndex != -1 && packet.PowerGeneratorIndex < pool.Length &&
            pool[packet.PowerGeneratorIndex].id != -1)
        {
            pool[packet.PowerGeneratorIndex].productCount = packet.ProductCount;
        }
    }
}
