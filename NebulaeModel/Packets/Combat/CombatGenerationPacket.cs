namespace NebulaeModel.Packets.Combat;

[NebulaeModel.Networking.PacketId(10585767148242858038UL)]
internal class CombatGenerationPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public byte[] Data { get; set; }
}
