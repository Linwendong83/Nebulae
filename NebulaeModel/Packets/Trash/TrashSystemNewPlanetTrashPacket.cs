using NebulaeModel.DataStructures;

namespace NebulaeModel.Packets.Trash;

[NebulaeModel.Networking.PacketId(11658639457633652931UL)]
internal class TrashSystemNewPlanetTrashPacket
{
    public TrashSystemNewPlanetTrashPacket() { }

    public TrashSystemNewPlanetTrashPacket(int trashId, in TrashObject trashObject, in TrashData trashData)
    {
        TrashId = trashId;
        PlanetId = trashData.nearPlanetId;
        Pos = trashData.lPos.ToFloat3();
        Life = trashData.life;
        ItemId = trashObject.item;
        Count = trashObject.count;
        Inc = trashObject.inc;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TrashId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public Float3 Pos { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Life { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int Count { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int Inc { get; set; }
}
