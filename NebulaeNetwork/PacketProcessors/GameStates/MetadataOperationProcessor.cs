using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.GameStates;
internal class MetadataOperationProcessor : PacketProcessor<MetadataOperationPacket>
{
    protected override void ProcessPacket(MetadataOperationPacket packet, NebulaeConnection conn)
    {
        if (!Multiplayer.Session.IsGameLoaded || packet.WorldId != SaveManager.WorldId) return;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player == null || packet.Message is MetadataMessage.Quote or MetadataMessage.Result) return;
            Multiplayer.Session.PropertyTransactions.HandleServer(packet, (PlayerData)player.Data, conn.SendPacket);
        }
        else Multiplayer.Session.PropertyTransactions.Receive(packet);
    }
}
internal class PropertyHistoryProcessor : PacketProcessor<PropertyHistoryPacket>
{
    private long sequence;
    protected override void ProcessPacket(PropertyHistoryPacket packet, NebulaeConnection conn)
    {
        if (IsHost || packet.Sequence <= sequence || packet.Consumption?.Length != 6) return;
        sequence = packet.Sequence;
        for (var i = 0; i < 6; i++)
            GameMain.history.SetPropertyItemConsumption(PropertySystem.matrixIds[i], packet.Consumption[i]);
        GameMain.history.hasUsedPropertyBanAchievement = packet.BanAchievement;
    }
}
