#region

using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(952863603058579721UL)]
internal class DysonSphereAddNodePacket
{
    public DysonSphereAddNodePacket() { }

    public DysonSphereAddNodePacket(int starIndex, int layerId, int nodeId, int nodeProtoId, Float3 position)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        NodeId = nodeId;
        NodeProtoId = nodeProtoId;
        Position = position;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int NodeId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int NodeProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public Float3 Position { get; set; }
}
