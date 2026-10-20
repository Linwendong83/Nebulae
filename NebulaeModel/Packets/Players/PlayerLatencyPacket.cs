namespace NebulaeModel.Packets.Players;

/// <summary>Small RTT probe and host-owned latency snapshot for the player overlay.</summary>
[NebulaeModel.Networking.PacketId(15988617662710102919UL)]
internal class PlayerLatencyPacket
{
    public PlayerLatencyPacket() { }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte Kind { get; set; } // 0 probe, 1 reply, 2 snapshot
    [NebulaeModel.Networking.WireOrder(1)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long SentTicks { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public ushort[] PlayerIds { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] Milliseconds { get; set; }
}
