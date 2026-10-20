#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Inserter;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Inserter;
internal class InserterItemUpdateProcessor : PacketProcessor<InserterItemUpdatePacket>
{
    protected override void ProcessPacket(InserterItemUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId).factory?.factorySystem.inserterPool;
        if (pool == null || packet.InserterIndex == -1 || packet.InserterIndex >= pool.Length ||
            pool[packet.InserterIndex].id == -1)
        {
            return;
        }
        pool[packet.InserterIndex].itemId = packet.ItemId;
        pool[packet.InserterIndex].itemCount = packet.ItemCount;
        pool[packet.InserterIndex].itemInc = packet.ItemInc;
        pool[packet.InserterIndex].stackCount = packet.StackCount;
    }
}
