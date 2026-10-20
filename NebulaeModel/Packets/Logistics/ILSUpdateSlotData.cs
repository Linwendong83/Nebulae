namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(15500655856167476592UL)]
internal class ILSUpdateSlotData
{
    public ILSUpdateSlotData() { }

    public ILSUpdateSlotData(int planetId, int stationId, int stationGId, int index, int storageIdx)
    {
        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
        Index = index;
        StorageIdx = storageIdx;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StationId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StationGId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Index { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int StorageIdx { get; set; }
}
