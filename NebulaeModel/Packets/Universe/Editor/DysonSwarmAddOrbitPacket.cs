#region

using NebulaeModel.DataStructures;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(15195761332095146644UL)]
internal class DysonSwarmAddOrbitPacket
{
    public DysonSwarmAddOrbitPacket() { }

    public DysonSwarmAddOrbitPacket(int starIndex, int orbitId, float radius, Quaternion rotation)
    {
        StarIndex = starIndex;
        OrbitId = orbitId;
        Radius = radius;
        Rotation = new Float4(rotation);
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int OrbitId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public float Radius { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public Float4 Rotation { get; set; }
}
