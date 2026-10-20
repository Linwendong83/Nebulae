namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(12181813588421567975UL)]
internal class ILSAddStationComponent
{
    public ILSAddStationComponent() { }

    public ILSAddStationComponent(int planetId, int stationId, int stationGId, int entityId, int maxShipCount)
    {
        StationGId = stationGId;
        PlanetId = planetId;
        StationId = stationId;
        EntityId = entityId;
        MaxShipCount = maxShipCount;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StationId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StationGId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int EntityId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int MaxShipCount { get; set; }
}
