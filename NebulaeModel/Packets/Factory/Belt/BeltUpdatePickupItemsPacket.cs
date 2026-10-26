#region

using NebulaeModel.Contracts;

#endregion

namespace NebulaeModel.Packets.Factory.Belt;

[NebulaeModel.Networking.PacketId(7244088077961496973UL)]
internal class BeltUpdatePickupItemsPacket
{
    public BeltUpdatePickupItemsPacket() { }

    public BeltUpdatePickupItemsPacket(BeltUpdate[] beltUpdates, int planetId)
    {
        BeltUpdates = beltUpdates;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public BeltUpdate[] BeltUpdates { get; set; }
}
internal struct BeltUpdate : INetSerializable
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Count { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int BeltId { get; set; }

    public BeltUpdate(int itemId, int count, int beltId)
    {
        ItemId = itemId;
        Count = count;
        BeltId = beltId;
    }

    public void Serialize(INetDataWriter writer)
    {
        writer.Put(ItemId);
        writer.Put(Count);
        writer.Put(BeltId);
    }

    public void Deserialize(INetDataReader reader)
    {
        ItemId = reader.GetInt();
        Count = reader.GetInt();
        BeltId = reader.GetInt();
    }
}
