using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Logistics.ControlPanel;

[PacketId(0x4e4542554c410101UL)]
internal sealed class FacilityDirectoryRequest
{
    [WireOrder(0)] public bool Subscribe { get; set; }
}

[PacketId(0x4e4542554c410102UL)]
internal sealed class FacilityDirectoryPacket
{
    [WireOrder(0)] public long Revision { get; set; }
    [WireOrder(1)] public byte[] Data { get; set; }
}

[PacketId(0x4e4542554c410103UL)]
internal sealed class FacilityRowsSubscription
{
    [WireOrder(0)] public int Generation { get; set; }
    [WireOrder(1)] public int[] Indices { get; set; }
    [WireOrder(2)] public int[] Planets { get; set; }
    [WireOrder(3)] public int[] Entities { get; set; }
    [WireOrder(4)] public short[] Types { get; set; }
}
