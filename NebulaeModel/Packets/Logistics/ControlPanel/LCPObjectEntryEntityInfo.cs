namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(2801279363286631997UL)]
internal class LCPObjectEntryEntityInfo
{
    public LCPObjectEntryEntityInfo() { }

    public void Set(int index, int protoId, int id, string name)
    {
        Index = index;
        ProtoId = protoId;
        Id = id;
        Name = name;
    }

    public static readonly LCPObjectEntryEntityInfo Instance = new();

    [NebulaeModel.Networking.WireOrder(0)]
    public int Index { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Id { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public string Name { get; set; }
    [NebulaeModel.Networking.WireOrder(4)] public int Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(5)] public int AstroId { get; set; }
    [NebulaeModel.Networking.WireOrder(6)] public int ObjId { get; set; }
}
