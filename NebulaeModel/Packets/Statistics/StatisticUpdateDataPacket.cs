namespace NebulaeModel.Packets.Statistics;

[NebulaeModel.Networking.PacketId(15378723773360064224UL)]
internal class StatisticUpdateDataPacket
{
    public StatisticUpdateDataPacket() { }

    public StatisticUpdateDataPacket(byte[] statisticsBinaryData)
    {
        StatisticsBinaryData = statisticsBinaryData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte[] StatisticsBinaryData { get; set; }
}
