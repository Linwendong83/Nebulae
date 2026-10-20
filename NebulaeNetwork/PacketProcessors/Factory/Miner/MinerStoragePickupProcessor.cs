#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Miner;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Miner;
internal class MinerStoragePickupProcessor : PacketProcessor<MinerStoragePickupPacket>
{
    protected override void ProcessPacket(MinerStoragePickupPacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factorySystem?.minerPool;
        if (pool == null || packet.MinerIndex == -1 || packet.MinerIndex >= pool.Length || pool[packet.MinerIndex].id == -1)
        {
            return;
        }
        pool[packet.MinerIndex].productCount = 0;
        pool[packet.MinerIndex].productId = 0;
    }
}
