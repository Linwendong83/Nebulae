using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Factory;

[PacketId(14187665029480190201UL)]
internal sealed class RepairOfferPacket
{
    [WireOrder(0)] public int PlanetId { get; set; }
    [WireOrder(1)] public int EntityId { get; set; }
    [WireOrder(2)] public long Generation { get; set; }
    [WireOrder(3)] public long Sequence { get; set; }
    [WireOrder(4)] public int Idle { get; set; }
    [WireOrder(5)] public int Priority { get; set; }
    [WireOrder(6)] public int ResumeDrone { get; set; }
    [WireOrder(7)] public int Remaining { get; set; }
    [WireOrder(8)] public float X { get; set; }
    [WireOrder(9)] public float Y { get; set; }
    [WireOrder(10)] public float Z { get; set; }
}

internal enum RepairControl : byte { Grant, Started, Cancel, Heartbeat }

[PacketId(14187665029480190202UL)]
internal sealed class RepairControlPacket
{
    [WireOrder(0)] public RepairControl Message { get; set; }
    [WireOrder(1)] public long Flight { get; set; }
    [WireOrder(2)] public int PlanetId { get; set; }
    [WireOrder(3)] public int EntityId { get; set; }
    [WireOrder(4)] public long Generation { get; set; }
    [WireOrder(5)] public int Priority { get; set; }
    [WireOrder(6)] public int ResumeDrone { get; set; }
    [WireOrder(7)] public long OfferSequence { get; set; }
    [WireOrder(8)] public byte[] FlightState { get; set; }
}

[PacketId(14187665029480190203UL)]
internal sealed class RepairDroneStatePacket
{
    [WireOrder(0)] public long Flight { get; set; }
    [WireOrder(1)] public ushort PlayerId { get; set; }
    [WireOrder(2)] public int PlanetId { get; set; }
    [WireOrder(3)] public long Tick { get; set; }
    [WireOrder(4)] public int Stage { get; set; }
    [WireOrder(5)] public float X { get; set; }
    [WireOrder(6)] public float Y { get; set; }
    [WireOrder(7)] public float Z { get; set; }
    [WireOrder(8)] public float Qx { get; set; }
    [WireOrder(9)] public float Qy { get; set; }
    [WireOrder(10)] public float Qz { get; set; }
    [WireOrder(11)] public float Qw { get; set; }
    [WireOrder(12)] public int EntityId { get; set; }
    [WireOrder(13)] public long Generation { get; set; }
    [WireOrder(14)] public byte[] FlightState { get; set; }
    [WireOrder(15)] public long Sequence { get; set; }
}
