using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Universe;

[PacketId(0xa51a88a479bd7ac1UL)]
internal sealed class HivePreviewRequest
{
    // Zero closes this connection's previous star preview.
    [WireOrder(0)] public int StarId { get; set; }
}
