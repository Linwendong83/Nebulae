namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(14816205950691906437UL)]
internal class StationUI
{
    internal enum EUISettings
    {
        None,
        MaxChargePower,
        MaxTripDrones,
        MaxTripVessel,
        MinDeliverDrone,
        MinDeliverVessel,
        WarpDistance,
        WarperNeeded,
        IncludeCollectors,
        SetDroneCount,
        SetShipCount,
        SetWarperCount,
        PilerCount,
        MaxMiningSpeed,
        DroneAutoReplenish,
        ShipAutoReplenish,
        RemoteGroupMask,
        RoutePriority
    }

    public StationUI() { }

    public StationUI(int planetId, int stationId, int stationGId, EUISettings settingIndex, double value,
        bool warperShouldTakeFromStorage = false)
    {
        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
        SettingIndex = settingIndex;
        SettingValue = value;
        WarperShouldTakeFromStorage = warperShouldTakeFromStorage;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StationId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StationGId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public EUISettings SettingIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public double SettingValue { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public bool WarperShouldTakeFromStorage { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public bool ShouldRefund { get; set; }
}
