using System;
using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Warning;

[PacketId(0x4750570000000002UL)]
internal class GreenPrebuildWarningState
{
    // Matches the native save format: zero is the global override, other ids
    // retain their individual settings even while the override is enabled.
    [WireOrder(0)] public int[] PlanetIds { get; set; } = Array.Empty<int>();
}
