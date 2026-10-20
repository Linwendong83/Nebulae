namespace NebulaeModel.Packets.Statistics;

[NebulaeModel.Networking.PacketId(248734985639742908UL)]
internal class StatisticsExtraDataPacket
{
    public StatisticsExtraDataPacket() { }

    public StatisticsExtraDataPacket(int factoryCount, byte[] binaryData)
    {
        FactoryCount = factoryCount;
        BinaryData = binaryData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int FactoryCount { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] BinaryData { get; set; }
}
