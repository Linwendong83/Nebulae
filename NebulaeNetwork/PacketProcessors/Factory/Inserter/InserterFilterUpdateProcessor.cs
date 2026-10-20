#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Inserter;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Inserter;
internal class InserterFilterUpdateProcessor : PacketProcessor<InserterFilterUpdatePacket>
{
    protected override void ProcessPacket(InserterFilterUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factorySystem?.inserterPool;
        if (pool == null || packet.InserterIndex == -1 || packet.InserterIndex >= pool.Length ||
            pool[packet.InserterIndex].id == -1)
        {
            return;
        }
        pool[packet.InserterIndex].filter = packet.ItemId;
        var entityId = pool[packet.InserterIndex].entityId;
        GameMain.galaxy.PlanetById(packet.PlanetId).factory.entitySignPool[entityId].iconId0 = (uint)packet.ItemId;
        GameMain.galaxy.PlanetById(packet.PlanetId).factory.entitySignPool[entityId].iconType =
            packet.ItemId <= 0 ? 0U : 1U;
    }
}
