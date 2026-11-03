#region

using NebulaeModel.DataStructures;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(6502094611711185687UL)]
internal class DysonSwarmEditOrbitPacket
{
    public DysonSwarmEditOrbitPacket() { }

    public DysonSwarmEditOrbitPacket(int starIndex, int orbitId, float radius, Quaternion rotation)
    {
        StarIndex = starIndex;
        OrbitId = orbitId;
        Radius = radius;
        Rotation = new Float4(rotation);
    }

    public DysonSwarmEditOrbitPacket(int starIndex, int orbitId, Vector4 color)
    {
        StarIndex = starIndex;
        OrbitId = orbitId;
        Color = new Float4(color.x, color.y, color.z, color.w);
        Radius = -1;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int OrbitId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public float Radius { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public Float4 Rotation { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public Float4 Color { get; set; }
}
