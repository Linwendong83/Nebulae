namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(4758439478373596397UL)]
internal class StationUIInitialSyncRequest
{
    public StationUIInitialSyncRequest() { }

    public StationUIInitialSyncRequest(int planetId, int stationId, int stationGId)
    {
        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StationId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StationGId { get; set; }
}
