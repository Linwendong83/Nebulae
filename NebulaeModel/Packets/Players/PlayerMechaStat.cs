namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(14648819470476481591UL)]
internal class PlayerMechaStat
{
    public PlayerMechaStat() { }

    public PlayerMechaStat(int itemId, int itemCount)
    {
        ItemId = itemId;
        ItemCount = itemCount;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemCount { get; set; }
}
