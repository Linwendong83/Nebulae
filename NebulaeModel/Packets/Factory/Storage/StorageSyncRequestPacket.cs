namespace NebulaeModel.Packets.Factory.Storage;

[NebulaeModel.Networking.PacketId(13725489430344155224UL)]
internal class StorageSyncRequestPacket
{
    public StorageSyncRequestPacket() { }

    public StorageSyncRequestPacket(int planetId, int storageId)
    {
        PlanetId = planetId;
        StorageId = storageId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StorageId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)] public long Sequence { get; set; }
}
