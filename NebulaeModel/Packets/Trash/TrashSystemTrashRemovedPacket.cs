namespace NebulaeModel.Packets.Trash;

[NebulaeModel.Networking.PacketId(4727597399177185355UL)]
internal class TrashSystemTrashRemovedPacket
{
    public TrashSystemTrashRemovedPacket() { }

    public TrashSystemTrashRemovedPacket(int trashId, int itemId)
    {
        TrashId = trashId;
        ItemId = itemId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TrashId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemId { get; set; }
}
