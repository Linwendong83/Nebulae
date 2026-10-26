using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Factory;

// An ordered factory operation, separate from combat-stat replication. It must precede
// reconstruction/build operations that reuse the released slots.
[PacketId(14187665029480190210UL)]
internal sealed class BuildingDeathPacket
{
    [WireOrder(0)] public int PlanetId { get; set; }
    [WireOrder(1)] public long Sequence { get; set; }
    [WireOrder(2)] public int ObjectId { get; set; }
    [WireOrder(3)] public int ProtoId { get; set; }
    [WireOrder(4)] public long EntityGeneration { get; set; }
    [WireOrder(5)] public long TurretGeneration { get; set; }
    [WireOrder(6)] public byte[] Prebuild { get; set; }
    [WireOrder(7)] public int[] Connections { get; set; }
    [WireOrder(8)] public long PrebuildGeneration { get; set; }
    [WireOrder(9)] public string ExtraInfo { get; set; }
    [WireOrder(10)] public byte[] Wreckage { get; set; }
}
