using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Logistics;

/// <summary>Actual native shipping deltas, applied once without rerunning item selection.</summary>
[PacketId(12274399218092522748UL)]
internal class ILSStorageCommitPacket
{
    [WireOrder(0)] public int GId { get; set; }
    [WireOrder(1)] public int PlanetId { get; set; }
    [WireOrder(2)] public long PreviousRevision { get; set; }
    [WireOrder(3)] public long Revision { get; set; }
    [WireOrder(4)] public int[] Index { get; set; }
    [WireOrder(5)] public int[] ItemId { get; set; }
    [WireOrder(6)] public long[] CountDelta { get; set; }
    [WireOrder(7)] public long[] IncDelta { get; set; }
}
