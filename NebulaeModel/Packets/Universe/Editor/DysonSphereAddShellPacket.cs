#region

using System.Collections.Generic;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(5632155376726484605UL)]
internal class DysonSphereAddShellPacket
{
    public DysonSphereAddShellPacket() { }

    public DysonSphereAddShellPacket(int starIndex, int layerId, int shellId, int protoId, List<int> nodeIds)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        ShellId = shellId;
        ProtoId = protoId;
        NodeIds = nodeIds.ToArray();
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int ShellId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] NodeIds { get; set; }
}
