#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Ejector;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Ejector;
internal class EjectorOrbitUpdateProcessor : PacketProcessor<EjectorOrbitUpdatePacket>
{
    protected override void ProcessPacket(EjectorOrbitUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factorySystem?.ejectorPool;
        if (pool != null && packet.EjectorIndex != -1 && packet.EjectorIndex < pool.Length &&
            pool[packet.EjectorIndex].id != -1)
        {
            pool[packet.EjectorIndex].SetOrbit(packet.NewOrbitIndex);
        }
    }
}
