namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(549036074337489036UL)]
internal class ILSRequestgStationPoolSync
{
    // Zero requests the initial baseline; a positive gid repairs only that station.
    [NebulaeModel.Networking.WireOrder(0)]
    public int GId { get; set; }
}
