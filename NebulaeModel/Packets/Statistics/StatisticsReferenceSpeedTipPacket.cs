namespace NebulaeModel.Packets.Statistics;

[NebulaeModel.Networking.PacketId(14718777504685141739UL)]
internal class StatisticsReferenceSpeedTipPacket
{
    public StatisticsReferenceSpeedTipPacket() { }

    public StatisticsReferenceSpeedTipPacket(int itemId, int astroFilter, int itemCycle, int productionProtoId, byte[] binaryData)
    {
        ItemId = itemId;
        AstroFilter = astroFilter;
        ItemCycle = itemCycle;
        ProductionProtoId = productionProtoId;
        BinaryData = binaryData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int AstroFilter { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int ItemCycle { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ProductionProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] BinaryData { get; set; }
}
