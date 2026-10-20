#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerGiveItemProcessor : PacketProcessor<PlayerGiveItemPacket>
{
    protected override void ProcessPacket(PlayerGiveItemPacket packet, NebulaeConnection conn)
    {
        GameMain.mainPlayer.TryAddItemToPackage(packet.ItemId, packet.ItemCount, packet.ItemInc, true);
    }
}
