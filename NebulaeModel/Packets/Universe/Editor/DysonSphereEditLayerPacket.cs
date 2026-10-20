#region

using NebulaeModel.DataStructures;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(13461755431024465875UL)]
internal class DysonSphereEditLayerPacket
{
    public DysonSphereEditLayerPacket() { }

    public DysonSphereEditLayerPacket(int starIndex, int layerId, Quaternion orbitRotation)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        OrbitRotation = new Float4(orbitRotation);
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public Float4 OrbitRotation { get; set; }
}
