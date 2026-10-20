using NebulaeModel.DataStructures;

namespace NebulaeModel.Packets.Trash;

[NebulaeModel.Networking.PacketId(15676954901293883370UL)]
internal class TrashSystemNewPlayerTrashPacket
{
    public TrashSystemNewPlayerTrashPacket() { }

    public TrashSystemNewPlayerTrashPacket(ushort playerId, int trashId, in TrashObject trashObject, in TrashData trashData)
    {
        PlayerId = playerId;
        TrashId = trashId;
        NearStarId = trashData.nearStarId;
        Life = trashData.life;
        UVel = new Double3(trashData.uVel.x, trashData.uVel.y, trashData.uVel.z);
        ItemId = trashObject.item;
        Count = trashObject.count;
        Inc = trashObject.inc;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int TrashId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int NearStarId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Life { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public Double3 UVel { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int Count { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int Inc { get; set; }
}
