#region

using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Belt;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;
internal class BeltUpdatePickupItemsProcessor : PacketProcessor<BeltUpdatePickupItemsPacket>
{
    protected override void ProcessPacket(BeltUpdatePickupItemsPacket packet, NebulaeConnection conn)
    {
        ApplyPickup(packet);
    }

    private static void ApplyPickup(BeltUpdatePickupItemsPacket packet)
    {
        var traffic = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.cargoTraffic;
        if (traffic == null || packet.BeltUpdates == null || packet.BeltUpdates.Length > 65536)
        {
            return;
        }
        BeltPickupBatch.Apply(packet.BeltUpdates, Pick, (update, count) =>
            Log.Warn($"Belt pickup missing {count} item{update.ItemId} on belt{update.BeltId}, planet{packet.PlanetId}"));

        int Pick(BeltUpdate update, int needed)
        {
            if (update.BeltId >= traffic.beltPool.Length) return 0;
            var belt = traffic.beltPool[update.BeltId];
            if (belt.id != update.BeltId) return 0;
            var path = traffic.GetCargoPath(belt.segPathId);
            if (path == null) return 0;
            var pivot = belt.segIndex + belt.segPivotOffset;
            for (var k = pivot; k <= belt.segIndex + belt.segLength - 1; k++)
            {
                var count = Take(k); if (count > 0) return count;
            }
            for (var k = pivot - 1; k >= belt.segIndex; k--)
            {
                var count = Take(k); if (count > 0) return count;
            }
            return 0;

            int Take(int k)
            {
                if (path.TryPickItem(k - 5, 12, update.ItemId, out var stack, out var inc) == 0) return 0;
                var split = BeltPickupBatch.SplitStack(stack, inc, needed);
                if (split.Count > 0 && !path.TryInsertItem(pivot, update.ItemId, (byte)split.Count, (byte)split.Inc))
                    Multiplayer.Session.Belts.RegiserbeltPutdownPacket(new BeltUpdatePutItemOnPacket(update.BeltId,
                        update.ItemId, (byte)split.Count, (byte)split.Inc, packet.PlanetId));
                return split.Taken;
            }
        }
    }
}
