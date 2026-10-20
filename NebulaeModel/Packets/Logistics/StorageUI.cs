namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(10726666473055371764UL)]
internal class StorageUI
{
    public StorageUI() { }

    public StorageUI(int planetId, int stationId, int stationGId, int storageIdx, int itemId, int itemCountMax,
        ELogisticStorage localLogic, ELogisticStorage remoteLogic)
    {
        ItemCount = -1; //Indicate it is SetStationStorage()

        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
        StorageIdx = storageIdx;
        ItemId = itemId;
        ItemCountMax = itemCountMax;
        LocalLogic = localLogic;
        RemoteLogic = remoteLogic;
    }

    public StorageUI(int planetId, int stationId, int stationGId, int storageIdx, int itemCount, int itemInc)
    {
        ShouldRefund = false;

        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
        StorageIdx = storageIdx;
        ItemCount = itemCount;
        ItemInc = itemInc;
    }

    public StorageUI(int planetId, int stationId, int stationGId, int storageIdx, byte keepMode)
    {
        ItemCount = -2; //Indicate it is other settings update

        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
        StorageIdx = storageIdx;
        KeepMode = keepMode;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StationId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StationGId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int StorageIdx { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int ItemCountMax { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public ELogisticStorage LocalLogic { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public ELogisticStorage RemoteLogic { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public int ItemInc { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public bool ShouldRefund { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public byte KeepMode { get; set; }
}
