#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeWorld;
using NebulaeWorld.Factory;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;

internal class BeltUpdatePutItemsProcessor : PacketProcessor<BeltUpdatePutItemsPacket>
{
    protected override void ProcessPacket(BeltUpdatePutItemsPacket packet, NebulaeConnection conn)
    {
        if (!packet.IsConsistent())
        {
            return;
        }
        for (var i = 0; i < packet.BeltId.Length; i++)
        {
            var single = new BeltUpdatePutItemOnPacket(packet.BeltId[i], packet.ItemId[i], packet.ItemCount[i],
                packet.ItemInc[i], packet.PlanetId);
            if (!BeltManager.TryPutItemOnBelt(single))
            {
                // The same retry queue the single-entry packet uses, so a belt that cannot take the item
                // yet is retried on the same schedule whether it arrived alone or in a batch.
                Multiplayer.Session.Belts.RegiserbeltPutdownPacket(single);
            }
        }
    }
}
