namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(8045828725233715174UL)]
internal class DysonSphereRemoveNodePacket
{
    public DysonSphereRemoveNodePacket() { }

    public DysonSphereRemoveNodePacket(int starIndex, int layerId, int nodeId)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        NodeId = nodeId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int NodeId { get; set; }
}
