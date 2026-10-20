namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(11480069122597674715UL)]
internal class EntityWarningSwitchPacket
{
    public EntityWarningSwitchPacket() { }

    public EntityWarningSwitchPacket(int planetId, int entityId, bool enable)
    {
        PlanetId = planetId;
        EntityId = entityId;
        Enable = enable;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int EntityId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool Enable { get; set; }
}
