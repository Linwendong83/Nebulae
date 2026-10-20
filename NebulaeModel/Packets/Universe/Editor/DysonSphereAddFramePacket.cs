namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(7326233326207988078UL)]
internal class DysonSphereAddFramePacket
{
    public DysonSphereAddFramePacket() { }

    public DysonSphereAddFramePacket(int starIndex, int layerId, int frameId, int protoId, int nodeAId, int nodeBId, bool euler)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        FrameId = frameId;
        ProtoId = protoId;
        NodeAId = nodeAId;
        NodeBId = nodeBId;
        Euler = euler;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int FrameId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int NodeAId { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int NodeBId { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public bool Euler { get; set; }
}
