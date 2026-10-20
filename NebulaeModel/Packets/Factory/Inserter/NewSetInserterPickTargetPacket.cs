#region

using NebulaeModel.DataStructures;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Factory.Inserter;

[NebulaeModel.Networking.PacketId(4167866442187407074UL)]
internal class NewSetInserterPickTargetPacket
{
    public NewSetInserterPickTargetPacket() { }

    public NewSetInserterPickTargetPacket(int objId, int otherObjId, int inserterId, int offset, Vector3 pointPos, int planetId)
    {
        ObjId = objId;
        OtherObjId = otherObjId;
        InserterId = inserterId;
        Offset = offset;
        PointPos = new Float3(pointPos - pointPos.normalized * 0.15f);
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int ObjId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int OtherObjId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int InserterId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Offset { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public Float3 PointPos { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int PlanetId { get; set; }
}
