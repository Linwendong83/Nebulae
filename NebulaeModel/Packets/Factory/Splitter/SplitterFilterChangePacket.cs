namespace NebulaeModel.Packets.Factory.Splitter;

[NebulaeModel.Networking.PacketId(4233772629629823632UL)]
internal class SplitterFilterChangePacket
{
    public SplitterFilterChangePacket() { }

    public SplitterFilterChangePacket(int splitterIndex, int itemId, int planetId)
    {
        SplitterIndex = splitterIndex;
        ItemId = itemId;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int SplitterIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
