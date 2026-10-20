namespace NebulaeModel.Packets.Warning;

[NebulaeModel.Networking.PacketId(16939767011914327332UL)]
internal class WarningDataPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int ActiveWarningCount { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Tick { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public byte[] BinaryData { get; set; }
}
