namespace NebulaeModel.Packets.Combat;

[NebulaeModel.Networking.PacketId(15533830833978318678UL)]
internal class BattleVisualPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int StarId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long Sequence { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long Tick { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public bool Reset { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] Data { get; set; }
}
