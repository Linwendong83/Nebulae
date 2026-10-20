namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(9279229608776850064UL)]
internal class EntityBoostSwitchPacket
{
    public EntityBoostSwitchPacket() { }

    public EntityBoostSwitchPacket(int planetId, EBoostEntityType entityType, int id, bool enable)
    {
        PlanetId = planetId;
        EntityType = entityType;
        Id = id;
        Enable = enable;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public EBoostEntityType EntityType { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Id { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public bool Enable { get; set; }
}

internal enum EBoostEntityType
{
    ArtificialStar,
    Ejector,
    Silo
}
