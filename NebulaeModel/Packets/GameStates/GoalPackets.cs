namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(12394647199582286971UL)]
internal class GoalSnapshotPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public long Version { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] Data { get; set; }
}

[NebulaeModel.Networking.PacketId(3145028198052277003UL)]
internal class GoalObservationPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int GoalId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long Sequence { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long Value { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long Target { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool Complete { get; set; }
}
