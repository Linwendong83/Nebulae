namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(2380446694923253763UL)]
internal class DispenserCourierPacket
{
    public DispenserCourierPacket() { }

    public DispenserCourierPacket(int planetId, int playerId, int dispenserId, int itemId, int itemCount)
    {
        PlanetId = planetId;
        PlayerId = playerId;
        DispenserId = dispenserId;
        ItemId = itemId;
        ItemCount = itemCount;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int DispenserId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int ItemCount { get; set; }
}
