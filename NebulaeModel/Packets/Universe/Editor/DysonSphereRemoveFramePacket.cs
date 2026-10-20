namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(10561113092435086735UL)]
internal class DysonSphereRemoveFramePacket
{
    public DysonSphereRemoveFramePacket() { }

    public DysonSphereRemoveFramePacket(int starIndex, int layerId, int frameId)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        FrameId = frameId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int FrameId { get; set; }
}
