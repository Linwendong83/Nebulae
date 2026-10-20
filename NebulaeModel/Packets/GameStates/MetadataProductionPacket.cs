namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(5680317432274652614UL)]
internal class MetadataProductionPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public string WorldId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long ClusterKey { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long Sequence { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int[] Earned { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] WorldPeak { get; set; }
}

[NebulaeModel.Networking.PacketId(5274430293799114163UL)]
internal class MetadataReceiptPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public string WorldId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long Sequence { get; set; }
}
