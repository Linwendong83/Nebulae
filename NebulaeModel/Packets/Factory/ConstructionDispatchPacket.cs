using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Factory;

[PacketId(14187665029480190101UL)]
internal sealed class ConstructionLaunchOfferPacket
{
    [WireOrder(0)] public int PlanetId { get; set; }
    [WireOrder(1)] public long Sequence { get; set; }
    [WireOrder(2)] public int[] Targets { get; set; }
    [WireOrder(3)] public long[] Instances { get; set; }
    [WireOrder(4)] public int Priority { get; set; }
}

[PacketId(14187665029480190102UL)]
internal sealed class ConstructionLaunchGrantPacket : BuildDroneLaunchPacket { }

[PacketId(14187665029480190103UL)]
internal sealed class BaseDroneStatePacket
{
    [WireOrder(0)] public int PlanetId { get; set; }
    [WireOrder(1)] public long Tick { get; set; }
    [WireOrder(2)] public byte[] State { get; set; }
    [WireOrder(3)] public bool Full { get; set; }
    [WireOrder(4)] public long Sequence { get; set; }
}

[PacketId(14187665029480190105UL)]
internal sealed class DroneFlightSnapshotRequestPacket
{
    [WireOrder(0)] public int PlanetId { get; set; }
}

[PacketId(14187665029480190104UL)]
internal sealed class ConstructionStoragePacket
{
    [WireOrder(0)] public int PlanetId { get; set; }
    [WireOrder(1)] public int StorageId { get; set; }
    [WireOrder(2)] public int EntityId { get; set; }
    [WireOrder(3)] public long Generation { get; set; }
    [WireOrder(4)] public long Tick { get; set; }
    [WireOrder(5)] public byte[] State { get; set; }
}
