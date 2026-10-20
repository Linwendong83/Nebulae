#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory;
internal class ExtraInfoUpdateProcessor : PacketProcessor<ExtraInfoUpdatePacket>
{
    protected override void ProcessPacket(ExtraInfoUpdatePacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null)
        {
            return;
        }

        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            if (packet.ObjId < 0)
            {
                var prebuildId = -packet.ObjId;
                if (prebuildId > 0 && prebuildId < factory.prebuildCursor)
                {
                    factory.WriteExtraInfoOnPrebuild(prebuildId, packet.Info);
                }
            }
            else
            {
                var entityId = packet.ObjId;
                if (entityId > 0 && entityId < factory.entityCursor)
                {
                    factory.WriteExtraInfoOnEntity(entityId, packet.Info);
                }
            }
        }
    }
}
