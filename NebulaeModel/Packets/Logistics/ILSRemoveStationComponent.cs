namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(17830837571517866342UL)]
internal class ILSRemoveStationComponent
{
    public ILSRemoveStationComponent() { }

    public ILSRemoveStationComponent(int stationId, int planetId, int stationGId)
    {
        StationId = stationId;
        PlanetId = planetId;
        StationGId = stationGId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StationId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StationGId { get; set; }
}
