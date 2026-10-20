namespace NebulaeModel.Packets.Factory.Inserter;

[NebulaeModel.Networking.PacketId(3176080680567579587UL)]
internal class InserterFilterUpdatePacket
{
    public InserterFilterUpdatePacket() { }

    public InserterFilterUpdatePacket(int inserterIndex, int itemId, int planetId)
    {
        InserterIndex = inserterIndex;
        ItemId = itemId;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int InserterIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
