#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Tank;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Tank;
internal class TankStorageUpdateProcessor : PacketProcessor<TankStorageUpdatePacket>
{
    protected override void ProcessPacket(TankStorageUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factoryStorage?.tankPool;
        if (pool == null || packet.TankIndex == -1 || packet.TankIndex >= pool.Length || pool[packet.TankIndex].id == -1)
        {
            return;
        }
        pool[packet.TankIndex].fluidId = packet.FluidId;
        pool[packet.TankIndex].fluidCount = packet.FluidCount;
        pool[packet.TankIndex].fluidInc = packet.FluidInc;
    }
}
