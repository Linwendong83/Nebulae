namespace NebulaeModel.Packets.Factory.Storage;

[NebulaeModel.Networking.PacketId(7301317473622425940UL)]
internal class StorageSyncResponsePacket
{
    public StorageSyncResponsePacket() { }

    public StorageSyncResponsePacket(int planetId, int storageIndex, byte[] storageComponent)
    {
        StorageIndex = storageIndex;
        StorageComponent = storageComponent;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte[] StorageComponent { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int StorageIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(3)] public long Sequence { get; set; }
}
