using NebulaeModel.DataStructures;
using UnityEngine;

namespace NebulaeModel.Packets.Factory.Foundation;

[NebulaeModel.Networking.PacketId(12011374562362328328UL)]
internal class VeinPosUpdatePacket
{
    public VeinPosUpdatePacket() { }

    public VeinPosUpdatePacket(int planetId, int veinId, Vector3 pos)
    {
        PlanetId = planetId;
        VeinId = veinId;
        Pos = new Float3(pos);
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int VeinId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public Float3 Pos { get; set; }
}
