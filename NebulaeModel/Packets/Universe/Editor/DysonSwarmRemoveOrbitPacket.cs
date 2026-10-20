namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(16764192070842909531UL)]
internal class DysonSwarmRemoveOrbitPacket
{
    public DysonSwarmRemoveOrbitPacket() { }

    public DysonSwarmRemoveOrbitPacket(int starIndex, int orbitId, SwarmRemoveOrbitEvent removeEvent)
    {
        StarIndex = starIndex;
        OrbitId = orbitId;
        Event = removeEvent;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int OrbitId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public SwarmRemoveOrbitEvent Event { get; set; }
}

internal enum SwarmRemoveOrbitEvent
{
    Remove,
    Disable,
    Enable,
    RemoveSails
}
