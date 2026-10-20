using System;
using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Session;

[PacketId(0x9d004a6f696e0001UL)]
internal class JoinSyncBegin
{
    [WireOrder(0)] public long Generation { get; set; }
    [WireOrder(1)] public NebulaeModel.DataStructures.PlayerTechBonuses TechBonuses { get; set; } = new();
}

[PacketId(0x9d004a6f696e0002UL)]
internal class JoinSyncBatch
{
    [WireOrder(0)] public long Generation { get; set; }
    [WireOrder(1)] public long FirstSequence { get; set; }
    [WireOrder(2)] public int[] Scopes { get; set; } = Array.Empty<int>();
    [WireOrder(3)] public byte[][] Packets { get; set; } = Array.Empty<byte[]>();
}

[PacketId(0x9d004a6f696e0003UL)]
internal class JoinSyncBarrier
{
    [WireOrder(0)] public long Generation { get; set; }
    [WireOrder(1)] public long Sequence { get; set; }
}

[PacketId(0x9d004a6f696e0004UL)]
internal class JoinSyncAck
{
    [WireOrder(0)] public long Generation { get; set; }
    [WireOrder(1)] public long Sequence { get; set; }
}

[PacketId(0x9d004a6f696e0005UL)]
internal class JoinSyncProgress
{
    [WireOrder(0)] public long Generation { get; set; }
    [WireOrder(1)] public long Progress { get; set; }
}
