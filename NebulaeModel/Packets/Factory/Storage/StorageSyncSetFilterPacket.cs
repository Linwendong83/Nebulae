namespace NebulaeModel.Packets.Factory.Storage;

[NebulaeModel.Networking.PacketId(3073547654533707139UL)]
internal class StorageSyncSetFilterPacket
{
    public StorageSyncSetFilterPacket() { }

    public StorageSyncSetFilterPacket(int storageIndex, int planetId, int gridIndex, int filterId, EStorageType storageType)
    {
        StorageIndex = storageIndex;
        PlanetId = planetId;
        GridIndex = gridIndex;
        FilterId = filterId;
        StorageType = storageType;
    }
    [NebulaeModel.Networking.WireOrder(0)]
    public int StorageIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int GridIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int FilterId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public EStorageType StorageType { get; set; }
}
