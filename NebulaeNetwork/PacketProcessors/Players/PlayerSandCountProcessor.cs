#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeModel.Utils;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerSandCountProcessor : PacketProcessor<PlayerSandCount>
{
    protected override void ProcessPacket(PlayerSandCount packet, NebulaeConnection conn)
    {
        if (IsHost) return;

        var player = GameMain.mainPlayer;
        var originalSandCount = player.sandCount;

        player.SetHiddenProperty(nameof(Player.sandCount), player.sandCount + packet.SandCount);
        if (player.sandCount != originalSandCount)
        {
            UIRoot.instance.uiGame.OnSandCountChanged(player.sandCount, player.sandCount - originalSandCount, (ESandSource)0);
        }
    }
}
