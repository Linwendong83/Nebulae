#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Splitter;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Splitter;
internal class SplitterPriorityChangeProcessor : PacketProcessor<SplitterPriorityChangePacket>
{
    protected override void ProcessPacket(SplitterPriorityChangePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.cargoTraffic.splitterPool;
        if (pool != null && packet.SplitterIndex != -1 && packet.SplitterIndex < pool.Length &&
            pool[packet.SplitterIndex].id != -1)
        {
            pool[packet.SplitterIndex].SetPriority(packet.Slot, packet.IsPriority, packet.Filter);
        }
    }
}
