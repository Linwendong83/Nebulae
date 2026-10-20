namespace NebulaeModel.Packets.Factory.Belt;

[NebulaeModel.Networking.PacketId(15398919176629478286UL)]
internal class BeltUpdatePutItemOnPacket
{
    public BeltUpdatePutItemOnPacket() { }

    public BeltUpdatePutItemOnPacket(int beltId, int itemId, byte itemCount, byte itemInc, int planetId)
    {
        BeltId = beltId;
        ItemId = itemId;
        ItemCount = itemCount;
        ItemInc = itemInc;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int BeltId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public byte ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public byte ItemInc { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int PlanetId { get; set; }
}
