#region

using NebulaeModel.DataStructures;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Factory.Inserter;

/// <summary>
/// One inserter target write. <see cref="Pick"/> selects the pick side (connection slot 1) instead
/// of the insert side (slot 0); both operations carry the same payload, so they share one wire id
/// and the flag keeps them apart at the receiver.
/// </summary>
[NebulaeModel.Networking.PacketId(12886054953100113458UL)]
internal class NewSetInserterTargetPacket
{
    public NewSetInserterTargetPacket() { }

    public NewSetInserterTargetPacket(bool pick, int objId, int otherObjId, int inserterId, int offset, Vector3 pointPos,
        int planetId)
    {
        Pick = pick;
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
    [NebulaeModel.Networking.WireOrder(6)]
    public bool Pick { get; set; }
}
