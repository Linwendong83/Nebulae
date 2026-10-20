namespace NebulaeModel.Packets.Warning;

[NebulaeModel.Networking.PacketId(13115623332243964648UL)]
internal class WarningSignalPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int SignalCount { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] Signals { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] Counts { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Tick { get; set; }
}
