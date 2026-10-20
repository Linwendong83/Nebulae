#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.PowerExchanger;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.PowerExchanger;
internal class PowerExchangerStorageUpdateProcessor : PacketProcessor<PowerExchangerStorageUpdatePacket>
{
    protected override void ProcessPacket(PowerExchangerStorageUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.powerSystem?.excPool;
        if (pool == null || packet.PowerExchangerIndex == -1 || packet.PowerExchangerIndex >= pool.Length ||
            pool[packet.PowerExchangerIndex].id == -1)
        {
            return;
        }
        pool[packet.PowerExchangerIndex].SetEmptyCount(packet.EmptyAccumulatorCount, packet.Inc);
        pool[packet.PowerExchangerIndex].SetFullCount(packet.FullAccumulatorCount, packet.Inc);
    }
}
