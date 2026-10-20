namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(12778441266292062221UL)]
internal class PlayerGiveItemPacket
{
    public PlayerGiveItemPacket() { }

    public PlayerGiveItemPacket(int itemId, int itemCount, int itemInc)
    {
        ItemId = itemId;
        ItemCount = itemCount;
        ItemInc = itemInc;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int ItemInc { get; set; }
}
