namespace NebulaeModel.Packets.Factory.Inserter;

[NebulaeModel.Networking.PacketId(9758676263724916794UL)]
internal class InserterItemUpdatePacket
{
    public InserterItemUpdatePacket() { }

    public InserterItemUpdatePacket(in InserterComponent inserter, int planetId)
    {
        InserterIndex = inserter.id;
        ItemId = inserter.itemId;
        ItemCount = inserter.itemCount;
        ItemInc = inserter.itemInc;
        StackCount = inserter.stackCount;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int InserterIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public short ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public short ItemInc { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int StackCount { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int PlanetId { get; set; }
}
