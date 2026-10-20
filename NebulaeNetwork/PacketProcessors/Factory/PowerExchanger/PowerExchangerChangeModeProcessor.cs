#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.PowerExchanger;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.PowerExchanger;
internal class PowerExchangerChangeModeProcessor : PacketProcessor<PowerExchangerChangeModePacket>
{
    protected override void ProcessPacket(PowerExchangerChangeModePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.powerSystem?.excPool;
        if (pool != null && packet.PowerExchangerIndex != -1 && packet.PowerExchangerIndex < pool.Length &&
            pool[packet.PowerExchangerIndex].id != -1)
        {
            pool[packet.PowerExchangerIndex].targetState = packet.Mode;
        }
    }
}
