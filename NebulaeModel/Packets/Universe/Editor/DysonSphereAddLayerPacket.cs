#region

using NebulaeModel.DataStructures;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(17359052262388421982UL)]
internal class DysonSphereAddLayerPacket
{
    public DysonSphereAddLayerPacket() { }

    public DysonSphereAddLayerPacket(int starIndex, int layerId, float orbitRadius, Quaternion orbitRotation,
        float orbitAngularSpeed)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        OrbitRadius = orbitRadius;
        OrbitRotation = new Float4(orbitRotation);
        OrbitAngularSpeed = orbitAngularSpeed;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public float OrbitRadius { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public Float4 OrbitRotation { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public float OrbitAngularSpeed { get; set; }
}
