namespace NebulaeModel.Packets.Factory.Storage;

[NebulaeModel.Networking.PacketId(6233977371929530062UL)]
internal class StorageSyncRealtimeChangePacket
{
    public StorageSyncRealtimeChangePacket() { }

    public StorageSyncRealtimeChangePacket(int storageIndex, StorageSyncRealtimeChangeEvent storageEvent, int itemId, int count,
        int inc)
    {
        StorageEvent = storageEvent;
        ItemId = itemId;
        Count = count;
        StorageIndex = storageIndex;
        Inc = inc;
        PlanetId = GameMain.localPlanet?.id ?? -1;
    }

    public StorageSyncRealtimeChangePacket(int storageIndex, StorageSyncRealtimeChangeEvent storageEvent, int itemId, int count,
        bool useBan, int planetId)
    {
        StorageEvent = storageEvent;
        ItemId = itemId;
        Count = count;
        UseBan = useBan;
        StorageIndex = storageIndex;
        PlanetId = planetId;
    }

    public StorageSyncRealtimeChangePacket(int storageIndex, StorageSyncRealtimeChangeEvent storageEvent, int gridIndex,
        int itemId, int count, int inc)
    {
        StorageEvent = storageEvent;
        ItemId = itemId;
        Count = count;
        Length = gridIndex;
        StorageIndex = storageIndex;
        Inc = inc;
        PlanetId = GameMain.localPlanet?.id ?? -1;
    }

    public StorageSyncRealtimeChangePacket(int storageIndex, StorageSyncRealtimeChangeEvent storageEvent, int itemId, int count,
        int startIndex, int length, int inc)
    {
        StorageEvent = storageEvent;
        ItemId = itemId;
        Count = count;
        StartIndex = startIndex;
        Length = length;
        StorageIndex = storageIndex;
        Inc = inc;
        PlanetId = GameMain.localPlanet?.id ?? -1;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public StorageSyncRealtimeChangeEvent StorageEvent { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Count { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public bool UseBan { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int StartIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int Length { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int[] Needs { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int StorageIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int Inc { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public int PlanetId { get; set; }
}

internal enum StorageSyncRealtimeChangeEvent
{
    AddItem1 = 1,
    AddItem2 = 2,
    AddItemStacked = 3,
    AddItemFiltered = 4,
    TakeItem = 5,
    TakeItemFromGrid = 6,
    TakeHeadItems = 7,
    TakeTailItems1 = 8,
    TakeTailItems2 = 9
}
