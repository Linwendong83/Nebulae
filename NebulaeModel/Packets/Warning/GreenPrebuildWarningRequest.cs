using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Warning;

[PacketId(0x4750570000000001UL)]
internal class GreenPrebuildWarningRequest
{
    // Zero selects the all-planets switch; positive ids select one planet.
    [WireOrder(0)] public int PlanetId { get; set; }
    [WireOrder(1)] public bool Enable { get; set; }
}
