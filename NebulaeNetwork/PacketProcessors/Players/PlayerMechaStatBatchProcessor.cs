#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Statistics;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;

internal class PlayerMechaStatBatchProcessor : PacketProcessor<PlayerMechaStatBatchPacket>
{
    protected override void ProcessPacket(PlayerMechaStatBatchPacket packet, NebulaeConnection conn)
    {
        if (IsClient || !packet.IsConsistent())
        {
            return;
        }

        var player = Players.Get(conn);
        if (player == null)
        {
            return;
        }
        // The factory is resolved once for the whole batch: every entry in it was reported by the same
        // player in the same tick, so they all resolve to the same factory.
        var factory = PlayerMechaStatProcessor.FindFactoryForPlayer(player);
        for (var i = 0; i < packet.ItemId.Length; i++)
        {
            if (packet.ItemCount[i] >= 0)
            {
                GameMain.mainPlayer.mecha.AddProductionStat(packet.ItemId[i], packet.ItemCount[i], factory);
            }
            else
            {
                GameMain.mainPlayer.mecha.AddConsumptionStat(packet.ItemId[i], -packet.ItemCount[i], factory);
            }
        }
    }
}
