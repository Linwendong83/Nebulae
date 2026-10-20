namespace NebulaeModel.Packets.Statistics;

[NebulaeModel.Networking.PacketId(14747278509029527261UL)]
internal class MilestoneUnlockPacket
{
    public MilestoneUnlockPacket() { }

    public MilestoneUnlockPacket(int id, long unlockTick, int patternId, long[] parameters)
    {
        Id = id;
        UnlockTick = unlockTick;
        PatternId = patternId;
        Parameters = parameters;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int Id { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long UnlockTick { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PatternId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long[] Parameters { get; set; }
}
