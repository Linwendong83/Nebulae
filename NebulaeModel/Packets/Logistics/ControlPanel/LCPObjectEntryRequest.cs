namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(16617032405610180899UL)]
internal class LCPObjectEntryRequest
{
    public LCPObjectEntryRequest() { }

    public void Set(UIControlPanelObjectEntry controlPanelFilter, bool isInit = false)
    {
        // isInit: Request for entityData.protoId, name
        Index = controlPanelFilter.index;
        EntryType = (short)controlPanelFilter.entryType;
        AstroId = controlPanelFilter.target.astroId;
        // Use sign of ObjId to note the state of request
        ObjId = isInit ? -controlPanelFilter.target.objId : controlPanelFilter.target.objId;
    }

    public static readonly LCPObjectEntryRequest Instance = new();

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
