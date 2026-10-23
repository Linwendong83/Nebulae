namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(12298623713523655411UL)]
internal sealed class PrebuildInstancePacket
{
    [NebulaeModel.Networking.WireOrder(0)] public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)] public int PrebuildId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)] public long Generation { get; set; }
}
