using System;
using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Session;

internal enum JoinDataStage : byte { World, Factory }

[PacketId(0x9d004a6f696e0006UL)]
internal sealed class JoinDataTransferBegin
{
    [WireOrder(0)] public long Generation { get; set; }
    [WireOrder(1)] public long TransferId { get; set; }
    [WireOrder(2)] public byte Stage { get; set; }
    [WireOrder(3)] public int PlanetId { get; set; }
    [WireOrder(4)] public int[] MessageLengths { get; set; } = Array.Empty<int>();
}

[PacketId(0x9d004a6f696e0007UL)]
internal sealed class JoinDataTransferChunk
{
    [WireOrder(0)] public long Generation { get; set; }
    [WireOrder(1)] public long TransferId { get; set; }
    [WireOrder(2)] public int MessageIndex { get; set; }
    [WireOrder(3)] public int Offset { get; set; }
    [WireOrder(4)] public byte[] Data { get; set; } = Array.Empty<byte>();
}
