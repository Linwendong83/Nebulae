namespace NebulaeModel.Packets.Factory.Storage;

[NebulaeModel.Networking.PacketId(6527987759789534923UL)]
internal class StorageSyncSetBansPacket
{
    public StorageSyncSetBansPacket() { }

    public StorageSyncSetBansPacket(int storageIndex, int planetId, int bans)
    {
        StorageIndex = storageIndex;
        PlanetId = planetId;
        Bans = bans;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StorageIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Bans { get; set; }
}
