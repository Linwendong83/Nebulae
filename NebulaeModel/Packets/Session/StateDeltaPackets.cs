using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Session;

/// <summary>Connection-local encoding of one complete state packet.</summary>
[PacketId(0xCFB31C1E85724601UL)]
internal sealed class StateDeltaPacket
{
    [WireOrder(0)] public ulong OriginalPacketId { get; set; }
    [WireOrder(1)] public byte[] Key { get; set; }
    [WireOrder(2)] public ulong BaselineId { get; set; }
    [WireOrder(3)] public long BaseRevision { get; set; }
    [WireOrder(4)] public long Revision { get; set; }
    [WireOrder(5)] public byte[] Data { get; set; }
}

/// <summary>Request a full state on this connection after losing a field baseline.</summary>
[PacketId(0xCFB31C1E85724602UL)]
internal sealed class StateResyncPacket
{
    [WireOrder(0)] public ulong OriginalPacketId { get; set; }
    [WireOrder(1)] public byte[] Key { get; set; }
}
