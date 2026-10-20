#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory;
internal class EntityWarningSwitchProcessor : PacketProcessor<EntityWarningSwitchPacket>
{
    protected override void ProcessPacket(EntityWarningSwitchPacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        var pool = factory?.entityPool;
        if (pool == null || packet.EntityId <= 0 || packet.EntityId >= pool.Length ||
            pool[packet.EntityId].id != packet.EntityId)
        {
            return;
        }
        if (IsHost)
        {
            switch (packet.Enable)
            {
                case true when pool[packet.EntityId].warningId == 0:
                    GameMain.data.warningSystem.NewWarningData(factory.index, packet.EntityId, 0);
                    break;
                case false:
                    GameMain.data.warningSystem.RemoveWarningData(pool[packet.EntityId].warningId);
                    pool[packet.EntityId].warningId = 0;
                    break;
            }
        }
        else
        {
            //Use dummy warningId on client side
            pool[packet.EntityId].warningId = packet.Enable ? 1 : 0;
        }
    }
}
