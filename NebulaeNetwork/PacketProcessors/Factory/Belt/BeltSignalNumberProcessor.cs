#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;
internal class BeltSignalNumberProcessor : PacketProcessor<BeltSignalNumberPacket>
{
    protected override void ProcessPacket(BeltSignalNumberPacket packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
            if (factory == null || packet.EntityId >= factory.entityCursor) return;
            factory.cargoTraffic.SetBeltSignalNumber(packet.EntityId, packet.Number);
        }
    }
}
