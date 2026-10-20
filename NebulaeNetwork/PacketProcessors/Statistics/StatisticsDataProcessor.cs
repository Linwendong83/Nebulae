
#region
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Statistics;
internal class StatisticsDataProcessor : PacketProcessor<StatisticsDataPacket>
{
    protected override void ProcessPacket(StatisticsDataPacket packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        using var reader = new BinaryUtils.Reader(packet.StatisticsBinaryData);
        Multiplayer.Session.Statistics.ImportAllData(reader.BinaryReader);
    }
}
