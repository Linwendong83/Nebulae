namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(12828995331737342466UL)]
internal class LCPStationRouteResultsPacket
{
    public LCPStationRouteResultsPacket() { }

    [NebulaeModel.Networking.WireOrder(0)]
    public int QueryPlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int QueryStationGid { get; set; }

    // Return routeResultCounts, resultPositions, routeResults
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] RouteResultCounts { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int[] ResultPositions { get; set; }

    // Members of StationRouteResult
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] TargetIds { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int[] PairStorageIndexMasks { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int[] PlanetIds { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public short[] RouteTypes { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int[] StationIds { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public double[] DistanceToInspectorStationSqrs { get; set; }
}
