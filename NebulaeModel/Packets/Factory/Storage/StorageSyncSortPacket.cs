namespace NebulaeModel.Packets.Factory.Storage;

[NebulaeModel.Networking.PacketId(6617457757671706851UL)]
internal class StorageSyncSortPacket
{
    public StorageSyncSortPacket() { }

    public StorageSyncSortPacket(int storageIndex, int planetId)
    {
        StorageIndex = storageIndex;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StorageIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
}
