#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class DispenserCourierProcessor : PacketProcessor<DispenserCourierPacket>
{
    protected override void ProcessPacket(DispenserCourierPacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.mainPlayer.factory;
        var pool = factory?.transport.dispenserPool;
        if (GameMain.mainPlayer.planetId != packet.PlanetId || pool == null)
        {
            return;
        }
        if (packet.DispenserId <= 0 || packet.DispenserId >= pool.Length || pool[packet.DispenserId] == null)
        {
            return;
        }
        var dispenser = pool[packet.DispenserId];
        Multiplayer.Session.Couriers.AddCourier(packet.PlayerId, factory.entityPool[dispenser.entityId].pos, packet.ItemId,
            packet.ItemCount);
        dispenser.pulseSignal = 2;
    }
}
