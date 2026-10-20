#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld.Logistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class ILSArriveStarPlanetResponseProcessor : PacketProcessor<ILSArriveStarPlanetResponse>
{
    protected override void ProcessPacket(ILSArriveStarPlanetResponse packet, NebulaeConnection conn)
    {
        if (IsHost || !packet.IsConsistent())
        {
            return;
        }

        var gStationPool = GameMain.data.galacticTransport.stationPool;
        var inventory = Multiplayer.Session.Ships.Inventory;
        if (packet.MissingGId > 0) inventory.RepairUnavailable(packet.MissingGId);

        var offsetStorage = 0;
        var offsetSlot = 0;
        for (var i = 0; i < packet.StationGId.Length; i++)
        {
            if (!inventory.AcceptSnapshot(packet.StationGId[i], packet.Revision[i]))
            {
                offsetSlot += packet.SlotLength[i]; offsetStorage += packet.StorageLength[i];
                continue;
            }
            if (packet.StationGId[i] >= gStationPool.Length || gStationPool[packet.StationGId[i]] == null)
            {
                ILSShipManager.CreateFakeStationComponent(packet.StationGId[i], packet.StationPId[i],
                    packet.StationMaxShips[i]);
                // Creation can replace the global pool when the capacity grows.
                gStationPool = GameMain.data.galacticTransport.stationPool;
            }

            var stationComponent = gStationPool[packet.StationGId[i]];
            if (!stationComponent.isCollector && stationComponent.slots?.Length != packet.SlotLength[i])
            {
                System.Array.Resize(ref stationComponent.slots, packet.SlotLength[i]);
            }
            if (stationComponent.storage.Length != packet.StorageLength[i])
            {
                System.Array.Resize(ref stationComponent.storage, packet.StorageLength[i]);
            }
            for (var j = 0; j < packet.SlotLength[i]; j++)
            {
                var index = offsetSlot + j;

                if (!stationComponent.isCollector)
                {
                    stationComponent.slots[j].storageIdx = packet.StorageIdx[index];
                }
            }
            offsetSlot += packet.SlotLength[i];
            for (var j = 0; j < packet.StorageLength[i]; j++)
            {
                var index = offsetStorage + j;

                stationComponent.storage[j].itemId = packet.ItemId[index];
                stationComponent.storage[j].count = packet.Count[index];
                stationComponent.storage[j].inc = packet.Inc[index];
            }
            offsetStorage += packet.StorageLength[i];
        }
    }
}
