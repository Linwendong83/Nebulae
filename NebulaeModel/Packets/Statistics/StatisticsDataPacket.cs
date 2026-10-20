namespace NebulaeModel.Packets.Statistics;

[NebulaeModel.Networking.PacketId(10101662728669896910UL)]
internal class StatisticsDataPacket
{
    public StatisticsDataPacket() { }

    public StatisticsDataPacket(byte[] statisticsBinaryData)
    {
        StatisticsBinaryData = statisticsBinaryData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte[] StatisticsBinaryData { get; set; }
}
