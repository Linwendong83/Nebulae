namespace NebulaeModel.Packets.Factory.Splitter;

[NebulaeModel.Networking.PacketId(16358115368163032198UL)]
internal class SplitterPriorityChangePacket
{
    public SplitterPriorityChangePacket() { }

    public SplitterPriorityChangePacket(int splitterIndex, int slot, bool isPriority, int filter, int planetId)
    {
        SplitterIndex = splitterIndex;
        Slot = slot;
        IsPriority = isPriority;
        Filter = filter;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int SplitterIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Slot { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool IsPriority { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Filter { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int PlanetId { get; set; }
}
