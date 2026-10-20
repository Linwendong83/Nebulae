#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;
internal class BeltSignalIconProcessor : PacketProcessor<BeltSignalIconPacket>
{
    protected override void ProcessPacket(BeltSignalIconPacket packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
            if (factory == null || packet.EntityId >= factory.entityCursor) return;
            factory.cargoTraffic.SetBeltSignalIcon(packet.EntityId, packet.SignalId);
        }
    }
}
