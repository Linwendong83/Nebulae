namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(17896601028161885748UL)]
internal class StationUIInitialSync
{
    public StationUIInitialSync() { }

    public StationUIInitialSync(
        int planetId,
        int stationId,
        int stationGId,
        double tripRangeDrones,
        double tripRangeShips,
        int deliveryDrones,
        int deliveryShips,
        double warperEnableDistance,
        bool warperNecessary,
        bool includeOrbitCollector,
        long energy,
        long energyPerTick,
        int pilerCount,
        int[] itemId,
        int[] itemCountMax,
        int[] itemCount,
        int[] itemInc,
        int[] localLogic,
        int[] remoteLogic,
        int[] remoteOrder
    )
    {
        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
        TripRangeDrones = tripRangeDrones;
        TripRangeShips = tripRangeShips;
        DeliveryDrones = deliveryDrones;
        DeliveryShips = deliveryShips;
        WarperEnableDistance = warperEnableDistance;
        WarperNecessary = warperNecessary;
        IncludeOrbitCollector = includeOrbitCollector;
        Energy = energy;
        EnergyPerTick = energyPerTick;
        PilerCount = pilerCount;

        ItemId = itemId;
        ItemCountMax = itemCountMax;
        ItemCount = itemCount;
        ItemInc = itemInc;
        LocalLogic = localLogic;
        RemoteLogic = remoteLogic;
        RemoteOrder = remoteOrder;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StationGId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StationId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public double TripRangeDrones { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public double TripRangeShips { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int DeliveryDrones { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int DeliveryShips { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public double WarperEnableDistance { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public bool WarperNecessary { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public bool IncludeOrbitCollector { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public long Energy { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public long EnergyPerTick { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public int PilerCount { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public int[] ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public int[] ItemCountMax { get; set; }
    [NebulaeModel.Networking.WireOrder(15)]
    public int[] ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(16)]
    public int[] ItemInc { get; set; }
    [NebulaeModel.Networking.WireOrder(17)]
    public int[] LocalLogic { get; set; }
    [NebulaeModel.Networking.WireOrder(18)]
    public int[] RemoteLogic { get; set; }
    [NebulaeModel.Networking.WireOrder(19)]
    public int[] RemoteOrder { get; set; }
}
