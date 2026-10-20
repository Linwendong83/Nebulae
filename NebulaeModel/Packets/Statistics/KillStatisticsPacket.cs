namespace NebulaeModel.Packets.Statistics;

[NebulaeModel.Networking.PacketId(8595753333791015274UL)]
internal class KillStatisticsPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public bool Snapshot { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long FromTick { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long ToTick { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public byte[] Data { get; set; }
}

[NebulaeModel.Networking.PacketId(6897649429488903551UL)]
internal class KillStatisticsRequest
{
    [NebulaeModel.Networking.WireOrder(0)]
    public bool Subscribe { get; set; }
}
