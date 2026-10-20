namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(13735535666550722592UL)]
internal class PlayerSandCount
{
    public PlayerSandCount() { }

    public PlayerSandCount(long sandCount)
    {
        SandCount = sandCount;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public long SandCount { get; set; }
}
