#region

using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

#endregion

/*
 * when a client arrives at a star he needs to sync the ILS storages as update events are sent only to corresponding stars
 * and also to sync the belt filters connected to the ILS
 */
namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class ILSArriveStarPlanetRequestProcessor : PacketProcessor<ILSArriveStarPlanetRequest>
{
    protected override void ProcessPacket(ILSArriveStarPlanetRequest packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        if (packet.StarId <= 0 || packet.GId < 0) return;
        var star = packet.StarId; var gid = packet.GId;
        Multiplayer.Session.Ships.QueueSnapshot(() => SendSnapshot(star, gid, conn));
    }

    internal static void SendSnapshot(int star, int gid, NebulaeConnection conn)
    {
        var stationGId = new List<int>();
        var revisions = new List<long>();
        var stationPId = new List<int>();
        var stationMaxShips = new List<int>();
        var storageLength = new List<int>();
        var slotLength = new List<int>();
        var arraySizeStorage = 0;
        var arraySizeSlot = 0;
        var offsetStorage = 0;
        var offsetSlot = 0;

        foreach (var stationComponent in GameMain.data.galacticTransport.stationPool)
        {
            if (stationComponent == null || GameMain.galaxy.PlanetById(stationComponent.planetId)?.star.id != star ||
                (gid > 0 && stationComponent.gid != gid))
            {
                continue;
            }
            stationGId.Add(stationComponent.gid);
            revisions.Add(Multiplayer.Session.Ships.Inventory.SnapshotRevision(stationComponent));
            stationPId.Add(stationComponent.planetId);
            stationMaxShips.Add(stationComponent.workShipDatas.Length);
            storageLength.Add(stationComponent.storage.Length);
            slotLength.Add(stationComponent.slots?.Length ?? 0);
        }

        var gStationPool = GameMain.data.galacticTransport.stationPool;

        arraySizeStorage += storageLength.Sum();
        arraySizeSlot += slotLength.Sum();

        var storageIdx = new int[arraySizeSlot];

        var itemId = new int[arraySizeStorage];
        var count = new int[arraySizeStorage];
        var inc = new int[arraySizeStorage];

        for (var i = 0; i < stationGId.Count; i++)
        {
            for (var j = 0; j < slotLength[i]; j++)
            {
                if (gStationPool[stationGId[i]].slots.Length > 0) // collectors dont have a slot for belts
                {
                    storageIdx[offsetSlot + j] = gStationPool[stationGId[i]].slots[j].storageIdx;
                }
            }
            offsetSlot += slotLength[i];

            for (var j = 0; j < storageLength[i]; j++)
            {
                itemId[offsetStorage + j] = gStationPool[stationGId[i]].storage[j].itemId;
                count[offsetStorage + j] = gStationPool[stationGId[i]].storage[j].count;
                inc[offsetStorage + j] = gStationPool[stationGId[i]].storage[j].inc;
            }
            offsetStorage += storageLength[i];
        }

        conn.SendPacket(new ILSArriveStarPlanetResponse(stationGId.ToArray(),
            stationPId.ToArray(),
            stationMaxShips.ToArray(),
            storageLength.ToArray(),
            storageIdx,
            slotLength.ToArray(),
            itemId,
            count,
            inc)
        { Revision = revisions.ToArray(), MissingGId = stationGId.Count == 0 ? gid : 0 });
    }
}
