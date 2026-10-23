namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(16617032405610180899UL)]
internal class LCPObjectEntryRequest
{
    public LCPObjectEntryRequest() { }

    [NebulaeModel.Networking.WireOrder(0)]
    public int Index { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public short EntryType { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int AstroId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ObjId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)] public int Generation { get; set; }
}
